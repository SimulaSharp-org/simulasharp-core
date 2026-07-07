using System.Diagnostics;

namespace SimulaSharp.Kernel.Services;

/// <summary>
/// The hard real-time clock for a simulation run.
/// <para>
/// A single background thread (the sole writer) continuously advances the
/// <em>horizon</em> — elapsed simulation time in microseconds — and publishes it
/// to a plain <see cref="long"/> field. Observers (SimSites) read that field with
/// <see cref="Volatile.Read(ref long)"/>. Because there is exactly one writer and
/// the field is a naturally-aligned 64-bit value, volatile semantics are
/// sufficient: we need visibility/ordering, not the read-modify-write atomicity
/// that <see cref="Interlocked"/> provides.
/// </para>
/// <para>
/// Time always advances at real time (1:1). There is no speed factor and no
/// reset: <see cref="Pause"/> freezes the horizon, <see cref="Start"/> resumes it,
/// and to start over you create a new <see cref="Simulator"/>.
/// </para>
/// </summary>
public sealed class SimClock : IClock
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
    /// thread; read by observers via <see cref="Volatile.Read(ref long)"/>.
    /// </summary>
    private long _horizonUs;

    /// <summary>Released to wake the clock thread out of its paused wait.</summary>
    private readonly AutoResetEvent _resume = new(false);

    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _disposed;

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
    public long NowUs => Volatile.Read(ref _horizonUs);

    /// <inheritdoc/>
    public long MissionNowUs => _missionEpochUs + Volatile.Read(ref _horizonUs);

    /// <inheritdoc/>
    public bool IsRunning => _running;

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

        Stopped?.Invoke(this, EventArgs.Empty);
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

            Volatile.Write(ref _horizonUs, _baseUs + SegmentUs());
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
        _resume.Set();      // unblock the thread if it is parked
        _thread?.Join();
        _thread = null;
        _resume.Dispose();
    }
}
