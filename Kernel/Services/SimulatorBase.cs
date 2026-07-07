using SimulaSharp.Kernel.Logging;
using SimulaSharp.Kernel.Model;

namespace SimulaSharp.Kernel.Services;

/// <summary>
/// The reusable machinery every simulation shares, and the base the generated
/// <c>&lt;SimName&gt;Simulator</c> extends. It owns the <see cref="SimClock"/>, the
/// SimSites and the constructed models/services, fans log entries out to the
/// registered sinks, and implements the <see cref="ISimulator"/> lifecycle.
/// <para>
/// The generated subclass supplies the topology by implementing
/// <see cref="OnInitialize"/> with the protected building blocks below (create sites,
/// mint loggers, construct and register components, wire references, fill config).
/// </para>
/// </summary>
public abstract class SimulatorBase : ISimulator
{
    private readonly SimClock _clock;
    private readonly ILogSink _fanOut;
    private readonly List<SimSite> _sites = [];
    private readonly List<IModel> _models = [];
    private readonly List<IService> _services = [];

    // Copy-on-write sink list: lock-free fan-out reads, locked registration writes.
    private readonly object _sinkLock = new();
    private volatile ILogSink[] _sinks = [];

    private bool _initialized;
    private bool _configured;
    private bool _started;

    /// <param name="missionStart">Absolute wall-clock time mapped to simulation time 0.</param>
    protected SimulatorBase(DateTime missionStart)
    {
        _clock = new SimClock(missionStart);
        _fanOut = new FanOutSink(this);
    }

    /// <inheritdoc/>
    public IClock Clock => _clock;

    /// <inheritdoc/>
    public bool IsRunning => _clock.IsRunning;

    // ── Logging ─────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void RegisterLogSink(ILogSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_sinkLock)
        {
            var updated = new ILogSink[_sinks.Length + 1];
            Array.Copy(_sinks, updated, _sinks.Length);
            updated[_sinks.Length] = sink;
            _sinks = updated;   // volatile publish
        }
    }

    /// <summary>Fans one entry out to every currently-registered sink.</summary>
    private void Fan(in LogEntry entry)
    {
        var sinks = _sinks;   // volatile snapshot
        for (int i = 0; i < sinks.Length; i++)
            sinks[i].Submit(entry);
    }

    /// <summary>The sink handed to component loggers; forwards to <see cref="Fan"/>.</summary>
    private sealed class FanOutSink(SimulatorBase owner) : ILogSink
    {
        public void Submit(in LogEntry entry) => owner.Fan(entry);
    }

    // ── Building blocks for the generated subclass ──────────────────────────────

    /// <summary>Creates and registers a root SimSite (one per assembly <c>Sites[]</c> entry).</summary>
    protected SimSite CreateSite(string name, int maxEvents = 1024)
    {
        var site = new SimSite(name, _clock, maxEvents);
        _sites.Add(site);
        return site;
    }

    /// <summary>Creates a component-scoped logger over this simulation's clock and sink fan-out.</summary>
    protected ILogger CreateLogger(string issuer) => new ComponentLogger(issuer, _clock, _fanOut);

    /// <summary>Registers a constructed model so it is Configured and Disposed with the simulation.</summary>
    protected T Register<T>(T model) where T : IModel
    {
        _models.Add(model);
        return model;
    }

    /// <summary>Registers a service, adopting its own SimSite into the simulation's lifecycle.</summary>
    protected T RegisterService<T>(T service) where T : ServiceBase
    {
        _services.Add(service);
        _sites.Add(service.Site);
        return service;
    }

    /// <summary>
    /// Implemented by the generated subclass: construct the SimSites and model tree,
    /// wire references, and fill config values, using the protected building blocks.
    /// </summary>
    protected abstract void OnInitialize();

    // ── ISimulator lifecycle ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Initialize()
    {
        if (_initialized) return;
        OnInitialize();
        _initialized = true;
    }

    /// <inheritdoc/>
    public void Configure()
    {
        if (!_initialized) throw new InvalidOperationException("Initialize() must run before Configure().");
        if (_configured) return;
        foreach (var service in _services) service.Configure();
        foreach (var model in _models) model.Configure();
        _configured = true;
    }

    /// <inheritdoc/>
    public void Start()
    {
        if (!_configured) throw new InvalidOperationException("Configure() must run before Start().");
        if (!_started)
        {
            foreach (var site in _sites) site.Start();
            _started = true;
        }
        _clock.Start();
    }

    /// <inheritdoc/>
    public void Stop() => _clock.Pause();

    /// <summary>
    /// Tears the simulation down (single-use, no reset): stop every site thread, run
    /// each service's then each model's deinit while nothing is executing, then
    /// dispose the sites and the clock.
    /// </summary>
    public void Dispose()
    {
        foreach (var site in _sites) site.Stop();
        foreach (var service in _services) service.Dispose();
        for (int i = _models.Count - 1; i >= 0; i--) _models[i].Dispose();
        foreach (var site in _sites) site.Dispose();
        _clock.Dispose();
    }
}
