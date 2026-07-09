using SimulaSharp.Kernel.Primitives;

namespace SimulaSharp.Kernel.Services;

/// <summary>
/// The <b>read &amp; observe</b> view of the hard real-time clock — what models and services hold.
/// A single writer thread (the concrete <see cref="SimClock"/>) advances the horizon; observers
/// read it here and may subscribe to <see cref="Started"/>/<see cref="Stopped"/>. Time never runs
/// faster or slower than real time. Control (start/pause/run-for) and disposal live on the concrete
/// <see cref="SimClock"/>, owned by the simulator — so a model cannot pause time or dispose the clock.
/// </summary>
public interface IClock
{
    /// <summary>Elapsed simulation time in microseconds (the volatile horizon).</summary>
    long SimNowUs { get; }

    /// <summary>Absolute mission time in microseconds (mission epoch + <see cref="SimNowUs"/>).</summary>
    long MissionNowUs { get; }

    /// <summary>
    /// Captures sim time and mission time together from a single horizon read, so the
    /// two are guaranteed consistent (unlike reading <see cref="SimNowUs"/> and
    /// <see cref="MissionNowUs"/> in sequence).
    /// </summary>
    TimeSnapshot Now();

    /// <summary>True while time is advancing (i.e. started and not paused).</summary>
    bool IsRunning { get; }

    /// <summary>Raised when the clock starts or resumes.</summary>
    event EventHandler? Started;

    /// <summary>Raised when the clock pauses.</summary>
    event EventHandler? Stopped;
}
