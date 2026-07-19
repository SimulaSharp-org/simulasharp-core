using System;
using System.Collections.Generic;
using SimulaSharp.Kernel.Model;
using SimulaSharp.Kernel.Services;

namespace SimulaSharp.Kernel.Events;

/// <summary>
/// The stock, logic-free global event (payload-less). Registration records each handler against the scheduler it is to
/// run on; a <see cref="Trigger"/> posts every handler onto that scheduler. The <c>subscriber</c> identity itself is
/// unused — it is accepted only for uniformity with routing events. Because every <see cref="Register"/> runs
/// synchronously at assembly time (before the run starts) and triggering only reads the subscriber list, no locking is
/// needed.
/// </summary>
public sealed class SimEvent : ISimEvent<IModel>
{
    private readonly List<(IScheduler Scheduler, Action Handler)> _subs = new();

    /// <inheritdoc/>
    public void Register(IModel subscriber, IScheduler subScheduler, Action handler) => _subs.Add((subScheduler, handler));

    /// <inheritdoc/>
    public void Trigger()
    {
        var subs = _subs;
        for (int i = 0; i < subs.Count; i++)
            subs[i].Scheduler.Post(subs[i].Handler);
    }
}

/// <summary>
/// The stock, logic-free global event carrying a payload of type <typeparamref name="T"/>. See <see cref="SimEvent"/>.
/// </summary>
/// <typeparam name="T">The event payload type.</typeparam>
public sealed class SimEvent<T> : ISimEvent<IModel, T>
{
    private readonly List<(IScheduler Scheduler, Action<T> Handler)> _subs = new();

    /// <inheritdoc/>
    public void Register(IModel subscriber, IScheduler subScheduler, Action<T> handler) => _subs.Add((subScheduler, handler));

    /// <inheritdoc/>
    public void Trigger(T arg)
    {
        var subs = _subs;
        for (int i = 0; i < subs.Count; i++)
        {
            var (scheduler, handler) = subs[i];
            scheduler.Post(() => handler(arg));
        }
    }
}
