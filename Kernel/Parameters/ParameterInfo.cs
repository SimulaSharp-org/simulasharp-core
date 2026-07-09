namespace SimulaSharp.Kernel.Parameters;

/// <summary>
/// A single <b>build-time parameter</b> on a model/service instance — the host's unit of pre-run tuning. It is
/// a thin binder: metadata plus boxed read/write delegates the source generator supplies. Parameters are
/// discovered when the simulator is assembled (its constructor) and can be read and overridden via
/// <c>ISimulator.GetParams()</c> / <c>ISimulator.SetParam(...)</c> <b>before</b> <c>Configure()</c> — after which
/// the value is fixed for the run.
/// <para>
/// Unlike a <see cref="Knobs.Knob"/> (a runtime operator control), a parameter is set once, before the sim
/// threads start, so no cross-thread synchronization is needed; the delegates just read/write the member.
/// </para>
/// </summary>
public sealed class ParameterInfo
{
    /// <summary>Full dotted instance path plus the parameter name, e.g. <c>SimName.Path.To.Model.Rate</c>.</summary>
    public required string Path { get; init; }

    /// <summary>The parameter's member name (e.g. <c>Rate</c>).</summary>
    public required string Name { get; init; }

    /// <summary>The value's CLR type name (e.g. <c>System.Double</c>).</summary>
    public required string ValueType { get; init; }

    /// <summary>Reads the current value, boxed.</summary>
    public required Func<object?> ReadBoxed { get; init; }

    /// <summary>Writes a new value (unboxed to the member's type inside).</summary>
    public required Action<object?> WriteBoxed { get; init; }

    /// <summary>The declared default (from the attribute), boxed; null when the parameter has no default.</summary>
    public object? Default { get; init; }

    /// <summary>Reads the current value (boxed).</summary>
    public object? Read() => ReadBoxed();

    /// <summary>Writes a new value (boxed); the generated delegate unboxes to the member's type.</summary>
    public void Write(object? value) => WriteBoxed(value);
}
