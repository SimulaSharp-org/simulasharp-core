namespace SimulaSharp.Kernel.Knobs;

/// <summary>
/// Implemented (explicitly, by the source generator) by models that declare knob
/// members. The generated simulator calls <see cref="CollectKnobs"/> during discovery,
/// passing the model's full dotted path, so the model can build its <see cref="Knob"/>
/// binders (stamped with that path) and add them to the simulator's cached list.
/// </summary>
public interface IKnobProvider
{
    /// <summary>Builds this model's knobs (paths prefixed with <paramref name="pathPrefix"/>) into <paramref name="into"/>.</summary>
    void CollectKnobs(string pathPrefix, ICollection<Knob> into);
}
