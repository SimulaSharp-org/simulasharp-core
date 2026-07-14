namespace SimulaSharp.Kernel.Attributes;

/// <summary>
/// Marks a <b>get-only</b> property on an interface used as a <c>[CrossReference&lt;T&gt;]</c> target as a
/// <b>live gauge</b>. On the generated <c>&lt;T&gt;Ref</c> the property is a plain synchronous getter that reads the
/// target directly — no dispatch, no <c>Task</c>, no marshalling, no cache — so a caller on any thread reads the freshest
/// value with a single lock-free memory read (the "gauge" tier). Use it for a continuously-changing scalar (e.g. a live
/// power draw) that would be wasteful to pull through the async request/response surface.
/// <para>
/// The property <b>must be get-only</b> and of an <b>atomically-readable</b> type (a word-size primitive, an <c>enum</c>,
/// or a reference) — a larger struct can tear under a cross-thread read. Its freshness guarantee is <em>producer-side</em>:
/// the implementing model must back the getter with a volatile read of a single-producer field. A gauge read is <b>not</b>
/// ordered against <c>[Triggerable]</c> notifications — it bypasses the dispatcher entirely. This attribute is a pure
/// code-generation hint on the interface contract; it is invisible to tooling that reads the model's structure.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class VolatileAccessAttribute : Attribute;
