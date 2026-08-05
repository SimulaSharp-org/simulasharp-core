using SimulaSharp.Kernel.Primitives;

namespace SimulaSharp.Kernel.Services;

/// <summary>
/// The <b>read &amp; observe</b> view of the hard real-time clock — what models and services hold.
/// A single writer thread (the concrete <see cref="SimClock"/>) advances the horizon; observers
/// read it here and may subscribe to <see cref="Started"/>/<see cref="Stopped"/>. Time never runs
/// faster or slower than real time. Control (start/pause/run-for) and disposal live on the concrete
/// <see cref="SimClock"/>, owned by the simulator — so a model cannot pause time or dispose the clock.
/// (A component may, however, install a <see cref="IMissionTimeProvider"/> via
/// <see cref="SetMissionTimeProvider"/>: that re-points the mission-time label without touching the horizon.)
/// </summary>
public interface IClock
{
    /// <summary>Elapsed simulation time in microseconds (the volatile horizon).</summary>
    long SimNowUs { get; }

    /// <summary>
    /// Absolute mission time in microseconds. By default this is the mission epoch + <see cref="SimNowUs"/>
    /// (mission time pinned to real time); when an <see cref="IMissionTimeProvider"/> is installed via
    /// <see cref="SetMissionTimeProvider"/> it is whatever that provider returns.
    /// </summary>
    long MissionNowUs { get; }

    /// <summary>
    /// Captures sim time (a single horizon read) together with mission time. With the default provider the pair is
    /// consistent (mission = epoch + sim); with a custom <see cref="IMissionTimeProvider"/> installed, mission comes
    /// from that provider — sim and mission are then independent clocks sampled together, not one derived from the other.
    /// </summary>
    TimeSnapshot Now();

    /// <summary>
    /// Installs (or clears, when <paramref name="provider"/> is null) the <see cref="IMissionTimeProvider"/> that
    /// backs <see cref="MissionNowUs"/> and the mission field of <see cref="Now"/>. With no provider the framework
    /// default applies (mission epoch + horizon — mission time pinned to real time). Set it from within the
    /// simulation — e.g. an FES service in its <c>Configure()</c> — to make an external or fast-forwarded clock the
    /// mission time every component reads. Thread-safe: a single volatile reference swap. Unlike start/pause it does
    /// <b>not</b> touch <see cref="SimNowUs"/> or the horizon's advance; it only re-points the mission-time label.
    /// </summary>
    void SetMissionTimeProvider(IMissionTimeProvider? provider);

    /// <summary>True while time is advancing (i.e. started and not paused).</summary>
    bool IsRunning { get; }

    /// <summary>Raised when the clock starts or resumes.</summary>
    event EventHandler? Started;

    /// <summary>Raised when the clock pauses.</summary>
    event EventHandler? Stopped;
}
