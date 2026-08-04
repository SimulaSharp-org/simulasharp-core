namespace SimulaSharp.Kernel.Primitives;

/// <summary>
/// The value a coroutine (<see cref="IEnumerator{SimYield}"/>) yields to say when
/// it should be resumed. All delays are in simulation microseconds.
/// </summary>
public readonly struct SimYield
{
    /// <summary>Microseconds to wait before the coroutine is resumed (0 = next processing pass). Ignored when <see cref="Awaited"/> is set.</summary>
    public readonly long DelayUs;

    /// <summary>
    /// When non-null, the coroutine is resumed on its own site once this task completes,
    /// rather than after a delay (see <see cref="Await(Task)"/>).
    /// </summary>
    public readonly Task? Awaited;

    /// <summary>
    /// For a <b>bounded</b> await, the timeout in simulation microseconds: if <see cref="Awaited"/> has not
    /// completed within this long, the coroutine is resumed anyway and <see cref="Token"/> is marked timed-out.
    /// Meaningful only together with a non-null <see cref="Token"/>; otherwise the await is unbounded.
    /// </summary>
    public readonly long TimeoutUs;

    /// <summary>
    /// The caller-owned outcome for a bounded await — non-null only for the timed
    /// <see cref="Await(Task, long, SimAwaitToken)"/> overload. The runtime resolves it to completed-or-timed-out,
    /// and the coroutine reads it after resuming to learn which happened.
    /// </summary>
    public readonly SimAwaitToken? Token;

    private SimYield(long delayUs, Task? awaited, long timeoutUs, SimAwaitToken? token)
    {
        DelayUs = delayUs;
        Awaited = awaited;
        TimeoutUs = timeoutUs;
        Token = token;
    }

    /// <summary>Resume on the next processing pass (zero delay).</summary>
    public static SimYield Immediate => new(0, null, 0, null);

    /// <summary>Resume after the specified delay in microseconds.</summary>
    public static SimYield Delay(long us) => new(us, null, 0, null);

    /// <summary>
    /// Suspend the coroutine until <paramref name="task"/> completes (e.g. a cross-site
    /// request), then resume it on its own site. Read the result from the task after
    /// resuming — it is complete by then:
    /// <code>var t = peer.Foo(Scheduler); yield return SimYield.Await(t); var r = t.GetAwaiter().GetResult();</code>
    /// </summary>
    public static SimYield Await(Task task) => new(0, task, 0, null);

    /// <summary>
    /// Suspend the coroutine until <paramref name="task"/> completes <b>or</b> <paramref name="timeoutUs"/> simulation
    /// microseconds elapse — whichever comes first — then resume it on its own site. After resuming, read
    /// <paramref name="token"/>: <see cref="SimAwaitToken.TimedOut"/> is <c>false</c> when the task completed (read its
    /// result), <c>true</c> when the timeout fired first (the task is still running; do <b>not</b> read its result).
    /// Pass a fresh <see cref="SimAwaitToken"/> per await — it is the channel that carries the outcome back:
    /// <code>
    /// var tok = new SimAwaitToken();
    /// var t = peer.SlowRequest();
    /// yield return SimYield.Await(t, timeoutUs: 5_000, tok);
    /// if (tok.TimedOut) { /* gave up */ } else { var r = t.GetAwaiter().GetResult(); }
    /// </code>
    /// </summary>
    public static SimYield Await(Task task, long timeoutUs, SimAwaitToken token) => new(0, task, timeoutUs, token);
}
