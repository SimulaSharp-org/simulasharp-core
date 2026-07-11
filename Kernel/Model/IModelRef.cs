namespace SimulaSharp.Kernel.Model;

/// <summary>
/// Marker implemented by every generated <c>&lt;Model&gt;Ref</c> class (and by every companion
/// <c>&lt;Interface&gt;Ref</c> interface generated for an interface reference target). It lets
/// <see cref="Primitives.IReference{T}"/> constrain its element to a Ref, and lets the generated wiring pass
/// heterogeneous Refs through a single <see cref="Primitives.RefCollection"/> before downcasting each to its
/// reference's Ref type.
/// </summary>
public interface IModelRef;
