using System.Diagnostics;
using SimulaSharp.Kernel.Primitives;

namespace SimulaSharp.Kernel.Services;

/// <summary>
/// The hard real-time clock for a simulation run.
/// <para>
/// A single background thread (the sole writer) continuously advances the
/// <em>horizon</em> — elapsed simulation time in microseconds — and publishes it
/// to a plain <see cref="long"/> field. Observers (SimSites) read that field with
/// <c>Volatile.Read</c>. Because there is exactly one writer and
/// the field is a naturally-aligned 64-bit value, volatile semantics are
/// sufficient: we need visibility/ordering, not the read-modify-write atomicity
/// that <see cref="Interlocked"/> provides.
/// </para>
/// <para>
/// Time always advances at real time (1:1). There is no speed factor and no
/// reset: <see cref="Pause"/> freezes the horizon, <see cref="Start"/> resumes it,
/// and to start over you create a new <see cref="SimulatorBase"/>.
/// </para>
/// </summary>
public sealed class SimClock : IClock, IDisposable
{
    /// <summary>Mission epoch as microseconds since the Unix epoch (constant).</summary>
    private readonly long _missionEpochUs;

    /// <summary>Measures real time elapsed within the current running segment.</summary>
    private readonly Stopwatch _sw = new();

    /// <summary>
    /// Simulation microseconds accumulated across previous running segments
    /// (i.e. everything before the current <see cref="_sw"/> segment). Combined
    /// with the stopwatch this survives pause/resume without losing time.
    /// </summary>
    private long _baseUs;

    /// <summary>
    /// The published horizon (elapsed simulation µs). Written only by the clock
    /// thread; read by observers via <c>Volatile.Read</c>.
    /// </summary>
    private long _horizonUs;

    /// <summary>Released to wake the clock thread out of its paused wait.</summary>
    private readonly AutoResetEvent _resume = new(false);

    /// <summary>
    /// Horizon (µs) at which the clock auto-pauses, or -1 when disarmed. Written by the
    /// arming caller (<see cref="PauseAtSimUs"/>/<see cref="RunFor"/>) and cleared by the
    /// clock thread when it fires or by any <see cref="Pause"/>. A single naturally-aligned
    /// 64-bit value read/written with <see cref="Volatile"/> — visibility only, no
    /// read-modify-write, so no <see cref="Interlocked"/> is needed.
    /// </summary>
    private long _pauseAtUs = -1;

    /// <summary>
    /// Signalled on every <see cref="Pause"/> so a caller blocked in <see cref="RunFor"/>
    /// wakes as soon as the run boundary (or any freeze) is reached.
    /// </summary>
    private readonly ManualResetEventSlim _paused = new(false);

    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _disposed;

    /// <summary>
    /// Sites registered to be woken precisely when the horizon reaches their next event. Copy-on-write:
    /// registration/removal is rare (site lifecycle) and takes <see cref="_waitersLock"/>; the clock
    /// thread reads the array lock-free on every tick.
    /// </summary>
    private volatile IHorizonWaiter[] _waiters = [];
    private readonly object _waitersLock = new();

    /// <inheritdoc/>
    public event EventHandler? Started;

    /// <inheritdoc/>
    public event EventHandler? Stopped;

    /// <summary>
    /// Creates a clock whose mission time is anchored at <paramref name="missionStart"/>.
    /// The horizon starts at 0; the clock does not advance until <see cref="Start"/>.
    /// </summary>
    /// <param name="missionStart">Absolute wall-clock time that maps to horizon 0.</param>
    public SimClock(DateTime missionStart)
    {
        _missionEpochUs = (long)((missionStart.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds * 1_000_000.0);
    }

    /// <inheritdoc/>
    public long SimNowUs => Volatile.Read(ref _horizonUs);

    /// <inheritdoc/>
    public long MissionNowUs => _missionEpochUs + Volatile.Read(ref _horizonUs);

    /// <inheritdoc/>
    public TimeSnapshot Now()
    {
        long h = Volatile.Read(ref _horizonUs);   // single read → consistent pair
        return new TimeSnapshot(h, _missionEpochUs + h);
    }

    /// <inheritdoc/>
    public bool IsRunning => _running;

    /// <summary>
    /// Registers a site to receive precise horizon wake-ups (called from <see cref="SimSite.Start"/>).
    /// Idempotent-safe for the caller: a site registers once when its thread starts.
    /// </summary>
    internal void RegisterWaiter(IHorizonWaiter waiter)
    {
        lock (_waitersLock)
        {
            var old = _waiters;
            var next = new IHorizonWaiter[old.Length + 1];
            Array.Copy(old, next, old.Length);
            next[old.Length] = waiter;
            _waiters = next;
        }
    }

    /// <summary>Unregisters a site (called from <see cref="SimSite.Stop"/>) so a stopped site is never signalled.</summary>
    internal void UnregisterWaiter(IHorizonWaiter waiter)
    {
        lock (_waitersLock)
        {
            var old = _waiters;
            int idx = Array.IndexOf(old, waiter);
            if (idx < 0) return;
            var next = new IHorizonWaiter[old.Length - 1];
            Array.Copy(old, 0, next, 0, idx);
            Array.Copy(old, idx + 1, next, idx, old.Length - idx - 1);
            _waiters = next;
        }
    }

    /// <summary>Wakes every registered site whose next event has come due at horizon <paramref name="h"/>.</summary>
    private void SignalDueWaiters(long h)
    {
        var waiters = _waiters;   // volatile snapshot; immutable, so safe to iterate lock-free
        for (int i = 0; i < waiters.Length; i++)
        {
            var w = waiters[i];
            if (h >= w.NextWakeUs) w.WakeFromClock();
        }
    }

    /// <summary>
    /// Real-time microseconds elapsed in the current running segment. Derived from
    /// total elapsed ticks (not accumulated per-iteration deltas), so it carries no
    /// truncation drift.
    /// </summary>
    private long SegmentUs() => _sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;

    /// <inheritdoc/>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) return;

        _running = true;
        _sw.Restart();

        if (_thread is null)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "SimClock" };
            _thread.Start();
        }
        else
        {
            // Resuming a previously-paused clock: wake the parked thread.
            _resume.Set();
        }

        Started?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public void Pause()
    {
        if (!_running) return;

        _running = false;
        _baseUs += SegmentUs();     // fold this segment into the accumulated total
        _sw.Stop();
        Volatile.Write(ref _horizonUs, _baseUs);   // freeze the published horizon exactly

        Volatile.Write(ref _pauseAtUs, -1);   // a freeze is never silently re-armed
        _paused.Set();                        // release any RunFor waiter
        Stopped?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public void PauseAtSimUs(long simTimeUs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Volatile.Write(ref _pauseAtUs, simTimeUs);

        // If the target already lies at/behind a running horizon, honor it now rather than
        // waiting for a tick that has effectively passed.
        if (simTimeUs >= 0 && _running && SimNowUs >= simTimeUs)
            Pause();
    }

    /// <inheritdoc/>
    public void RunFor(long durationUs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _paused.Reset();
        long targetUs = SimNowUs + durationUs;   // "from now", captured against the current horizon
        Start();                                 // ensure running first (no-op if already running)
        PauseAtSimUs(targetUs);                  // arm after Start so an already-passed target pauses cleanly
        _paused.Wait();                          // block until the auto-pause (or any Pause) fires
    }

    /// <summary>
    /// The clock thread. While running it hot-publishes the horizon; while paused it
    /// blocks on <see cref="_resume"/> so it consumes no CPU.
    /// </summary>
    private void Run()
    {
        while (!_disposed)
        {
            if (!_running)
            {
                _resume.WaitOne();
                continue;
            }

            long h = _baseUs + SegmentUs();
            Volatile.Write(ref _horizonUs, h);

            // Release any parked site whose next event is now due. Because this thread is the sole,
            // continuously advancing horizon writer, it delivers the wake with sub-µs precision — so
            // sites can park (0 CPU when idle) instead of busy-spinning, without losing real-time timing.
            SignalDueWaiters(h);

            // Auto-pause boundary (RunFor/PauseAtSimUs): self-pause on the writer thread so
            // the caller never races the horizon. Pause() disarms the target and parks us.
            long target = Volatile.Read(ref _pauseAtUs);
            if (target >= 0 && h >= target)
            {
                Pause();
                continue;
            }

            Thread.SpinWait(1);
        }
    }

    /// <summary>
    /// Stops the clock thread and releases resources. The instance cannot be
    /// restarted afterwards (create a new simulation instead).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        Volatile.Write(ref _pauseAtUs, -1);
        _paused.Set();      // release a caller blocked in RunFor
        _resume.Set();      // unblock the thread if it is parked
        _thread?.Join();
        _thread = null;
        _resume.Dispose();
        _paused.Dispose();
    }
}
