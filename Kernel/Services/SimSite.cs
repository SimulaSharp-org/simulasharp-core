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
/// <b>Execution:</b> while the clock is running the site <em>busy-spins</em> — it
/// reads the clock's horizon, fires everything now due, and loops. A busy-spin
/// (rather than a timed wait) is required for microsecond-accurate hard real-time
/// and to stay responsive to ad-hoc work injected by other threads at any moment.
/// While the clock is paused the site blocks, consuming no CPU.
/// </para>
/// </summary>
public sealed class SimSite : IScheduler
{
    private readonly string _name;
    private readonly IClock _clock;
    private readonly EventQueue _events;

    /// <summary>Cross-thread work destined for this site's thread (lock-free).</summary>
    private readonly ConcurrentQueue<Action> _inbox = new();

    /// <summary>Parks the run loop while the clock is paused; released on resume/stop.</summary>
    private readonly AutoResetEvent _resume = new(false);

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
    public SimSite(string name, IClock clock, int maxEvents = 1024)
    {
        _name = name;
        _clock = clock;
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

        _thread = new Thread(RunLoop) { IsBackground = true, Name = $"SimSite-{_name}" };
        _thread.Start();
    }

    /// <summary>Unsubscribes from the clock, stops the thread, and joins it.</summary>
    public void Stop()
    {
        if (_thread is null) return;

        _clock.Started -= OnClockStarted;
        _clock.Stopped -= OnClockStopped;
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

    private void OnClockStopped(object? sender, EventArgs e) => _running = false;

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
    public void Post(Action action) => _inbox.Enqueue(action);

    /// <inheritdoc/>
    public Task InjectVoidAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _inbox.Enqueue(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
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
        return tcs.Task;
    }

    // ── Run loop ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Busy-spins while running: drain the inbox, then fire everything due at the
    /// clock's current horizon. Parks on <see cref="_resume"/> while paused so a
    /// stopped simulation costs nothing.
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

            Thread.SpinWait(1);
        }
    }

    /// <summary>Stops the site and releases its wait handle.</summary>
    public void Dispose()
    {
        Stop();
        _resume.Dispose();
    }
}
