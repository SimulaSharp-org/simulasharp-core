namespace SimulaSharp.Kernel.Parameters;

/// <summary>
/// Implemented (explicitly, by the source generator) by models/services that declare <c>[Parameter]</c>
/// members. The generated simulator calls <see cref="CollectParameters"/> while assembling (in its
/// constructor), passing the instance's full dotted path, so it can build its <see cref="ParameterInfo"/>
/// binders (stamped with that path) and add them to the simulator's parameter list.
/// </summary>
public interface IParameterProvider
{
    /// <summary>Builds this instance's parameters (paths prefixed with <paramref name="pathPrefix"/>) into <paramref name="into"/>.</summary>
    void CollectParameters(string pathPrefix, ICollection<ParameterInfo> into);
}
