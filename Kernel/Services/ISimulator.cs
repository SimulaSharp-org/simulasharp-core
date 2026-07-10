using SimulaSharp.Kernel.Knobs;
using SimulaSharp.Kernel.Logging;
using SimulaSharp.Kernel.Parameters;
using SimulaSharp.Kernel.Plotting;

namespace SimulaSharp.Kernel.Services;

/// <summary>
/// The control endpoint for a whole simulation. A generated
/// <c>&lt;SimName&gt;Simulator</c> implements this (over <c>SimulatorBase</c>). The
/// driver (MMI or CLI) walks the lifecycle: constructing the simulator <b>assembles</b> the topology, then
/// register any log sinks, optionally tune parameters (<see cref="GetParams"/>/<see cref="SetParam"/>), run
/// <see cref="Configure"/> (each model's setup), then <see cref="Start"/>/<see cref="Stop"/>.
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

    /// <summary>The discovered knobs across every model instance (populated when the simulator is constructed).</summary>
    IReadOnlyList<Knob> Knobs { get; }

    /// <summary>
    /// Registers a knob channel (the MMI). It receives the discovered knobs and a push
    /// on every value change. Knobs are discovered at construction, so a channel registered any time
    /// receives them immediately.
    /// </summary>
    void RegisterKnobChannel(IKnobChannel channel);

    /// <summary>
    /// The discovered plot-telemetry points across every model instance (populated when the simulator
    /// is constructed), keyed by full dotted instance path. Sample them in one
    /// mission-time-stamped sweep via <see cref="SamplePlottables"/>.
    /// </summary>
    IReadOnlyDictionary<string, Plottable> Plottables { get; }

    /// <summary>
    /// Samples every plottable in one sweep, stamped with a single mission time read once, so
    /// all points share a consistent x-axis. The plot consumer (MMI) calls this at its own cadence.
    /// </summary>
    /// <returns>The mission time (µs) of the sweep and a path → value snapshot.</returns>
    (long MissionTimeUs, IReadOnlyDictionary<string, double> Values) SamplePlottables();

    /// <summary>
    /// The discovered build-time parameters across every model and service instance (populated when the
    /// simulator is constructed), keyed by full dotted instance path. Read a value, or override it with
    /// <see cref="SetParam"/> — but only <b>before</b> <see cref="Configure"/>.
    /// </summary>
    IReadOnlyList<ParameterInfo> GetParams();

    /// <summary>
    /// Overrides a parameter's value before the run is configured. Throws if called after
    /// <see cref="Configure"/> (parameters are fixed for the run). <paramref name="value"/> must be the
    /// parameter's declared type (boxed).
    /// </summary>
    void SetParam(ParameterInfo parameter, object value);

    /// <summary>Runs every model's and service's <c>Configure()</c> depth-first. Call after tuning parameters.</summary>
    void Configure();

    /// <summary>Starts or resumes the run.</summary>
    void Start();

    /// <summary>Freezes the run; resume with <see cref="Start"/>.</summary>
    void Stop();

    /// <summary>
    /// Runs for <paramref name="durationUs"/> µs of sim time from now, then freezes — blocking the
    /// caller until it does. A bounded real-time run (time still advances at 1:1), for scripted runs
    /// and tests, so a host doesn't need the concrete clock. Requires <see cref="Start"/>-ability.
    /// </summary>
    void RunFor(long durationUs);
}
