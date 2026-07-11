namespace SimulaSharp.Kernel.Attributes;

/// <summary>
/// Declares that the decorated model holds a <b>reference</b> — a non-owning link — to a list of models of type
/// <typeparamref name="T"/>, surfaced as a read-only <c>IReference&lt;T&gt;Ref</c> accessor named <see cref="Name"/>
/// whose elements are the generated guarded <c>&lt;T&gt;Ref</c> accessors. A reference may cross site boundaries, so
/// calls go through those Refs (same-site calls resolve directly; cross-site calls marshal onto the target's site).
/// <para>
/// The source generator emits a <c>public IReference&lt;{T}Ref&gt; {Name} { get; }</c> accessor on the
/// (<c>partial</c>) model, backed by a hidden fillable list the generated simulator populates during assembly from
/// this instance's <c>Refs</c> block in the YAML, keyed by <see cref="Name"/> (targets given by full instance path).
/// The accessor is read-only. <typeparamref name="T"/> may be a concrete model class or an interface (there is no
/// <c>IModel</c> constraint); when it is an interface, the generator mints a companion <c>&lt;T&gt;Ref</c> interface
/// that every referenced model's Ref implements, so the reference is polymorphic over all implementations.
/// </para>
/// </summary>
/// <typeparam name="T">The referenced model type (a class or an interface).</typeparam>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class ReferencesAttribute<T>(string name) : Attribute
{
    /// <summary>The reference accessor name: the generated property's name and the YAML <c>Refs</c> key.</summary>
    public string Name { get; } = name;
}
