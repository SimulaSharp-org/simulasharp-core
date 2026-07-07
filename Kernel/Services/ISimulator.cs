namespace SimulaSharp.Kernel.Services;

/// <summary>
/// The control endpoint for a whole simulation. A generated
/// <c>&lt;SimName&gt;Simulator</c> implements this (over the shared machinery in a
/// <c>SimulatorBase</c>): by the time an instance is handed out it has already
/// constructed the SimSites and model tree, wired the references, filled the
/// config values, and run each model's <c>Configure()</c> depth-first. The MMI or
/// CLI then drives the run through this surface.
/// <para>
/// Single-use: there is no reset — <see cref="IDisposable.Dispose"/> tears the
/// simulation down; create a new instance to start over.
/// </para>
/// </summary>
public interface ISimulator : IDisposable
{
    /// <summary>
    /// The simulation clock — used to read time (<see cref="IClock.SimNowUs"/>,
    /// <see cref="IClock.MissionNowUs"/>, <see cref="IClock.Now"/>) and to observe
    /// run lifecycle (<see cref="IClock.Started"/>/<see cref="IClock.Stopped"/>).
    /// Drive the run through <see cref="Start"/>/<see cref="Pause"/> below, not the
    /// clock's own controls.
    /// </summary>
    IClock Clock { get; }

    /// <summary>True while the simulation is running (started and not paused).</summary>
    bool IsRunning { get; }

    /// <summary>Starts or resumes the simulation.</summary>
    void Start();

    /// <summary>Pauses (freezes) the simulation; resume with <see cref="Start"/>.</summary>
    void Pause();
}
