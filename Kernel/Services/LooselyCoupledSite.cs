using System.Collections.Concurrent;

namespace SimulaSharp.Kernel.Services;

/// <summary>
/// A <b>clock-ungated</b> scheduling domain for external I/O (e.g. a hardware link): a dedicated
/// thread that is <em>not</em> driven by the simulation horizon and <em>never</em> parks when the
/// clock pauses. Unlike a <see cref="SimSite"/> it has no sim-time queue and does not auto-drain —
/// the owning <see cref="Model.LooselyCoupledServiceBase"/> supplies the loop (its
/// <c>_EntryPoint</c>) and decides when to service the inbox.
/// <para>
/// The thread runs the entry point under a <b>single-thread synchronization context</b>, so
/// <c>await</c>s inside the loop resume on this same thread — the socket/inbox state it touches
/// stays single-threaded, no locks. Cross-thread work arrives through the lock-free
/// <see cref="IDispatcher"/> inbox and is executed only when the loop calls <c>ServiceInbox()</c>.
/// </para>
/// </summary>
public sealed class LooselyCoupledSite : IDispatcherSite
{
    /// <summary>A single-threaded message pump: <see cref="Post"/> queues a continuation, <see cref="Pump"/> runs them on the calling thread until <see cref="Complete"/>.</summary>
    private sealed class PumpContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback d, object? state) { try { _queue.Add((d, state)); } catch (InvalidOperationException) { /* completed */ } }
        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException("LooselyCoupledSite is single-threaded; use Post/InjectAsync.");
        public void Complete() => _queue.CompleteAdding();
        public void Pump() { foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state); }
    }

    private readonly string _name;
    private readonly Func<Task> _entryPoint;
    private readonly ConcurrentQueue<Action> _inbox = new();
    private readonly CancellationTokenSource _stop = new();

    private PumpContext? _pump;
    private Thread? _thread;

    /// <param name="name">Diagnostic name (used for the thread name).</param>
    /// <param name="entryPoint">The loop the thread runs — supplied by the owning service.</param>
    public LooselyCoupledSite(string name, Func<Task> entryPoint)
    {
        _name = name;
        _entryPoint = entryPoint;
    }

    /// <inheritdoc/>
    public string Name => _name;

    /// <summary>Cancelled when <see cref="Stop"/>/<see cref="Dispose"/> is requested — the entry loop's shutdown signal.</summary>
    public CancellationToken Stopping => _stop.Token;

    /// <summary>True once shutdown has been requested.</summary>
    public bool IsStopping => _stop.IsCancellationRequested;

    // ── IDispatcher (cross-thread inbox) ────────────────────────────────────────

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

    /// <summary>Runs every currently-queued inbox item on this thread. Called by the owning service's loop.</summary>
    internal void DrainInbox()
    {
        while (_inbox.TryDequeue(out var work)) work();
    }

    // ── ISite (lifecycle, driven by the simulator) ──────────────────────────────

    /// <inheritdoc/>
    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = $"LooselyCoupled-{_name}" };
        _thread.Start();
    }

    private void Run()
    {
        var pump = _pump = new PumpContext();
        SynchronizationContext.SetSynchronizationContext(pump);
        Task task = _entryPoint();
        // When the entry point completes (loop saw shutdown), stop the pump so this thread can exit.
        task.ContinueWith(_ => pump.Complete(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        pump.Pump();   // runs the entry point's await-continuations on THIS thread until it completes
    }

    /// <inheritdoc/>
    public void Stop()
    {
        if (_thread is null) return;
        _stop.Cancel();      // unblocks a parked WaitRunning and flips the loop's shutdown check
        _thread.Join();
        _thread = null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Stop();
        _stop.Dispose();
    }
}
