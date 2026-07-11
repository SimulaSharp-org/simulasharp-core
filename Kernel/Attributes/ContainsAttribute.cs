using SimulaSharp.Kernel.Primitives;

namespace SimulaSharp.Kernel.Attributes;

/// <summary>
/// Declares that the decorated model <b>contains</b> a list of sub-models of type <typeparamref name="T"/>,
/// surfaced as a read-only <see cref="Container{T}"/> accessor named <see cref="Name"/>. Containment is the
/// owning parent→child association: the contained subtree shares the parent's SimSite, so children are reached
/// by direct (same-thread) calls — no <c>Ref</c> wrapper.
/// <para>
/// The source generator emits a <c>public Container&lt;T&gt; {Name} { get; init; }</c> property on the
/// (<c>partial</c>) model and fills it during assembly from this instance's <c>Models</c> block in the YAML,
/// keyed by <see cref="Name"/>. The accessor is read-only and <c>init</c>-only, so a model may enumerate its
/// children but never mutate the set after assembly. <typeparamref name="T"/> may be a concrete model class or
/// an interface (there is <b>no</b> <c>IModel</c> constraint — a framework-pure capability interface is fine);
/// every concrete child wired in is checked by the generator to be a model assignable to <typeparamref name="T"/>.
/// </para>
/// </summary>
/// <typeparam name="T">The contained sub-model type (a class or an interface).</typeparam>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class ContainsAttribute<T>(string name) : Attribute
{
    /// <summary>The container accessor name: the generated property's name and the YAML <c>Models</c> key.</summary>
    public string Name { get; } = name;
}
