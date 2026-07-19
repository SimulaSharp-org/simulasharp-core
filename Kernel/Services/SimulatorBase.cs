using SimulaSharp.Kernel.Knobs;
using SimulaSharp.Kernel.Logging;
using SimulaSharp.Kernel.Model;
using SimulaSharp.Kernel.Parameters;
using SimulaSharp.Kernel.Plotting;

namespace SimulaSharp.Kernel.Services;

/// <summary>
/// The reusable machinery every simulation shares, and the base the generated
/// <c>&lt;SimName&gt;Simulator</c> extends. It owns the <see cref="SimClock"/>, the
/// SimSites and the constructed models/services, fans log entries out to the
/// registered sinks, and implements the <see cref="ISimulator"/> lifecycle.
/// <para>
/// The generated subclass supplies the topology <b>in its constructor</b>, using the protected building blocks
/// below (create sites, mint loggers, construct and register components, apply parameters, wire references,
/// discover knobs/plottables/parameters), then calls <see cref="OnAssembled"/>. So a freshly-<c>new</c>'d
/// simulator is already assembled — the host may read/override parameters via <see cref="GetParams"/>/
/// <see cref="SetParam"/>, then <see cref="Configure"/> and <see cref="Start"/>.
/// </para>
/// </summary>
public abstract class SimulatorBase : ISimulator
{
    private readonly SimClock _clock;
    private readonly LogRouter _router = new();
    private readonly List<ISite> _sites = [];
    private readonly List<IModel> _models = [];
    private readonly List<IService> _services = [];
    private readonly List<object> _events = [];   // global events — rooted for the run's lifetime (see RegisterEvent)
    private readonly List<Knob> _knobs = [];
    private readonly List<IKnobChannel> _knobChannels = [];
    private readonly Dictionary<string, Plottable> _plottables = [];
    private readonly List<ParameterInfo> _parameters = [];

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

    /// <inheritdoc/>
    public IReadOnlyList<ParameterInfo> GetParams() => _parameters;

    /// <inheritdoc/>
    public void SetParam(ParameterInfo parameter, object value)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (_state >= SimulatorState.Configured)
            throw new InvalidOperationException(
                "Parameters can only be set before Configure() — they are fixed once the run is configured.");
        parameter.Write(value);
    }

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
    protected SimSite CreateSite(string name, int maxEvents = 64)
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

    /// <summary>Collects an instance's parameters (if any) into the simulation's list, stamped with its full path.
    /// Works for both models and services (either may declare <c>[Parameter]</c> members).</summary>
    protected void CollectParametersFrom(object instance, string fullPath) =>
        (instance as IParameterProvider)?.CollectParameters(fullPath, _parameters);

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
    /// Registers a clock-ungated (loosely-coupled) service — adopting its
    /// <see cref="LooselyCoupledSite"/> into the simulation's lifecycle (started, stopped, and
    /// disposed with the run) and its <c>Configure</c>/<c>Dispose</c> hooks.
    /// </summary>
    protected T RegisterLooselyCoupledService<T>(T service) where T : LooselyCoupledServiceBase
    {
        _services.Add(service);
        _sites.Add(service.Site);
        return service;
    }

    /// <summary>
    /// Roots a global event (an <see cref="SimulaSharp.Kernel.Events.ISimEvent{TSub}"/> instance) for the simulation's
    /// lifetime. Events have no site or lifecycle of their own — they are passive fan-out channels — but must stay
    /// reachable while producers (<c>[EventReference]</c>) and subscribers hold them, hence this anchor.
    /// </summary>
    protected T RegisterEvent<T>(T @event) where T : class
    {
        _events.Add(@event);
        return @event;
    }

    /// <summary>
    /// Called by the generated constructor once the topology is fully assembled (sites, model/service tree,
    /// applied parameters, wired references, discovered knobs/plottables/parameters). Marks the simulator
    /// ready and hands any registered knob channels the discovered knobs. After this, the host may tune
    /// parameters (<see cref="GetParams"/>/<see cref="SetParam"/>) and then <see cref="Configure"/>.
    /// </summary>
    protected void OnAssembled()
    {
        if (_state < SimulatorState.Initialized) _state = SimulatorState.Initialized;
        foreach (var channel in _knobChannels) AttachChannel(channel);
    }

    // ── ISimulator lifecycle ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Configure()
    {
        if (_state < SimulatorState.Initialized) throw new InvalidOperationException("The simulator is not assembled.");
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

    /// <inheritdoc/>
    public void RunFor(long durationUs) => _clock.RunFor(durationUs);

    /// <summary>
    /// Tears the simulation down (single-use, no reset). Order matters for the park-on-idle sites:
    /// the clock thread wakes a site by <c>Set()</c>ting its wait handle (per-tick horizon wakes and the
    /// stop handler), so it must be fully stopped before those handles are disposed. So: pause the clock
    /// (park its thread, and fire the stop handlers under control), stop every site thread, run each
    /// service's then each model's deinit while nothing is executing, <b>dispose (join) the clock</b>,
    /// then — only now that no thread can touch them — dispose the site wait handles.
    /// </summary>
    public void Dispose()
    {
        _clock.Pause();                                              // park the clock thread; no more per-tick wakes
        foreach (var site in _sites) site.Stop();                   // join each site thread; unregister its waiter
        foreach (var service in _services) service.Dispose();
        for (int i = _models.Count - 1; i >= 0; i--) _models[i].Dispose();
        _clock.Dispose();                                            // JOIN the clock thread before the next line
        foreach (var site in _sites) site.Dispose();                // safe now: nothing can Set() these handles
    }
}
