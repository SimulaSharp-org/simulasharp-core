using SimulaSharp.Kernel.Logging;
using SimulaSharp.Kernel.Services;

namespace SimulaSharp.Kernel.Model;

/// <summary>
/// Base class for simulation models. Holds the construction arguments the framework
/// injects (clock, scheduler, logger) and provides the <see cref="Configure"/> and
/// <see cref="Dispose"/> hooks. A model's whole containment subtree shares one
/// <see cref="Scheduler"/> (SimSite): the generated simulator constructs every model
/// in the subtree with that shared scheduler, so there is nothing to propagate here.
/// </summary>
public abstract class ModelBase : IModel
{
    /// <param name="timeKeeper">The shared simulation clock.</param>
    /// <param name="scheduler">The SimSite this model (and its subtree) runs on.</param>
    /// <param name="logger">This model's component-scoped logger.</param>
    protected ModelBase(IClock timeKeeper, IScheduler scheduler, ILogger logger)
    {
        TimeKeeper = timeKeeper;
        Scheduler = scheduler;
        Logger = logger;
    }

    /// <inheritdoc/>
    public IClock TimeKeeper { get; }

    /// <inheritdoc/>
    public IScheduler Scheduler { get; }

    /// <inheritdoc/>
    public ILogger Logger { get; }

    /// <summary>
    /// Called by the framework after construction, assembly and config are complete.
    /// Override to register long-running events on the scheduler, precompute values,
    /// or do other setup. The default does nothing.
    /// </summary>
    public virtual void Configure() { }

    /// <summary>
    /// Deinitialization hook. Override to release resources; the default does nothing.
    /// Called during the simulation's teardown after the site threads have stopped.
    /// </summary>
    public virtual void Dispose() { }
}
