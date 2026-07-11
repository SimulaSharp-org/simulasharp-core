using System.Collections.Generic;
using SimulaSharp.Kernel.Model;

namespace SimulaSharp.Kernel.Primitives;

/// <summary>
/// The read-only face of a <b>reference</b> association: the list of guarded <c>&lt;Type&gt;Ref</c> accessors a model
/// links to. This is the type a model's generated reference accessor is declared as, so a model can index and
/// enumerate the Refs (and call through them) but never mutate the set — the fillable side
/// (<see cref="FillableReference{T}"/>) is a separate type the model never sees. Covariant in
/// <typeparamref name="T"/> because it is purely a read surface.
/// </summary>
/// <typeparam name="T">The generated Ref type, or a companion <c>&lt;Type&gt;Ref</c> interface for an interface target.</typeparam>
public interface IReference<out T> : IReadOnlyList<T> where T : IModelRef
{
}
