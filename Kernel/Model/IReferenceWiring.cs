using SimulaSharp.Kernel.Primitives;

namespace SimulaSharp.Kernel.Model;

/// <summary>
/// Implemented (explicitly, by the source generator) by models that declare <c>[References&lt;T&gt;]</c> members.
/// After the generated simulator has constructed every instance, it calls <see cref="WireReference"/> once per
/// reference with a <see cref="RefCollection"/> of the pre-wrapped target Refs (the simulator builds each Ref because
/// only it knows the target's concrete type). The model downcasts each Ref to its reference's element type and fills
/// the hidden <see cref="FillableReference{T}"/> backing. Implemented explicitly, so it stays off the model's public
/// surface.
/// </summary>
public interface IReferenceWiring
{
    /// <summary>Fills the reference named <paramref name="member"/> from the pre-wrapped <paramref name="refs"/>.</summary>
    void WireReference(string member, RefCollection refs);
}
