namespace SimulaSharp.Kernel.Attributes;

/// <summary>
/// Declares that the decorated model holds an <b>intra-site reference</b> — a non-owning link to a list of models of
/// type <typeparamref name="T"/> that are <b>guaranteed to share this model's SimSite</b> — surfaced as a read-only
/// <c>IReadOnlyList&lt;T&gt;</c> accessor named <see cref="Name"/>. Because the targets run on the same thread, they are
/// reached by <b>direct synchronous calls</b>: the accessor holds the target instances themselves, not guarded Refs,
/// and there is no marshalling. Contrast <c>[CrossReference&lt;T&gt;]</c>, which may cross site boundaries and goes
/// through a generated async Ref.
/// <para>
/// The source generator emits a <c>public IReadOnlyList&lt;T&gt; {Name} { get; }</c> accessor on the (<c>partial</c>)
/// model, filled during assembly from this instance's <c>Refs</c> block in the YAML, keyed by <see cref="Name"/>
/// (targets given by full instance path). Filling happens in a second pass after construction — so cyclic same-site
/// references are fine — and the set is never mutated afterward. Every wired target is validated by the generator to
/// be a model assignable to <typeparamref name="T"/> <em>and</em> to reside on the same site as this model.
/// <typeparamref name="T"/> may be a concrete model class or an interface (there is no <c>IModel</c> constraint).
/// </para>
/// </summary>
/// <typeparam name="T">The referenced model type (a class or an interface).</typeparam>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class ReferenceAttribute<T>(string name) : Attribute
{
    /// <summary>The reference accessor name: the generated property's name and the YAML <c>Refs</c> key.</summary>
    public string Name { get; } = name;
}
