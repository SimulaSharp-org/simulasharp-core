namespace SimulaSharp.Kernel.Plotting;

/// <summary>
/// A single plot-telemetry point on a model instance — the plot consumer's unit of
/// observation. It is a thin binder: metadata (for axis rendering) plus a typed
/// <see cref="Sample"/> delegate the source generator supplies.
/// <para>
/// <see cref="Sample"/> does a lock-free volatile read of the model's backing field
/// (through the member's volatile getter) and projects it to <see cref="double"/>. The
/// consumer stamps one mission time per sweep (see the simulator's sampling helper), so
/// every point in a frame shares a consistent x-axis.
/// </para>
/// </summary>
public sealed class Plottable
{
    /// <summary>Full dotted instance path plus member name, e.g. <c>SimName.Path.To.Model.Vbus</c>. The map key.</summary>
    public required string Path { get; init; }

    /// <summary>Display label for the series.</summary>
    public required string Label { get; init; }

    /// <summary>Unit for the axis (e.g. <c>V</c>, <c>mA</c>); null if unitless.</summary>
    public string? Unit { get; init; }

    /// <summary>The source member's CLR type name (e.g. <c>System.Double</c>), before projection.</summary>
    public required string ValueType { get; init; }

    /// <summary>Volatile read of the current value, projected to <see cref="double"/>.</summary>
    public required Func<double> Sample { get; init; }
}
