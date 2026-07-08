using SimulaSharp.Kernel.Knobs;
using SimulaSharp.Kernel.Logging;
using SimulaSharp.Kernel.Model;
using SimulaSharp.Kernel.Plotting;

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
    private readonly LogRouter _router = new();
    private readonly List<SimSite> _sites = [];
    private readonly List<IModel> _models = [];
    private readonly List<IService> _services = [];
    private readonly List<Knob> _knobs = [];
    private readonly List<IKnobChannel> _knobChannels = [];
    private readonly Dictionary<string, Plottable> _plottables = [];

    private SimulatorState _state = SimulatorState.Initial;

    /// <param name="missionStart">Absolute wall-clock time mapped to simulation time 0.</param>
    protected SimulatorBase(DateTime missionStart)
    {
        _clock = new SimClock(missionStart);
    }

    /// <inheritdoc/>
    public IClock Clock => _clock;

    /// <inheritdoc/>
    public SimulatorState State => _state;

    /// <inheritdoc/>
    public bool IsRunning => _clock.IsRunning;

    /// <inheritdoc/>
    public IReadOnlyList<Knob> Knobs => _knobs;

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, Plottable> Plottables => _plottables;

    // ── Logging ─────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void RegisterLogSink(ILogSink sink) => _router.Add(sink);

    // ── Knobs ───────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void RegisterKnobChannel(IKnobChannel channel)
    {
        _knobChannels.Add(channel);
        if (_state >= SimulatorState.Initialized) AttachChannel(channel);
    }

    /// <summary>Hands the channel the discovered knobs and subscribes it to each knob's changes.</summary>
    private void AttachChannel(IKnobChannel channel)
    {
        channel.OnKnobsDiscovered(_knobs);
        foreach (var k in _knobs)
        {
            var knob = k;
            knob.Changed += () => channel.OnKnobChanged(knob);
        }
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
    protected ILogger CreateLogger(string issuer) => new ComponentLogger(issuer, _clock, _router);

    /// <summary>Collects a model's knobs (if any) into the simulation's cached list, stamped with its full path.</summary>
    protected void CollectKnobsFrom(IModel model, string fullPath) =>
        (model as IKnobProvider)?.CollectKnobs(fullPath, _knobs);

    /// <summary>Collects a model's plottables (if any) into the simulation's map, keyed by full path.</summary>
    protected void CollectPlottablesFrom(IModel model, string fullPath) =>
        (model as IPlottableProvider)?.CollectPlottables(fullPath, _plottables);

    /// <summary>
    /// Samples every plottable in one sweep, stamped with a single mission time
    /// (<see cref="IClock.MissionNowUs"/>) read once — so all points in the returned frame
    /// share a consistent x-axis. The plot consumer (MMI) calls this at its own cadence.
    /// </summary>
    /// <returns>The mission time (µs) of the sweep and a path → value snapshot.</returns>
    public (long MissionTimeUs, IReadOnlyDictionary<string, double> Values) SamplePlottables()
    {
        long missionTimeUs = _clock.MissionNowUs;
        var values = new Dictionary<string, double>(_plottables.Count);
        foreach (var kv in _plottables) values[kv.Key] = kv.Value.Sample();
        return (missionTimeUs, values);
    }

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
        if (_state >= SimulatorState.Initialized) return;
        OnInitialize();
        _state = SimulatorState.Initialized;
        foreach (var channel in _knobChannels) AttachChannel(channel);
    }

    /// <inheritdoc/>
    public void Configure()
    {
        if (_state < SimulatorState.Initialized) throw new InvalidOperationException("Initialize() must run before Configure().");
        if (_state >= SimulatorState.Configured) return;
        foreach (var service in _services) service.Configure();
        foreach (var model in _models) model.Configure();
        _state = SimulatorState.Configured;
    }

    /// <inheritdoc/>
    public void Start()
    {
        if (_state < SimulatorState.Configured) throw new InvalidOperationException("Configure() must run before Start().");
        if (_state < SimulatorState.Running)
        {
            foreach (var site in _sites) site.Start();
            _state = SimulatorState.Running;
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
