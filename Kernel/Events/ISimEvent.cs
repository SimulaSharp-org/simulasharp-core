using System;
using SimulaSharp.Kernel.Model;

namespace SimulaSharp.Kernel.Events;

/// <summary>
/// A global, scheduler-less pub/sub event. Unlike a <see cref="IService">service</see> — which owns a scheduler and
/// serializes every interaction through it — an event is a passive fan-out: any thread may
/// <see cref="ISimEventInvocator.Trigger"/> it, and each subscriber's handler is posted onto that subscriber's own
/// site. It costs nothing at rest (no thread, no serial executor) and is multi-producer.
/// <para>
/// <typeparamref name="TSub"/> is the subscriber identity a custom event may key on for routing/filtering (a bus or
/// router); the stock <see cref="SimEvent"/> ignores it and it exists only for a uniform signature. Registration is
/// synchronous, at assembly time (the simulator wires it from the assembly's <c>Subscriptions</c> block, before the run
/// starts) — so the stock implementation needs no locking; a custom event that mutates routing state at trigger time
/// owns its own synchronization.
/// </para>
/// </summary>
/// <typeparam name="TSub">The subscriber type; constrained to <see cref="IModel"/> so it carries its own scheduler.</typeparam>
public interface ISimEvent<in TSub> : ISimEventInvocator where TSub : IModel
{
    /// <summary>Registers <paramref name="handler"/> on behalf of <paramref name="subscriber"/>; on trigger the handler
    /// is posted onto the subscriber's own site. Called by the simulator during assembly, never at run time.</summary>
    void Register(TSub subscriber, Action handler);
}

/// <summary>
/// A global, scheduler-less pub/sub event carrying a payload of type <typeparamref name="T"/>. See
/// <see cref="ISimEvent{TSub}"/>.
/// </summary>
/// <typeparam name="TSub">The subscriber type; constrained to <see cref="IModel"/> so it carries its own scheduler.</typeparam>
/// <typeparam name="T">The event payload type.</typeparam>
public interface ISimEvent<in TSub, T> : ISimEventInvocator<T> where TSub : IModel
{
    /// <summary>Registers <paramref name="handler"/> on behalf of <paramref name="subscriber"/>; on trigger the handler
    /// is posted, with the triggered payload, onto the subscriber's own site.</summary>
    void Register(TSub subscriber, Action<T> handler);
}
