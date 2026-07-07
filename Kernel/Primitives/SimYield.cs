namespace SimulaSharp.Kernel.Primitives;

/// <summary>
/// The value a coroutine (<see cref="IEnumerator{SimYield}"/>) yields to say when
/// it should be resumed. All delays are in simulation microseconds.
/// </summary>
public readonly struct SimYield
{
    public readonly long DelayUs;

    private SimYield(long delayUs) => DelayUs = delayUs;

    /// <summary>Resume on the next processing pass (zero delay).</summary>
    public static SimYield Immediate => new(0);

    /// <summary>Resume after the specified delay in microseconds.</summary>
    public static SimYield Delay(long us) => new(us);
}
