using System.Collections.Concurrent;

namespace SimulaSharp.Kernel.Services;

/// <summary>
/// A <b>clock-ungated</b> dispatcher site for external I/O (e.g. a hardware link): a dedicated thread
/// that is a pure <b>serial executor</b> of the work marshalled to it. It is <see cref="SimSite"/>'s
/// inbox mechanism with the clock and the sim-time event queue removed — the thread simply drains its
/// lock-free <see cref="IDispatcher"/> inbox, then <b>sleeps until the next <see cref="Post"/> wakes it</b>
/// (there is no horizon to advance on, so nothing else can wake it).
/// <para>
/// Because every item runs on this one thread, the owning service's state (routing tables, sequence
/// counters, an outbound socket written inline from a marshalled request) stays single-threaded with no
/// locks. Blocking I/O that must <em>read</em> concurrently belongs on a separate reader thread the
/// service owns, which hands inbound work back through <see cref="Post"/>.
/// </para>
/// </summary>
public sealed class LooselyCoupledSite : IDispatcherSite
{
    private readonly string _name;

    /// <summary>Cross-thread work destined for this site's thread (lock-free).</summary>
    private readonly ConcurrentQueue<Action> _inbox = new();

    /// <summary>Parks the executor while the inbox is empty; released by every <see cref="Post"/> and by <see cref="Stop"/>.</summary>
    private readonly AutoResetEvent _resume = new(false);

    private Thread? _thread;
    private volatile bool _stopped;

    /// <param name="name">Diagnostic name (used for the thread name).</param>
    public LooselyCoupledSite(string name) => _name = name;

    /// <inheritdoc/>
    public string Name => _name;

    // ── IDispatcher (cross-thread inbox) ────────────────────────────────────────

    /// <inheritdoc/>
    public void Post(Action action)
    {
        _inbox.Enqueue(action);
        _resume.Set();          // wake the executor if it is parked
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

    // ── ISite (lifecycle, driven by the simulator) ──────────────────────────────

    /// <inheritdoc/>
    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = $"LooselyCoupled-{_name}" };
        _thread.Start();
    }

    /// <summary>Drain everything queued, then sleep until the next <see cref="Post"/> (or <see cref="Stop"/>) wakes us.</summary>
    private void Run()
    {
        while (!_stopped)
        {
            while (_inbox.TryDequeue(out var work)) work();
            if (!_stopped) _resume.WaitOne();
        }
    }

    /// <inheritdoc/>
    public void Stop()
    {
        if (_thread is null) return;
        _stopped = true;
        _resume.Set();      // wake the parked executor so it observes the stop and exits
        _thread.Join();
        _thread = null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Stop();
        _resume.Dispose();
    }
}
