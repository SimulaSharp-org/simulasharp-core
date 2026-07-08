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
    /// rather than after a delay (see <see cref="Await"/>).
    /// </summary>
    public readonly Task? Awaited;

    private SimYield(long delayUs, Task? awaited)
    {
        DelayUs = delayUs;
        Awaited = awaited;
    }

    /// <summary>Resume on the next processing pass (zero delay).</summary>
    public static SimYield Immediate => new(0, null);

    /// <summary>Resume after the specified delay in microseconds.</summary>
    public static SimYield Delay(long us) => new(us, null);

    /// <summary>
    /// Suspend the coroutine until <paramref name="task"/> completes (e.g. a cross-site
    /// request), then resume it on its own site. Read the result from the task after
    /// resuming — it is complete by then:
    /// <code>var t = peer.Foo(Scheduler); yield return SimYield.Await(t); var r = t.GetAwaiter().GetResult();</code>
    /// </summary>
    public static SimYield Await(Task task) => new(0, task);
}
