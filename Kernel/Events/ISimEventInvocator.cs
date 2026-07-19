using System;

namespace SimulaSharp.Kernel.Events;

/// <summary>
/// The producer capability of a global event: it can be <see cref="Trigger">triggered</see>, but not subscribed to.
/// This is the surface an <c>[EventReference]</c> member holds — a producer fires the event, while registration is the
/// simulator's job (driven by the assembly's <c>Subscriptions</c> block). Triggering is safe from any thread.
/// </summary>
public interface ISimEventInvocator
{
    /// <summary>Fires the event: every registered subscriber's handler is posted onto its own site (fire-and-forget).</summary>
    void Trigger();
}

/// <summary>
/// The producer capability of a global event carrying a payload of type <typeparamref name="T"/>. See
/// <see cref="ISimEventInvocator"/>.
/// </summary>
/// <typeparam name="T">The event payload type (contravariant — a producer of a wider payload satisfies a narrower one).</typeparam>
public interface ISimEventInvocator<in T>
{
    /// <summary>Fires the event with <paramref name="arg"/>: every subscriber's handler is posted, with the payload,
    /// onto its own site (fire-and-forget).</summary>
    void Trigger(T arg);
}
