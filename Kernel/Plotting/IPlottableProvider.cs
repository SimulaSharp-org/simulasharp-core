namespace SimulaSharp.Kernel.Plotting;

/// <summary>
/// Implemented (explicitly, by the source generator) by models <b>or services</b> that declare
/// <c>[Plottable]</c> members. The generated simulator calls
/// <see cref="CollectPlottables"/> during discovery, passing the instance's full dotted
/// path, so it can build its <see cref="Plottable"/> binders (stamped with that
/// path) and add them to the simulator's map.
/// </summary>
public interface IPlottableProvider
{
    /// <summary>Builds this model's plottables (paths prefixed with <paramref name="pathPrefix"/>) into <paramref name="into"/>, keyed by full path.</summary>
    void CollectPlottables(string pathPrefix, IDictionary<string, Plottable> into);
}
