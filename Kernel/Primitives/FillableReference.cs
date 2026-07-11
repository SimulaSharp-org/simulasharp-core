using System.Collections;
using System.Collections.Generic;
using SimulaSharp.Kernel.Model;

namespace SimulaSharp.Kernel.Primitives;

/// <summary>
/// The fillable implementation of <see cref="IReference{T}"/>: a plain list of guarded Refs that the generated wiring
/// seam (<c>IReferenceWiring.WireReference</c>) populates once, during assembly. A model holds it typed as the
/// read-only <see cref="IReference{T}"/>, so <see cref="Add"/> is reachable only by downcasting to this concrete
/// type — which only the generated seam does. After wiring it is never touched again, so it behaves as a frozen list.
/// </summary>
/// <typeparam name="T">The generated Ref type, or a companion <c>&lt;Type&gt;Ref</c> interface for an interface target.</typeparam>
public sealed class FillableReference<T> : IReference<T> where T : IModelRef
{
    private readonly List<T> _items = new();

    /// <summary>Appends a guarded Ref. Called only by the generated wiring seam during assembly.</summary>
    public void Add(T item) => _items.Add(item);

    /// <summary>The Ref at <paramref name="index"/>.</summary>
    public T this[int index] => _items[index];

    /// <summary>The number of referenced Refs.</summary>
    public int Count => _items.Count;

    /// <inheritdoc/>
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
}
