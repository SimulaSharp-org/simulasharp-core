using SimulaSharp.Kernel.Logging;
using SimulaSharp.Kernel.Services;

namespace SimulaSharp.Kernel.Model;

/// <summary>
/// Base class for simulation services. Unlike a model, a service owns its <b>own</b>
/// SimSite (created here from the clock), so every model↔service call is cross-thread
/// and marshalled through that site's inbox. Holds the injected clock and logger and
/// provides the <see cref="Configure"/>/<see cref="Dispose"/> hooks.
/// <para>
/// The service does not start, stop or dispose its site — the simulator owns every
/// site's lifecycle and reaches this one via <see cref="Site"/>. <see cref="Dispose"/>
/// is deinit only (called after the site threads have been stopped).
/// </para>
/// </summary>
public abstract class ServiceBase : IService
{
    private readonly SimSite _site;

    /// <param name="timeKeeper">The shared simulation clock.</param>
    /// <param name="logger">This service's component-scoped logger.</param>
    /// <param name="name">Diagnostic name for the service's SimSite.</param>
    /// <param name="maxEvents">Capacity of the service site's event queue.</param>
    protected ServiceBase(IClock timeKeeper, ILogger logger, string name, int maxEvents = 64)
    {
        TimeKeeper = timeKeeper;
        Logger = logger;
        // The service owns its site; its error channel logs under the service's own logger.
        _site = new SimSite(name, timeKeeper, maxEvents, logger);
    }

    /// <inheritdoc/>
    public IClock TimeKeeper { get; }

    /// <inheritdoc/>
    public ILogger Logger { get; }

    /// <inheritdoc/>
    public IDispatcherSite Site => _site;

    /// <summary>
    /// The service's own sim-paced scheduler — use it to register long-running sim-time work in
    /// <see cref="Configure"/>. (The public <see cref="Site"/> is the same site; this is the
    /// sim-time facet, available only to a <see cref="ServiceBase"/>, not to callers.)
    /// </summary>
    protected IScheduler Scheduler => _site;

    /// <summary>
    /// Called by the framework after construction, assembly and config are complete.
    /// Override to register long-running events on the scheduler or precompute values.
    /// </summary>
    public virtual void Configure() { }

    /// <summary>Deinitialization hook. Override to release resources; the site is disposed by the simulator.</summary>
    public virtual void Dispose() { }
}
