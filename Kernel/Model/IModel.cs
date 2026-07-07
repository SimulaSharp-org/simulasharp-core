using SimulaSharp.Kernel.Logging;
using SimulaSharp.Kernel.Services;

namespace SimulaSharp.Kernel.Model;

/// <summary>
/// A simulation model — a simulated entity with state and behaviour. The framework
/// injects the clock, scheduler and logger as construction arguments, and every
/// generated <c>&lt;Model&gt;Ref</c> wraps an <see cref="IModel"/>. A model's whole
/// containment subtree shares one <see cref="Scheduler"/>. Deinitialization happens
/// in <see cref="IDisposable.Dispose"/>.
/// </summary>
public interface IModel : IDisposable
{
    /// <summary>The shared simulation clock (construction argument).</summary>
    public IClock TimeKeeper { get; }

    /// <summary>The SimSite this model and its containment subtree run on (construction argument).</summary>
    public IScheduler Scheduler { get; }

    /// <summary>This model's component-scoped logger (construction argument).</summary>
    public ILogger Logger { get; }

    /// <summary>
    /// Called by the framework after construction, assembly wiring and config filling
    /// are complete. Here the model registers its own long-running events on the
    /// scheduler (if any), precomputes values and does other setup work.
    /// </summary>
    public void Configure();
}
