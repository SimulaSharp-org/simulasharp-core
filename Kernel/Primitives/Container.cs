using System.Collections;
using SimulaSharp.Kernel.Model;

namespace SimulaSharp.Kernel.Primitives;

/// <summary>
/// An <b>owning</b>, list-based association: the sub-models a parent model contains.
/// A contained model's subtree shares the parent's SimSite (assigned at construction
/// by the generated simulator), so this type carries no scheduling behaviour — it is
/// a typed, enumerable, tree-walkable collection that also signals "containment" to
/// the source generator (the type itself is the decoration; no attribute needed).
/// </summary>
/// <typeparam name="T">The contained model type.</typeparam>
public sealed class Container<T> : IReadOnlyList<T> where T : IModel
{
    private readonly List<T> _items = [];

    /// <summary>Adds a child. Called by the generated simulator while building the model tree.</summary>
    public void Add(T child) => _items.Add(child);

    /// <summary>The child at <paramref name="index"/>.</summary>
    public T this[int index] => _items[index];

    /// <summary>The number of contained children.</summary>
    public int Count => _items.Count;

    /// <inheritdoc/>
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
}
