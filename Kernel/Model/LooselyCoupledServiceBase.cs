using SimulaSharp.Kernel.Logging;
using SimulaSharp.Kernel.Services;

namespace SimulaSharp.Kernel.Model;

/// <summary>
/// Base for a <b>clock-ungated</b> service — one that owns external I/O (a hardware link, an
/// out-of-process feed) that must run outside simulation time and act on the run's start/stop, e.g.
/// an EGSE that halts/unhalts a real OBC in lockstep with the sim. It runs on a
/// <see cref="LooselyCoupledSite"/> (its own dedicated thread, never gated by the clock) rather than
/// a <see cref="SimSite"/>.
/// <para>
/// You implement <see cref="_EntryPoint"/> — the thread's loop. Inside it, call
/// <see cref="ServiceInbox"/> to run cross-thread work marshalled to this service (its generated
/// <c>[ServiceRequest]</c>/<c>[ServiceEvent]</c> plumbing), and <c>await</c>
/// <see cref="WaitRunning"/> to gate on the clock. The canonical shape:
/// <code>
/// protected override async Task _EntryPoint()
/// {
///     Connect(); Identify(); Halt();                 // setup; hardware halted
///     while (!await WaitRunning())                    // false = clock running; true = disposing
///     {
///         Unhalt();                                   // rising edge — hardware in lockstep
///         while (TimeKeeper.IsRunning &amp;&amp; !Stopping) { PumpLink(); ServiceInbox(); }
///         Halt();                                     // falling edge — freeze with the sim
///     }
///     Close();
/// }
/// </code>
/// </para>
/// </summary>
public abstract class LooselyCoupledServiceBase : IService
{
    private readonly LooselyCoupledSite _site;

    /// <param name="timeKeeper">The shared simulation clock (read/observe).</param>
    /// <param name="logger">This service's component-scoped logger.</param>
    /// <param name="name">Diagnostic name for the service's site/thread.</param>
    protected LooselyCoupledServiceBase(IClock timeKeeper, ILogger logger, string name)
    {
        TimeKeeper = timeKeeper;
        Logger = logger;
        _site = new LooselyCoupledSite(name, RunEntryPoint);
    }

    /// <inheritdoc/>
    public IClock TimeKeeper { get; }

    /// <inheritdoc/>
    public ILogger Logger { get; }

    /// <inheritdoc/>
    public IDispatcherSite Site => _site;

    /// <summary>True once the service is being torn down — check it in inner loops so they exit.</summary>
    protected bool Stopping => _site.IsStopping;

    /// <summary>
    /// The thread's main loop, run on the site's dedicated thread under a single-thread context so
    /// <c>await</c>s resume here. Do external I/O, call <see cref="ServiceInbox"/> to run marshalled
    /// work, and <c>await</c> <see cref="WaitRunning"/> to gate on the clock.
    /// </summary>
    protected abstract Task _EntryPoint();

    /// <summary>
    /// Runs all currently-queued cross-thread work (this service's marshalled requests / event
    /// deliveries) inline on this thread. Call it from <see cref="_EntryPoint"/> wherever you want to
    /// service pending work.
    /// </summary>
    protected void ServiceInbox() => _site.DrainInbox();

    /// <summary>
    /// Await in the loop to gate on the run state: the task yields <c>false</c> when the clock is
    /// running (proceed), or <c>true</c> when the service is being disposed (end-of-life — break the
    /// loop and tear down). Completes immediately if already running or already stopping; otherwise
    /// parks until the clock starts or shutdown is requested. Its continuation resumes on this thread.
    /// </summary>
    protected Task<bool> WaitRunning()
    {
        if (_site.IsStopping) return Task.FromResult(true);
        if (TimeKeeper.IsRunning) return Task.FromResult(false);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStarted(object? s, EventArgs e) => tcs.TrySetResult(false);
        TimeKeeper.Started += OnStarted;
        var reg = _site.Stopping.Register(() => tcs.TrySetResult(true));

        // Close the race: the clock may have started, or shutdown begun, between the checks above
        // and the subscriptions just made.
        if (_site.IsStopping) tcs.TrySetResult(true);
        else if (TimeKeeper.IsRunning) tcs.TrySetResult(false);

        tcs.Task.ContinueWith(_ => { TimeKeeper.Started -= OnStarted; reg.Dispose(); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return tcs.Task;
    }

    /// <summary>Wraps the author's <see cref="_EntryPoint"/> so a fault is logged, not fatal to the thread.</summary>
    private async Task RunEntryPoint()
    {
        try { await _EntryPoint(); }
        catch (Exception ex) { Logger.LogError($"loosely-coupled service entry point faulted: {ex}"); }
    }

    /// <summary>
    /// Called by the framework after construction, assembly wiring and config are complete. The site
    /// thread has not started yet; override for pre-run setup that does not need the loop running.
    /// </summary>
    public virtual void Configure() { }

    /// <summary>Deinitialization hook. The site thread is already stopped by the time this runs.</summary>
    public virtual void Dispose() { }
}
