using SimulaSharp.Kernel.Primitives;

namespace SimulaSharp.Kernel.Services;

/// <summary>
/// Hard real-time clock owned by a <see cref="Simulator"/>. A single writer thread
/// advances the horizon; observers (SimSites) read it. Time never runs faster or
/// slower than real time — <see cref="Pause"/> freezes it, <see cref="Start"/>
/// resumes it. There is no reset: create a new simulation instead.
/// </summary>
public interface IClock : IDisposable
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

    /// <summary>Start or resume the clock. Raises <see cref="Started"/>.</summary>
    void Start();

    /// <summary>Freeze the horizon. Raises <see cref="Stopped"/>.</summary>
    void Pause();

    /// <summary>
    /// Arms an automatic <see cref="Pause"/> for when the horizon first reaches
    /// <paramref name="simTimeUs"/>; pass a negative value to disarm. A manual
    /// <see cref="Pause"/> also disarms it (so a user freeze is never silently re-armed).
    /// Time still advances continuously at 1:1 up to the target — this is a stop boundary,
    /// not a jump — so overshoot is bounded by the clock's spin granularity.
    /// </summary>
    void PauseAtSimUs(long simTimeUs);

    /// <summary>
    /// Runs for <paramref name="durationUs"/> µs of sim time from now, then pauses,
    /// <em>blocking the caller</em> until that pause fires. If the clock is not already
    /// running it is started; a non-positive duration pauses immediately. Because time
    /// advances at real time, this call takes roughly <paramref name="durationUs"/> of
    /// wall-clock time — it is a controlled real-time run, not a fast-forward.
    /// </summary>
    void RunFor(long durationUs);

    /// <summary>Raised when the clock starts or resumes.</summary>
    event EventHandler? Started;

    /// <summary>Raised when the clock pauses.</summary>
    event EventHandler? Stopped;
}
