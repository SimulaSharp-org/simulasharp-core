using SimulaSharp.Kernel.Model;

namespace SimulaSharp.Kernel.Primitives;

/// <summary>
/// The public face of a <b>non-owning</b>, list-based association between models —
/// what the developer declares (e.g. <c>public Reference&lt;Tracker&gt; Trackers</c>).
/// It offers only <see cref="Add"/>, used by the generated simulator while wiring the
/// assembly; the developer never reads models out of it directly. Access goes through
/// the codegen-produced <see cref="ReferenceAccessor{T}"/> instead, which yields the
/// guarded <c>&lt;Model&gt;Ref</c> wrappers. The type itself is the "reference"
/// decoration (no attribute needed).
/// </summary>
/// <typeparam name="T">The referenced model type.</typeparam>
public sealed class Reference<T> where T : IModel
{
    private readonly List<T> _items = [];

    /// <summary>Adds a referenced model. Called by the generated simulator during assembly wiring.</summary>
    public void Add(T target) => _items.Add(target);

    /// <summary>Framework access to the referenced models (used to build the <see cref="ReferenceAccessor{T}"/>).</summary>
    internal IReadOnlyList<T> Items => _items;
}
