namespace SimulaSharp.Kernel.Attributes;

/// <summary>
/// Marks a <c>void</c> method on an interface used as a <c>[CrossReference&lt;T&gt;]</c> target as
/// <b>fire-and-forget</b>. On the generated <c>&lt;T&gt;Ref</c> the method returns <c>void</c>: a same-site caller
/// invokes the target directly (inline, immediately), while a cross-site caller <c>Post</c>s the call onto the target's
/// dispatcher and returns at once — there is no result to await. Use it for one-way notifications / commands (the
/// "notify" tier), where the async request/response surface (the default) would be needless overhead.
/// <para>
/// The method <b>must return <c>void</c></b> (there is nothing to hand back) and take no <c>out</c>/<c>ref</c>
/// parameters. This attribute is a pure code-generation hint on the interface contract; it is invisible to tooling that
/// reads the model's structure.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class TriggerableAttribute : Attribute;
