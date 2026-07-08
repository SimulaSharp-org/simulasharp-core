namespace SimulaSharp.Kernel.Model;

/// <summary>
/// Implemented (explicitly, by the source generator) by models that declare
/// <see cref="Primitives.Reference{T}"/> members. The generated simulator calls
/// <see cref="WireReferences"/> after it has populated every model's reference
/// collections, so each model can build its <see cref="Primitives.ReferenceAccessor{T}"/>
/// backings over the now-complete set of targets. It is implemented explicitly, so it
/// does not appear on the model's public surface.
/// </summary>
public interface IReferenceWiring
{
    /// <summary>Builds this model's reference accessors from its populated reference collections.</summary>
    void WireReferences();
}
