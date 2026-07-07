namespace SimulaSharp.Kernel.Model;

/// <summary>
/// Marker implemented by every generated <c>&lt;Model&gt;Ref</c> class. Lets
/// <see cref="Primitives.ReferenceAccessor{T}"/> constrain its element to a Ref, and
/// lets the source generator tell a reference (<c>Reference&lt;IModel&gt;</c>) apart
/// from a reference accessor (<c>ReferenceAccessor&lt;IModelRef&gt;</c>).
/// </summary>
public interface IModelRef;
