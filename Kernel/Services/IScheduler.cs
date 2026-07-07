using SimulaSharp.Kernel.Primitives;

namespace SimulaSharp.Kernel.Services;

/// <summary>
/// The scheduling surface a model uses to run work on its own SimSite. Delays are
/// relative to the clock's current time. Actions receive no time argument — read
/// the clock (<see cref="IClock.SimNowUs"/>) when "now" is needed.
/// </summary>
public interface IScheduler : IDisposable
{
    // One-shot and periodic actions
    int AddEvent(Action action, long delayUs);
    int AddPeriodicEvent(Action action, long periodUs);

    // Coroutine-based process (materialized on first step)
    int AddCoroutine(Func<IEnumerator<SimYield>> factory, long delayUs);

    // Removal
    void RemoveEvent(int id);

    // Cross-thread communication (runs on the target site's thread)
    void Post(Action action);
    Task InjectVoidAsync(Action action);
    Task<T> InjectAsync<T>(Func<T> action);
}
