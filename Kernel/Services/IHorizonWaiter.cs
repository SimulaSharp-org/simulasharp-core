namespace SimulaSharp.Kernel.Services;

/// <summary>
/// A site that asks the clock to wake it at a precise horizon. The <see cref="SimClock"/> is the sole,
/// continuously advancing horizon writer, so it can release a parked site the instant the horizon
/// reaches <see cref="NextWakeUs"/> — giving sub-microsecond wake precision without the site
/// busy-spinning and without depending on OS timer granularity (a stock Windows timed wait can be off
/// by ~15 ms). Sites register with <see cref="SimClock.RegisterWaiter"/> while running.
/// </summary>
internal interface IHorizonWaiter
{
    /// <summary>
    /// Absolute sim-time (µs) this site wants to be woken at, or <see cref="long.MaxValue"/> when it is
    /// not waiting on a timed event (parked for another reason, or spinning). Read by the clock thread
    /// with volatile semantics; written by the site thread before it parks.
    /// </summary>
    long NextWakeUs { get; }

    /// <summary>
    /// Called by the clock thread once the horizon reaches <see cref="NextWakeUs"/>: clears the request
    /// (so it is not signalled again) and releases the site's run loop.
    /// </summary>
    void WakeFromClock();
}
