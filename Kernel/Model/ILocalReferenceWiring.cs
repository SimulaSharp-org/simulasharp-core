using System.Collections.Generic;

namespace SimulaSharp.Kernel.Model;

/// <summary>
/// Implemented (explicitly, by the source generator) by models that declare <c>[Reference&lt;T&gt;]</c> members
/// (intra-site references). After the generated simulator has constructed every instance, it calls
/// <see cref="WireLocalReference"/> once per reference with the target model instances themselves — no Ref wrapping,
/// because the targets share this model's SimSite and are reached by direct synchronous calls. The model casts each
/// target to its reference's element type and fills the hidden backing list. Implemented explicitly, so it stays off
/// the model's public surface. (Contrast <see cref="IReferenceWiring"/>, the cross-site variant that passes guarded
/// Refs through a <see cref="Primitives.RefCollection"/>.)
/// </summary>
public interface ILocalReferenceWiring
{
    /// <summary>Fills the intra-site reference named <paramref name="member"/> from the target instances <paramref name="targets"/>.</summary>
    void WireLocalReference(string member, IReadOnlyList<IModel> targets);
}
