using SimulaSharp.Kernel.Knobs;
using SimulaSharp.Kernel.Logging;
using SimulaSharp.Kernel.Model;
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
    : IModel, IKnobProvider, IPlottableProvider
{
    private int _speed;
    private double _load;
    private Knob? _speedKnob;

    public IClock TimeKeeper { get; } = clock;
    public IScheduler Scheduler { get; } = scheduler;
    public ILogger Logger { get; } = logger;

    public bool Configured { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>The discovered knob for <c>Speed</c> (null until <see cref="CollectKnobs"/> runs).</summary>
    public Knob? SpeedKnob => _speedKnob;

    public double Load { set => Volatile.Write(ref _load, value); }

    public void Configure() => Configured = true;
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

/// <summary>A concrete <see cref="SimulatorBase"/> wired by hand (no generated OnInitialize).</summary>
internal sealed class ProbeSimulator() : SimulatorBase(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc))
{
    /// <summary>The single model built in <see cref="OnInitialize"/> (exposed for assertions).</summary>
    public ProbeModel? Model { get; private set; }

    protected override void OnInitialize()
    {
        var site = CreateSite("Probe");
        Model = Register(new ProbeModel(Clock, site, CreateLogger("Sim.Probe")));
        CollectKnobsFrom(Model, "Sim.Probe");
        CollectPlottablesFrom(Model, "Sim.Probe");
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
