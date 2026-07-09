using SimulaSharp.Kernel.Primitives;

namespace SimulaSharp.Kernel.Services;

/// <summary>
/// The sim-time scheduling surface a model uses to run work on its own SimSite. Extends
/// <see cref="IDispatcher"/> (cross-thread marshalling) with a queue of work ordered by simulation
/// time. Delays are relative to the clock's current time; actions receive no time argument — read
/// the clock (<see cref="IClock.SimNowUs"/>) when "now" is needed.
/// <para>
/// It carries no lifecycle: a model holds an <see cref="IScheduler"/> and so cannot start, stop, or
/// dispose the site it runs on — that is the simulator's job (via <see cref="ISite"/>).
/// </para>
/// </summary>
public interface IScheduler : IDispatcher
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

    /// <summary>
    /// Schedules a one-shot action at an absolute mission time (µs since the mission epoch,
    /// i.e. against <see cref="IClock.MissionNowUs"/>) instead of a relative delay.
    /// </summary>
    /// <returns>The event id, or -1 if the instant is already in the past or the queue is full.</returns>
    int AddEventAtMission(Action action, long missionTimeUs);

    /// <summary>
    /// Schedules a repeating action whose <em>first</em> firing is at the absolute mission time
    /// <paramref name="missionTimeUs"/>, then every <paramref name="periodUs"/> µs thereafter
    /// (the period stays a relative interval; only the first firing is mission-anchored).
    /// </summary>
    /// <returns>The event id, or -1 if the instant is in the past, the period is non-positive, or the queue is full.</returns>
    int AddPeriodicEventAtMission(Action action, long missionTimeUs, long periodUs);

    /// <summary>
    /// Schedules a coroutine process first stepped at the absolute mission time
    /// <paramref name="missionTimeUs"/>; subsequent steps follow the delays it yields.
    /// </summary>
    /// <returns>The event id, or -1 if the instant is already in the past or the queue is full.</returns>
    int AddCoroutineAtMission(Func<IEnumerator<SimYield>> factory, long missionTimeUs);

    /// <summary>Cancels a pending event by id; no effect if it has already fired or the id is unknown.</summary>
    void RemoveEvent(int id);
}
