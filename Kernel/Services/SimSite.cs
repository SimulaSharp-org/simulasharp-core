using System.Collections.Concurrent;
using SimulaSharp.Kernel.Primitives;

namespace SimulaSharp.Kernel.Services;

/// <summary>
/// An independent scheduling domain: one thread, one <see cref="EventQueue"/>.
/// <para>
/// A root model and its whole containment subtree share a single SimSite, so
/// closely-bound models run on the same thread and call each other directly,
/// lock-free. Work from <em>other</em> sites/threads arrives through the lock-free
/// <see cref="Post"/>/<see cref="InjectAsync{T}"/> inbox and is executed on this
/// site's thread, so the event queue is only ever touched single-threaded.
/// </para>
/// <para>
/// <b>Execution:</b> while the clock is running the site reads the clock's horizon,
/// fires everything now due, and loops. When it falls idle it <em>parks</em> (0 CPU)
/// rather than busy-spinning, so it no longer starves the other site threads or the
/// external-I/O threads under load. The on-time wake is delivered by the clock itself
/// — the sole, continuously advancing horizon writer — which releases the site the
/// instant the horizon reaches its next event (<see cref="IHorizonWaiter"/>), giving
/// sub-µs precision that does not depend on OS timer granularity. A park is also cut
/// short by injected inbox work and by a clock pause; the timed wait is only a
/// backstop. Work due within <see cref="SpinBudgetUs"/> is spun for, shaving the wake
/// latency off the most imminent events. While the clock is paused the site blocks,
/// consuming no CPU. See <see cref="RunLoop"/> for the spin/park policy.
/// </para>
/// </summary>
public sealed class SimSite : IScheduler, IDispatcherSite, IHorizonWaiter
{
    /// <summary>Spin (rather than park) when the next event is within this many µs — keeps hard-real-time latency for
    /// imminent work while releasing the core when the site is genuinely idle.</summary>
    private const long SpinBudgetUs = 200;

    /// <summary>Backstop cap (ms) on a park's timed wait. The clock delivers the real, on-time wake; this only
    /// bounds how long an idle site sleeps before it re-checks state, so nothing ever sleeps blind.</summary>
    private const int BackstopMs = 1000;

    private readonly string _name;
    private readonly IClock _clock;
    private readonly EventQueue _events;

    /// <summary>Cross-thread work destined for this site's thread (lock-free).</summary>
    private readonly ConcurrentQueue<Action> _inbox = new();

    /// <summary>Parks the run loop while the clock is paused or the site is idle; released on
    /// resume/stop, on any cross-thread inbox enqueue, and by the clock when the next event comes due.</summary>
    private readonly AutoResetEvent _resume = new(false);

    /// <summary>The concrete clock when it supports precise horizon wake-ups (it always does today);
    /// null only if some other <see cref="IClock"/> is supplied, in which case the timed backstop drives waking.</summary>
    private readonly SimClock? _wakeClock;

    /// <summary>Absolute sim-time (µs) the clock should wake this site at, or <see cref="long.MaxValue"/> when the
    /// site is not parked on a timed event. Written by this thread before parking; read by the clock thread.</summary>
    private long _nextWakeUs = long.MaxValue;

    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _disposed;

    /// <summary>Human-readable site name (used for the thread name and diagnostics).</summary>
    public string Name => _name;

    /// <summary>
    /// Creates a site bound to <paramref name="clock"/>. The site does no work until
    /// <see cref="Start"/> is called and the clock is running.
    /// </summary>
    /// <param name="name">Diagnostic name.</param>
    /// <param name="clock">The shared simulation clock.</param>
    /// <param name="maxEvents">Capacity of the site's event queue.</param>
    public SimSite(string name, IClock clock, int maxEvents = 64)
    {
        _name = name;
        _clock = clock;
        _wakeClock = clock as SimClock;   // enables precise clock-driven wakes (see IHorizonWaiter)
        _events = new EventQueue(maxEvents);

        // When a coroutine awaits a task, resume it (on this thread, via the inbox) once
        // the task completes — re-arming at the then-current time so later delays are correct.
        _events.OnAwait = (slot, version, task) =>
            task.ContinueWith(
                _ => Post(() => _events.ReArm(slot, version, _clock.SimNowUs)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    /// <summary>
    /// Starts the site's thread and subscribes to the clock lifecycle. Safe to call
    /// before or after the clock starts: the current clock state is captured, and
    /// <see cref="IClock.Started"/>/<see cref="IClock.Stopped"/> keep it in sync.
    /// </summary>
    public void Start()
    {
        if (_thread is not null) return;

        _clock.Started += OnClockStarted;
        _clock.Stopped += OnClockStopped;
        _running = _clock.IsRunning;   // handle a clock that is already running
        _wakeClock?.RegisterWaiter(this);   // let the clock deliver precise on-time wakes

        _thread = new Thread(RunLoop) { IsBackground = true, Name = $"SimSite-{_name}" };
        _thread.Start();
    }

    /// <summary>Unsubscribes from the clock, stops the thread, and joins it.</summary>
    public void Stop()
    {
        if (_thread is null) return;

        _clock.Started -= OnClockStarted;
        _clock.Stopped -= OnClockStopped;
        _wakeClock?.UnregisterWaiter(this);
        _disposed = true;
        _resume.Set();      // unblock the loop if parked
        _thread.Join();
        _thread = null;
    }

    private void OnClockStarted(object? sender, EventArgs e)
    {
        _running = true;
        _resume.Set();
    }

    private void OnClockStopped(object? sender, EventArgs e)
    {
        _running = false;
        _resume.Set();      // cut a timed park short so the pause is observed at once
    }

    // ── IScheduler (called on this site's thread) ───────────────────────────────

    /// <inheritdoc/>
    public int AddEvent(Action action, long delayUs) =>
        _events.AddEvent(action, _clock.SimNowUs + delayUs);

    /// <inheritdoc/>
    public int AddPeriodicEvent(Action action, long periodUs) =>
        _events.AddPeriodic(action, _clock.SimNowUs + periodUs, periodUs);

    /// <inheritdoc/>
    public int AddCoroutine(Func<IEnumerator<SimYield>> factory, long delayUs) =>
        _events.AddCoroutine(factory, _clock.SimNowUs + delayUs);

    /// <summary>
    /// Converts an absolute mission time (µs) into the absolute sim-time due the
    /// <see cref="EventQueue"/> expects, using a single consistent clock snapshot so the
    /// sim/mission pair can't tear across two separate reads. Returns -1 for a past instant.
    /// </summary>
    private long MissionDueUs(long missionTimeUs)
    {
        TimeSnapshot t = _clock.Now();
        return missionTimeUs < t.MissionTimeUs
            ? -1
            : t.SimTimeUs + (missionTimeUs - t.MissionTimeUs);
    }

    /// <inheritdoc/>
    public int AddEventAtMission(Action action, long missionTimeUs)
    {
        long dueUs = MissionDueUs(missionTimeUs);
        return dueUs < 0 ? -1 : _events.AddEvent(action, dueUs);
    }

    /// <inheritdoc/>
    public int AddPeriodicEventAtMission(Action action, long missionTimeUs, long periodUs)
    {
        long dueUs = MissionDueUs(missionTimeUs);
        return dueUs < 0 ? -1 : _events.AddPeriodic(action, dueUs, periodUs);
    }

    /// <inheritdoc/>
    public int AddCoroutineAtMission(Func<IEnumerator<SimYield>> factory, long missionTimeUs)
    {
        long dueUs = MissionDueUs(missionTimeUs);
        return dueUs < 0 ? -1 : _events.AddCoroutine(factory, dueUs);
    }

    /// <inheritdoc/>
    public void RemoveEvent(int id) => _events.RemoveEvent(id);

    // ── IScheduler (cross-thread inbox) ─────────────────────────────────────────

    /// <inheritdoc/>
    public void Post(Action action)
    {
        _inbox.Enqueue(action);
        _resume.Set();          // wake the run loop if it is parked (idle or between events)
    }

    /// <inheritdoc/>
    public Task InjectVoidAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _inbox.Enqueue(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        _resume.Set();
        return tcs.Task;
    }

    /// <inheritdoc/>
    public Task<T> InjectAsync<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _inbox.Enqueue(() =>
        {
            try { tcs.SetResult(action()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        _resume.Set();
        return tcs.Task;
    }

    // ── IHorizonWaiter (called on the clock thread) ─────────────────────────────

    /// <inheritdoc/>
    long IHorizonWaiter.NextWakeUs => Volatile.Read(ref _nextWakeUs);

    /// <inheritdoc/>
    void IHorizonWaiter.WakeFromClock()
    {
        Volatile.Write(ref _nextWakeUs, long.MaxValue);   // consume the request so it isn't re-signalled
        _resume.Set();
    }

    // ── Run loop ────────────────────────────────────────────────────────────────

    /// <summary>
    /// While running: drain the inbox, then fire everything due at the clock's current horizon.
    /// <para>
    /// When the site falls idle (inbox empty, nothing imminently due) it <em>parks</em> on
    /// <see cref="_resume"/> instead of spinning, releasing the core for the other sites and for
    /// I/O-bound work on other threads (a blocking ZMQ/serial read, say). It publishes the next event's
    /// due time in <see cref="_nextWakeUs"/> and the clock — the sole horizon writer — <c>Set</c>s
    /// <see cref="_resume"/> the instant the horizon reaches it, so the wake is on time to sub-µs without
    /// busy-spinning or trusting the OS timer. The park is <em>also</em> released by a cross-thread inbox
    /// enqueue (<see cref="Post"/>/<see cref="InjectAsync{T}"/>) and by a clock pause; the timed
    /// <c>WaitOne</c> is only a <see cref="BackstopMs"/> safety net so nothing ever sleeps blind (and it
    /// drives waking at all should some non-<see cref="SimClock"/> clock be used). Work due within
    /// <see cref="SpinBudgetUs"/> is spun for, shaving the wake latency off the
    /// most imminent events (and covering manual/batch clocks whose horizon is already at/past due).
    /// </para>
    /// While the clock is paused the site blocks on <see cref="_resume"/>, consuming no CPU.
    /// </summary>
    private void RunLoop()
    {
        while (!_disposed)
        {
            if (!_running)
            {
                _resume.WaitOne();
                continue;
            }

            while (_inbox.TryDequeue(out var work))
                work();

            _events.ProcessDue(_clock.SimNowUs);

            // Cross-thread work may have landed while we were processing — loop and drain it now.
            if (!_inbox.IsEmpty)
                continue;

            long nextDueUs = _events.PeekNextDueTime();   // -1 when nothing is scheduled
            long waitUs = nextDueUs < 0 ? BackstopMs * 1000L : nextDueUs - _clock.SimNowUs;
            if (waitUs <= SpinBudgetUs)
            {
                Thread.SpinWait(1);                       // imminent (or already due) — spin for latency
                continue;
            }

            // Arm the clock to wake us exactly when the horizon reaches the next event, then park. The
            // timed wait is only a backstop — the clock's Set (or a Post, or a pause) is the real waker.
            Volatile.Write(ref _nextWakeUs, nextDueUs < 0 ? long.MaxValue : nextDueUs);
            _resume.WaitOne((int)Math.Clamp(waitUs / 1000, 1, BackstopMs));
            Volatile.Write(ref _nextWakeUs, long.MaxValue);
        }
    }

    /// <summary>Stops the site and releases its wait handle.</summary>
    public void Dispose()
    {
        Stop();
        _resume.Dispose();
    }
}
