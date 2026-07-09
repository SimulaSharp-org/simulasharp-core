using SimulaSharp.Kernel.Logging;
using SimulaSharp.Kernel.Services;

namespace SimulaSharp.Kernel.Model;

/// <summary>
/// A simulation service — a reusable capability that models depend on. A service runs on its
/// <b>own</b> site, so every model↔service call is cross-thread. There are two kinds:
/// <see cref="ServiceBase"/> (sim-paced, on a <see cref="Services.SimSite"/>) and
/// <see cref="LooselyCoupledServiceBase"/> (clock-ungated, for external I/O). Both expose their site
/// as a <see cref="IDispatcherSite"/> — enough for the simulator to run it and for the interaction
/// plumbing to marshal onto it. The framework injects the clock and logger as construction arguments;
/// deinitialization happens in <see cref="IDisposable.Dispose"/>.
/// </summary>
public interface IService : IDisposable
{
    /// <summary>The shared simulation clock — read/observe (construction argument).</summary>
    public IClock TimeKeeper { get; }

    /// <summary>This service's component-scoped logger (construction argument).</summary>
    public ILogger Logger { get; }

    /// <summary>
    /// The service's own site (marshalling + lifecycle). Every call into the service is marshalled
    /// onto it; the simulator starts/stops/disposes it. A <see cref="ServiceBase"/> additionally
    /// exposes it as an <see cref="IScheduler"/> for its own sim-time work.
    /// </summary>
    public IDispatcherSite Site { get; }

    /// <summary>
    /// Called by the framework after construction, assembly wiring and config filling
    /// are complete. Here the service registers its own long-running events (if any),
    /// precomputes values and does other setup work.
    /// </summary>
    public void Configure();
}
