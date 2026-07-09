namespace SimulaSharp.Kernel.Services;

/// <summary>
/// The cross-thread marshalling surface of a scheduling domain: hand work to the domain's thread
/// from any other thread. This is what the <c>&lt;Type&gt;Ref</c> accessors and the service
/// interaction plumbing (<c>[ServiceEvent]</c>/<c>[ServiceRequest]</c>) target — neither needs the
/// sim-time queue (<see cref="IScheduler"/>) nor the lifecycle (<see cref="ISite"/>), only the ability
/// to run something on the right thread.
/// </summary>
public interface IDispatcher
{
    /// <summary>Fire-and-forget cross-thread work, run on this domain's thread during its next drain.</summary>
    void Post(Action action);

    /// <summary>
    /// Runs cross-thread work on this domain's thread; the returned task completes when it
    /// finishes (or faults if it throws).
    /// </summary>
    Task InjectVoidAsync(Action action);

    /// <summary>
    /// Runs a cross-thread function on this domain's thread; the returned task yields its
    /// result (or faults if it throws).
    /// </summary>
    Task<T> InjectAsync<T>(Func<T> action);
}
