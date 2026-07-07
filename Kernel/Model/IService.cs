using SimulaSharp.Kernel.Logging;
using SimulaSharp.Kernel.Services;

namespace SimulaSharp.Kernel.Model;

/// <summary>
/// A simulation service — a reusable capability that models depend on. A service runs
/// on its <b>own</b> SimSite (created behind the scenes by the abstract service base
/// from the clock), so every model↔service call is cross-thread. The framework injects
/// the clock and logger as construction arguments. Deinitialization happens in
/// <see cref="IDisposable.Dispose"/>.
/// </summary>
public interface IService : IDisposable
{
    /// <summary>The shared simulation clock (construction argument).</summary>
    public IClock TimeKeeper { get; }

    /// <summary>This service's component-scoped logger (construction argument).</summary>
    public ILogger Logger { get; }

    /// <summary>
    /// The service's own SimSite, created behind the scenes by the abstract service
    /// base with the clock as its time source. Every call into the service is
    /// marshalled onto this site.
    /// </summary>
    public IScheduler Scheduler { get; }

    /// <summary>
    /// Called by the framework after construction, assembly wiring and config filling
    /// are complete. Here the service registers its own long-running events on the
    /// scheduler (if any), precomputes values and does other setup work.
    /// </summary>
    public void Configure();
}
