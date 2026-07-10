using SimulaSharp.Kernel.Logging;
using SimulaSharp.Kernel.Services;

namespace SimulaSharp.Kernel.Model;

/// <summary>
/// Base for a <b>clock-ungated</b> service — one that owns external I/O (a hardware link, an
/// out-of-process feed) that runs outside simulation time and acts on the run's start/stop, e.g. an
/// EGSE that halts/unhalts a real OBC in lockstep with the sim. Its <see cref="Site"/> is a
/// <see cref="LooselyCoupledSite"/>: a dedicated <b>dispatcher thread</b> that serially runs the work
/// marshalled to it (its generated <c>[ServiceRequest]</c>/<c>[ServiceEvent]</c> plumbing, plus
/// anything the service <c>Post</c>s) and sleeps when idle. There is no author-written loop.
/// <para>
/// The design is an <b>actor with its own I/O threads</b>:
/// <list type="bullet">
///   <item><b>Dispatcher</b> (the site thread) — owns the service's single-threaded state and does
///   <em>outbound</em> sends <b>inline</b> from a marshalled request handler (a different socket than
///   the reader, so it never contends with a read). No locks: all state lives here.</item>
///   <item><b>Reader thread(s)</b> — the service spins these itself (a blocking socket read) and hands
///   inbound work back to the dispatcher via <see cref="Site"/><c>.Post(...)</c>. Bound to the run via a
///   <see cref="System.Threading.CancellationTokenSource"/>: (re)spawn in <see cref="OnRunStarted"/>,
///   cancel + join in <see cref="OnRunStopped"/>.</item>
///   <item><b>Control plane</b> — <see cref="OnRunStarted"/>/<see cref="OnRunStopped"/> fire
///   <em>inline on the clock thread</em> (not the dispatcher), so a halt/unhalt over an independent
///   control link is immediate and never queued behind data work.</item>
/// </list>
/// The canonical shape:
/// <code>
/// public override void Configure() => _link = Connect();        // open sockets; hardware halted
///
/// protected override void OnRunStarted()                        // clock thread: unhalt + start reading
/// {
///     _link.Unhalt();
///     _rx = new CancellationTokenSource();
///     _rxThread = new Thread(() => ReadLoop(_rx.Token)) { IsBackground = true };
///     _rxThread.Start();
/// }
/// protected override void OnRunStopped()                        // clock thread: halt + stop reading
/// {
///     _link.Halt(); _rx?.Cancel(); _link.WakeReader(); _rxThread?.Join();
/// }
///
/// [ServiceRequest] protected void Handle_Ch0Tx(byte[] f) =&gt; _link.Write(0, f);  // dispatcher: inline send
/// private void ReadLoop(CancellationToken ct)
/// {
///     while (!ct.IsCancellationRequested &amp;&amp; _link.TryRead(out var ch, out var frame))
///         Site.Post(() =&gt; Route(ch, frame));                    // hand inbound to the dispatcher
/// }
/// </code>
/// (The link's read and write paths must be independent — separate sockets — because the reader thread
/// and the dispatcher touch them concurrently.)
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
        _site = new LooselyCoupledSite(name);
        timeKeeper.Started += OnClockStarted;   // run-state control fires inline on the clock thread
        timeKeeper.Stopped += OnClockStopped;
    }

    /// <inheritdoc/>
    public IClock TimeKeeper { get; }

    /// <inheritdoc/>
    public ILogger Logger { get; }

    /// <inheritdoc/>
    public IDispatcherSite Site => _site;

    private void OnClockStarted(object? sender, EventArgs e) => OnRunStarted();
    private void OnClockStopped(object? sender, EventArgs e) => OnRunStopped();

    /// <summary>
    /// Called <b>inline on the clock thread</b> when the run starts or resumes. Override to bring the
    /// external link into lockstep — e.g. unhalt the hardware and (re)spawn the reader thread. It runs
    /// off the dispatcher on purpose, so it can act immediately over an independent control link.
    /// </summary>
    protected virtual void OnRunStarted() { }

    /// <summary>
    /// Called <b>inline on the clock thread</b> when the run pauses. Override to freeze the link — e.g.
    /// halt the hardware and cancel + join the reader thread.
    /// </summary>
    protected virtual void OnRunStopped() { }

    /// <summary>
    /// Called by the framework after construction, assembly wiring and config are complete. The
    /// dispatcher thread has not started yet; open sockets / do pre-run setup here.
    /// </summary>
    public virtual void Configure() { }

    /// <summary>
    /// Deinitialization hook, run with the dispatcher thread already stopped. Do the <b>final</b>
    /// teardown here — close sockets, cancel and join any reader thread (a pause-time
    /// <see cref="OnRunStopped"/> is not guaranteed on a dispose-while-running).
    /// </summary>
    public virtual void Dispose() { }
}
