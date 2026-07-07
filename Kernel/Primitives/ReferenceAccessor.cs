using System.Collections;
using SimulaSharp.Kernel.Model;

namespace SimulaSharp.Kernel.Primitives;

/// <summary>
/// The codegen-produced backing for a <see cref="Reference{T}"/> — e.g. a
/// <c>protected ReferenceAccessor&lt;TrackerRef&gt; _Trackers</c> generated for a
/// developer's <c>Reference&lt;Tracker&gt; Trackers</c>. It offers only
/// access/enumeration over the guarded <c>&lt;Model&gt;Ref</c> wrappers; the model's
/// own code calls referenced models through these Refs (passing its own scheduler).
/// Built via <see cref="ReferenceAccessor.From{TModel,TRef}"/>.
/// </summary>
/// <typeparam name="T">The generated Ref type.</typeparam>
public sealed class ReferenceAccessor<T> : IReadOnlyList<T> where T : IModelRef
{
    private readonly List<T> _refs;

    internal ReferenceAccessor(IEnumerable<T> refs) => _refs = new List<T>(refs);

    /// <summary>The Ref at <paramref name="index"/>.</summary>
    public T this[int index] => _refs[index];

    /// <summary>The number of referenced models.</summary>
    public int Count => _refs.Count;

    /// <inheritdoc/>
    public IEnumerator<T> GetEnumerator() => _refs.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _refs.GetEnumerator();
}

/// <summary>Factory that links a <see cref="Reference{T}"/> to its <see cref="ReferenceAccessor{T}"/>.</summary>
public static class ReferenceAccessor
{
    /// <summary>
    /// Wraps each model held by <paramref name="reference"/> in its Ref (via
    /// <paramref name="wrap"/>, a generated <c>m =&gt; new FooRef(m)</c>) and returns the
    /// access-only accessor. Called by the generated wiring after assembly completes.
    /// </summary>
    public static ReferenceAccessor<TRef> From<TModel, TRef>(Reference<TModel> reference, Func<TModel, TRef> wrap)
        where TModel : IModel
        where TRef : IModelRef
        => new(reference.Items.Select(wrap));
}
