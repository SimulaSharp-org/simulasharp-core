namespace SimulaSharp.Kernel.Services;

/// <summary>
/// The lifecycle surface of a scheduling domain — its thread's start/stop/teardown, driven by the
/// <see cref="SimulatorBase"/>. Deliberately separate from the "use" surfaces (<see cref="IDispatcher"/>,
/// <see cref="IScheduler"/>): a model holds an <see cref="IScheduler"/> and so <b>cannot</b> start,
/// stop, or dispose the thread it runs on — only the simulator, which holds the site, can.
/// </summary>
public interface ISite : IDisposable
{
    /// <summary>Human-readable site name (used for the thread name and diagnostics).</summary>
    string Name { get; }

    /// <summary>Starts the site's thread. Idempotent.</summary>
    void Start();

    /// <summary>Stops the site's thread and joins it.</summary>
    void Stop();
}
