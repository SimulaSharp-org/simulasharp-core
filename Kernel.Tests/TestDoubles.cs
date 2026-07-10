using SimulaSharp.Kernel.Knobs;
using SimulaSharp.Kernel.Logging;
using SimulaSharp.Kernel.Model;
using SimulaSharp.Kernel.Parameters;
using SimulaSharp.Kernel.Plotting;
using SimulaSharp.Kernel.Primitives;
using SimulaSharp.Kernel.Services;

namespace SimulaSharp.Kernel.Tests;

/// <summary>A capturing <see cref="ILogSink"/> — records every entry it receives.</summary>
internal sealed class CapturingSink : ILogSink
{
    private readonly List<LogEntry> _entries = [];
    public IReadOnlyList<LogEntry> Entries { get { lock (_entries) return _entries.ToArray(); } }
    public void Submit(in LogEntry entry) { lock (_entries) _entries.Add(entry); }
}

/// <summary>A minimal <see cref="IModel"/> with no behaviour — for association tests.</summary>
internal sealed class DummyModel : IModel
{
    public IClock TimeKeeper => null!;
    public IScheduler Scheduler => null!;
    public ILogger Logger => null!;
    public void Configure() { }
    public void Dispose() { }
}

/// <summary>The generated-Ref stand-in for <see cref="DummyModel"/> (association tests).</summary>
internal sealed class DummyModelRef(DummyModel model) : IModelRef
{
    public DummyModel Model { get; } = model;
}

/// <summary>
/// A hand-written model that exposes one knob and one plottable — standing in for what the
/// generators would emit, so <see cref="SimulatorBase"/> discovery can be tested without codegen.
/// </summary>
internal sealed class ProbeModel(IClock clock, IScheduler scheduler, ILogger logger)
    : IModel, IKnobProvider, IPlottableProvider, IParameterProvider
{
    private int _speed;
    private double _load;
    private Knob? _speedKnob;

    public IClock TimeKeeper { get; } = clock;
    public IScheduler Scheduler { get; } = scheduler;
    public ILogger Logger { get; } = logger;

    public bool Configured { get; private set; }
    public bool Disposed { get; private set; }
    private int _ticks;

    /// <summary>A build-time parameter (stands in for a <c>[Parameter(100)]</c> member); default 100.</summary>
    public int RatedSpeed { get; set; } = 100;

    public void CollectParameters(string pathPrefix, ICollection<ParameterInfo> into) =>
        into.Add(new ParameterInfo
        {
            Path = pathPrefix + ".RatedSpeed",
            Name = "RatedSpeed",
            ValueType = "int",
            ReadBoxed = () => RatedSpeed,
            WriteBoxed = v => RatedSpeed = (int)v!,
            Default = 100,
        });

    /// <summary>The discovered knob for <c>Speed</c> (null until <see cref="CollectKnobs"/> runs).</summary>
    public Knob? SpeedKnob => _speedKnob;

    public double Load { set => Volatile.Write(ref _load, value); }

    public void Configure()
    {
        Configured = true;
        // A fast periodic so a running clock actively wakes this model's site — exercises the
        // park-on-idle teardown ordering (clock-thread wakes vs. disposing the site's wait handle).
        Scheduler.AddPeriodicEvent(() => Interlocked.Increment(ref _ticks), 1_000);
    }

    public void Dispose() => Disposed = true;

    public void CollectKnobs(string pathPrefix, ICollection<Knob> into)
    {
        _speedKnob = new Knob
        {
            Path = pathPrefix + ".Speed",
            Label = "Speed",
            Kind = KnobKind.Number,
            ValueType = "int",
            ReadBoxed = () => Volatile.Read(ref _speed),
            WriteBoxed = v => Volatile.Write(ref _speed, (int)v!),
        };
        into.Add(_speedKnob);
    }

    public void CollectPlottables(string pathPrefix, IDictionary<string, Plottable> into) =>
        into[pathPrefix + ".Load"] = new Plottable
        {
            Path = pathPrefix + ".Load",
            Label = "Load",
            Unit = "W",
            ValueType = "double",
            Sample = () => Volatile.Read(ref _load),
        };
}

/// <summary>A concrete <see cref="SimulatorBase"/> wired by hand in the constructor (no codegen).</summary>
internal sealed class ProbeSimulator : SimulatorBase
{
    /// <summary>The single model built in the constructor (exposed for assertions).</summary>
    public ProbeModel Model { get; }

    public ProbeSimulator() : base(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc))
    {
        var site = CreateSite("Probe");
        Model = Register(new ProbeModel(Clock, site, CreateLogger("Sim.Probe")));
        CollectKnobsFrom(Model, "Sim.Probe");
        CollectPlottablesFrom(Model, "Sim.Probe");
        CollectParametersFrom(Model, "Sim.Probe");
        OnAssembled();
    }
}

/// <summary>An <see cref="IScheduler"/> that runs marshalled work inline on the caller — for
/// unit-testing components that only <see cref="IScheduler.Post"/> (e.g. service event channels).</summary>
internal sealed class SyncScheduler : IScheduler
{
    public int AddEvent(Action action, long delayUs) => 0;
    public int AddPeriodicEvent(Action action, long periodUs) => 0;
    public int AddCoroutine(Func<IEnumerator<SimYield>> factory, long delayUs) => 0;
    public int AddEventAtMission(Action action, long missionTimeUs) => 0;
    public int AddPeriodicEventAtMission(Action action, long missionTimeUs, long periodUs) => 0;
    public int AddCoroutineAtMission(Func<IEnumerator<SimYield>> factory, long missionTimeUs) => 0;
    public void RemoveEvent(int id) { }
    public void Post(Action action) => action();
    public Task InjectVoidAsync(Action action) { action(); return Task.CompletedTask; }
    public Task<T> InjectAsync<T>(Func<T> action) => Task.FromResult(action());
    public void Dispose() { }
}

/// <summary>A concrete <see cref="ServiceBase"/> for testing its ctor/hooks.</summary>
internal sealed class TestService(IClock clock, ILogger logger) : ServiceBase(clock, logger, "TestService")
{
    public bool Configured { get; private set; }
    public bool Disposed { get; private set; }
    public override void Configure() => Configured = true;
    public override void Dispose() => Disposed = true;
}

/// <summary>Records the knobs it is handed and every change it is notified of.</summary>
internal sealed class RecordingKnobChannel : IKnobChannel
{
    public IReadOnlyList<Knob> Discovered { get; private set; } = [];
    private readonly List<string> _changes = [];
    public IReadOnlyList<string> Changes { get { lock (_changes) return _changes.ToArray(); } }

    public void OnKnobsDiscovered(IReadOnlyList<Knob> knobs) => Discovered = knobs;
    public void OnKnobChanged(Knob knob) { lock (_changes) _changes.Add(knob.Path); }
}
