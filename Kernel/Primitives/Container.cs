using System.Collections;

namespace SimulaSharp.Kernel.Primitives;

/// <summary>
/// An <b>owning</b>, list-based association: the sub-models a parent model contains, declared via
/// <c>[Contains&lt;T&gt;("Name")]</c> on the model. A contained model's subtree shares the parent's SimSite
/// (assigned at construction by the generated simulator), so this type carries no scheduling behaviour.
/// <para>
/// It is a <b>frozen, read-only list</b>: the generated simulator builds it once through a
/// <see cref="ContainerFactory"/> (which casts the collected children to <typeparamref name="T"/> a single
/// time) and hands it to the parent through an <c>init</c>-only accessor. There is no public <c>Add</c> — a
/// model can enumerate and index its children but never mutate the set after assembly. There is no generic
/// constraint on <typeparamref name="T"/>; the source generator guarantees every element is a model
/// assignable to <typeparamref name="T"/>, so this stays a plain typed read-only list.
/// </para>
/// </summary>
/// <typeparam name="T">The contained model type (a class or an interface).</typeparam>
public sealed class Container<T> : IReadOnlyList<T>
{
    /// <summary>The empty container — the default an <c>init</c>-only accessor holds until the simulator fills it.</summary>
    public static readonly Container<T> Empty = new(System.Array.Empty<T>());

    private readonly IReadOnlyList<T> _items;

    /// <summary>Built only by <see cref="ContainerFactory.Build{T}"/>, which supplies the frozen, typed items.</summary>
    internal Container(IReadOnlyList<T> items) => _items = items;

    /// <summary>The child at <paramref name="index"/>.</summary>
    public T this[int index] => _items[index];

    /// <summary>The number of contained children.</summary>
    public int Count => _items.Count;

    /// <inheritdoc/>
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
}
