using System.Collections.ObjectModel;
using SimulaSharp.Kernel.Model;

namespace SimulaSharp.Kernel.Primitives;

/// <summary>
/// The build-time accumulator the generated simulator fills with pre-wrapped <see cref="IModelRef"/> instances — one
/// guarded Ref per referenced target, constructed by the simulator because only it knows each target's concrete type
/// — and passes to a model's <c>IReferenceWiring.WireReference</c> seam, which downcasts each to that reference's Ref
/// type and stores it. Deliberately non-generic: it carries heterogeneous concrete Refs unified by
/// <see cref="IModelRef"/>, exactly as <see cref="ContainerFactory"/> carries children unified by <c>IModel</c>.
/// </summary>
public sealed class RefCollection : Collection<IModelRef>
{
}
