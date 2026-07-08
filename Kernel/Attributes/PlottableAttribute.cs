namespace SimulaSharp.Kernel.Attributes;

/// <summary>
/// Marks a model's numeric member as a plot-telemetry point. The source generator
/// collects every decorated member into the simulator's <c>Plottables</c> map (keyed by
/// full dotted instance path), where a plot consumer (the MMI) discovers and samples it.
/// <para>
/// Apply it to a <c>partial</c> property so the generated getter is a volatile read
/// (sampling is a lock-free poll). A property may carry both a knob attribute and
/// <see cref="PlottableAttribute"/> at once — they share the single volatile backing
/// field; the knob drives observation/control while the plottable feeds graphs.
/// </para>
/// <para>
/// Supported member types: any numeric (<c>int/long/float/double/…</c>), <c>bool</c>
/// (projected to 1.0/0.0), and enums (read as their underlying numeric).
/// </para>
/// </summary>
/// <param name="label">Display label for the plotted series.</param>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PlottableAttribute(string label) : Attribute
{
    /// <summary>Display label for the plotted series.</summary>
    public string Label { get; set; } = label;

    /// <summary>Optional unit for the plot axis (e.g. <c>"V"</c>, <c>"mA"</c>); null if unitless.</summary>
    public string? Unit { get; set; }
}
