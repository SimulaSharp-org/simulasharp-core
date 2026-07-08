using SimulaSharp.Kernel.Knobs;
using SimulaSharp.Kernel.Logging;

namespace SimulaSharp.Kernel.Services;

/// <summary>
/// The control endpoint for a whole simulation. A generated
/// <c>&lt;SimName&gt;Simulator</c> implements this (over <c>SimulatorBase</c>). The
/// driver (MMI or CLI) walks the lifecycle: register any log sinks, then
/// <see cref="Initialize"/> (build the topology), <see cref="Configure"/> (run each
/// model's setup), then <see cref="Start"/>/<see cref="Stop"/>.
/// <para>Single-use: there is no reset — <see cref="IDisposable.Dispose"/> tears it down.</para>
/// </summary>
public interface ISimulator : IDisposable
{
    /// <summary>The simulation clock — read time and observe the run lifecycle.</summary>
    IClock Clock { get; }

    /// <summary>The current lifecycle state (Initial → Initialized → Configured → Running).</summary>
    SimulatorState State { get; }

    /// <summary>True while the simulation is running (started and not stopped).</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Registers a log destination. May be called at any time, including while the
    /// simulation is running; entries produced after registration fan out to it.
    /// </summary>
    void RegisterLogSink(ILogSink sink);

    /// <summary>The discovered knobs across every model instance (populated by <see cref="Initialize"/>).</summary>
    IReadOnlyList<Knob> Knobs { get; }

    /// <summary>
    /// Registers a knob channel (the MMI). It receives the discovered knobs and a push
    /// on every value change. May be registered before or after <see cref="Initialize"/>.
    /// </summary>
    void RegisterKnobChannel(IKnobChannel channel);

    /// <summary>Builds the topology: constructs the SimSites and model tree, wires references, fills config.</summary>
    void Initialize();

    /// <summary>Runs every model's and service's <c>Configure()</c> depth-first. Call after <see cref="Initialize"/>.</summary>
    void Configure();

    /// <summary>Starts or resumes the run.</summary>
    void Start();

    /// <summary>Freezes the run; resume with <see cref="Start"/>.</summary>
    void Stop();
}
