namespace SimulaSharp.Kernel.Attributes;

/// <summary>
/// Declares a named <b>publish point</b> on a service (a pub/sub channel from the
/// service out to models). Applied at class level and repeatable — a service may
/// expose many events. The service never writes any code for it; the source
/// generator owns both sides.
/// <para>
/// For <c>[ServiceEvent("SomeValueUpdated", typeof(int))]</c> the generator emits:
/// <list type="bullet">
///   <item><c>protected void Trigger_SomeValueUpdated(int value)</c> — the service
///   calls this internally to publish an update.</item>
///   <item><c>public void Register_SomeValueUpdated(IScheduler scheduler, Action&lt;int&gt; callback)</c>
///   — a model calls this to subscribe. The subscriber passes its own scheduler so
///   the service can deliver the callback onto the subscriber's site cross-thread.</item>
/// </list>
/// A payload-less event (<see cref="ArgType"/> null) generates the argument-free forms.
/// </para>
/// <para>
/// When a <see cref="ResultType"/> is given the callback returns a value: the
/// registered callback becomes a <c>Func&lt;arg, result&gt;</c> and <c>Trigger_</c>
/// surfaces that result back to the service (marshalled from the subscriber's site).
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ServiceEventAttribute(string name, Type? argType = null, Type? resultType = null) : Attribute
{
    /// <summary>The event name; used to name the generated <c>Trigger_</c>/<c>Register_</c> members.</summary>
    public string Name { get; } = name;

    /// <summary>The single payload type, or null for a payload-less event.</summary>
    public Type? ArgType { get; } = argType;

    /// <summary>The result type the subscriber returns, or null for a fire-and-forget event.</summary>
    public Type? ResultType { get; } = resultType;
}
