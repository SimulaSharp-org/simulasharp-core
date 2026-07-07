using SimulaSharp.Kernel.Primitives;

namespace SimulaSharp.Kernel.Services;

/// <summary>
/// The scheduling surface a model uses to run work on its own SimSite. Delays are
/// relative to the clock's current time. Actions receive no time argument — read
/// the clock (<see cref="IClock.SimNowUs"/>) when "now" is needed.
/// </summary>
public interface IScheduler : IDisposable
{
    /// <summary>Schedules a one-shot action to run <paramref name="delayUs"/> µs from now.</summary>
    /// <returns>The event id, or -1 if the queue is full.</returns>
    int AddEvent(Action action, long delayUs);

    /// <summary>Schedules a repeating action every <paramref name="periodUs"/> µs (first run after that period).</summary>
    /// <returns>The event id, or -1 if the queue is full.</returns>
    int AddPeriodicEvent(Action action, long periodUs);

    /// <summary>
    /// Schedules a coroutine process, first stepped <paramref name="delayUs"/> µs from now;
    /// the <paramref name="factory"/> is materialized at that first step and each yield
    /// re-schedules the next step by its delay.
    /// </summary>
    /// <returns>The event id, or -1 if the queue is full.</returns>
    int AddCoroutine(Func<IEnumerator<SimYield>> factory, long delayUs);

    /// <summary>Cancels a pending event by id; no effect if it has already fired or the id is unknown.</summary>
    void RemoveEvent(int id);

    /// <summary>Fire-and-forget cross-thread work, run on this site's thread during its next drain.</summary>
    void Post(Action action);

    /// <summary>
    /// Runs cross-thread work on this site's thread; the returned task completes when it
    /// finishes (or faults if it throws).
    /// </summary>
    Task InjectVoidAsync(Action action);

    /// <summary>
    /// Runs a cross-thread function on this site's thread; the returned task yields its
    /// result (or faults if it throws).
    /// </summary>
    Task<T> InjectAsync<T>(Func<T> action);
}
