using System.Threading;

namespace SimulaSharp.Kernel.Primitives;

/// <summary>
/// The caller-owned outcome of a <b>bounded</b> await
/// (<see cref="SimYield.Await(System.Threading.Tasks.Task, long, SimAwaitToken)"/>). A coroutine receives nothing on
/// resume, so it creates one of these, passes it into the bounded await, and reads <see cref="TimedOut"/> after it
/// re-enters to learn why it resumed:
/// <code>
/// var tok = new SimAwaitToken();
/// var t = peer.SlowRequest();
/// yield return SimYield.Await(t, timeoutUs: 5_000, tok);
/// // ── resumed here ──
/// if (tok.TimedOut) { /* gave up waiting */ }
/// else             { var r = t.GetAwaiter().GetResult(); }   // complete by now
/// </code>
/// <para>
/// The runtime resolves the token to exactly one outcome — completed or timed-out — whichever occurs first; the
/// other is ignored. On a timeout the awaited task is <b>not</b> cancelled: it keeps running and its eventual
/// completion is discarded, so its result must not be read. Create a fresh token per await; do not reuse or share one.
/// </para>
/// </summary>
public sealed class SimAwaitToken
{
    private const int Pending = 0, CompletedState = 1, TimedOutState = 2;

    // Resolved on the owning site thread (both the timeout deadline and the task-completion resume run there); the
    // atomic CompareExchange still makes the single-shot resolution robust regardless of who calls it.
    private int _state;

    /// <summary>True once the await resumed because its timeout elapsed before the task completed.</summary>
    public bool TimedOut => Volatile.Read(ref _state) == TimedOutState;

    /// <summary>True once the await resumed because the task completed before the timeout.</summary>
    public bool Completed => Volatile.Read(ref _state) == CompletedState;

    /// <summary>
    /// Resolves the token to a single outcome. Returns <c>true</c> for the caller that wins the race (and should
    /// resume the coroutine); <c>false</c> if the token was already resolved by the other trigger.
    /// </summary>
    internal bool TryResolve(bool timedOut) =>
        Interlocked.CompareExchange(ref _state, timedOut ? TimedOutState : CompletedState, Pending) == Pending;
}
