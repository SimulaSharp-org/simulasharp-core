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

    /// <summary>Raised when the clock starts or resumes.</summary>
    event EventHandler? Started;

    /// <summary>Raised when the clock pauses.</summary>
    event EventHandler? Stopped;
}
