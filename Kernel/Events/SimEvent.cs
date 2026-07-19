using System;
using System.Collections.Generic;
using SimulaSharp.Kernel.Services;

namespace SimulaSharp.Kernel.Events;

/// <summary>
/// The stock, logic-free global event (payload-less). Registration records each handler against the dispatcher it is to
/// run on; a <see cref="Trigger"/> posts every handler onto that dispatcher. The <c>subscriber</c> identity itself is
/// unused — it is accepted only for uniformity with routing events. Because every <see cref="Register"/> runs
/// synchronously at assembly time (before the run starts) and triggering only reads the subscriber list, no locking is
/// needed.
/// </summary>
public sealed class SimEvent : ISimEvent<object>
{
    private readonly List<(IDispatcher Dispatcher, Action Handler)> _subs = new();

    /// <inheritdoc/>
    public void Register(object subscriber, IDispatcher subDispatcher, Action handler) => _subs.Add((subDispatcher, handler));

    /// <inheritdoc/>
    public void Trigger()
    {
        var subs = _subs;
        for (int i = 0; i < subs.Count; i++)
            subs[i].Dispatcher.Post(subs[i].Handler);
    }
}

/// <summary>
/// The stock, logic-free global event carrying a payload of type <typeparamref name="T"/>. See <see cref="SimEvent"/>.
/// </summary>
/// <typeparam name="T">The event payload type.</typeparam>
public sealed class SimEvent<T> : ISimEvent<object, T>
{
    private readonly List<(IDispatcher Dispatcher, Action<T> Handler)> _subs = new();

    /// <inheritdoc/>
    public void Register(object subscriber, IDispatcher subDispatcher, Action<T> handler) => _subs.Add((subDispatcher, handler));

    /// <inheritdoc/>
    public void Trigger(T arg)
    {
        var subs = _subs;
        for (int i = 0; i < subs.Count; i++)
        {
            var (dispatcher, handler) = subs[i];
            dispatcher.Post(() => handler(arg));
        }
    }
}
