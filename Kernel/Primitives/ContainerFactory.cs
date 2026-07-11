using System.Collections.ObjectModel;
using SimulaSharp.Kernel.Model;

namespace SimulaSharp.Kernel.Primitives;

/// <summary>
/// The build-time accumulator behind a <see cref="Container{T}"/>. It is deliberately <b>non-generic</b> — its
/// only job during assembly is to gather the contained children as <see cref="IModel"/> — so the generated
/// simulator uses one factory shape for every containment regardless of element type. The element type appears
/// only on <see cref="Build{T}"/>, which casts the gathered children to <c>T</c> a single time and returns a
/// frozen <see cref="Container{T}"/>. The generator emits <c>new ContainerFactory()</c>, a
/// sequence of <c>Add(child)</c> calls, then <c>Build&lt;T&gt;()</c> passed to the parent's <c>init</c> accessor.
/// </summary>
public sealed class ContainerFactory : Collection<IModel>
{
    /// <summary>
    /// Freezes the gathered children into a read-only <see cref="Container{T}"/>, casting each to
    /// <typeparamref name="T"/> once. The cast never fails at runtime: the generator has already checked (SIM008)
    /// that every child is assignable to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The container element type (a class or an interface the children implement).</typeparam>
    public Container<T> Build<T>()
    {
        var typed = new T[Count];
        for (int i = 0; i < Count; i++) typed[i] = (T)(object)this[i];
        return new Container<T>(typed);
    }
}
