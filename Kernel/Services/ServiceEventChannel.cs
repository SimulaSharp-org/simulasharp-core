namespace SimulaSharp.Kernel.Services;

/// <summary>
/// Backing for a payload-less fire-and-forget <c>[ServiceEvent]</c>. Holds the
/// subscribers (each a callback plus the scheduler it must run on) and, on
/// <see cref="Trigger"/>, <see cref="IDispatcher.Post"/>s each callback onto its own
/// site. The subscriber list is copy-on-write, so triggering is lock-free and
/// registration is safe from any thread. The generated service wraps one of these per
/// event behind <c>Trigger_&lt;Name&gt;()</c> / <c>RegisterCallback_&lt;Name&gt;(...)</c>.
/// </summary>
public sealed class ServiceEventChannel
{
    private readonly object _lock = new();
    private volatile (IDispatcher Scheduler, Action Callback)[] _subs = [];

    /// <summary>Subscribes <paramref name="callback"/>, to be invoked on <paramref name="scheduler"/>.</summary>
    public void Register(IDispatcher scheduler, Action callback)
    {
        lock (_lock)
        {
            var updated = new (IDispatcher, Action)[_subs.Length + 1];
            Array.Copy(_subs, updated, _subs.Length);
            updated[_subs.Length] = (scheduler, callback);
            _subs = updated;
        }
    }

    /// <summary>Posts every subscriber's callback onto its own site (fire-and-forget).</summary>
    public void Trigger()
    {
        var subs = _subs;
        for (int i = 0; i < subs.Length; i++)
            subs[i].Scheduler.Post(subs[i].Callback);
    }
}

/// <summary>
/// Backing for a fire-and-forget <c>[ServiceEvent]</c> carrying a payload of type
/// <typeparamref name="T"/>. See <see cref="ServiceEventChannel"/>.
/// </summary>
/// <typeparam name="T">The event payload type.</typeparam>
public sealed class ServiceEventChannel<T>
{
    private readonly object _lock = new();
    private volatile (IDispatcher Scheduler, Action<T> Callback)[] _subs = [];

    /// <summary>Subscribes <paramref name="callback"/>, to be invoked on <paramref name="scheduler"/>.</summary>
    public void Register(IDispatcher scheduler, Action<T> callback)
    {
        lock (_lock)
        {
            var updated = new (IDispatcher, Action<T>)[_subs.Length + 1];
            Array.Copy(_subs, updated, _subs.Length);
            updated[_subs.Length] = (scheduler, callback);
            _subs = updated;
        }
    }

    /// <summary>Posts every subscriber's callback (with <paramref name="value"/>) onto its own site.</summary>
    public void Trigger(T value)
    {
        var subs = _subs;
        for (int i = 0; i < subs.Length; i++)
        {
            var callback = subs[i].Callback;
            subs[i].Scheduler.Post(() => callback(value));
        }
    }
}
