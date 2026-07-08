namespace SimulaSharp.Kernel.Knobs;

/// <summary>
/// A single observable/controllable knob on a model instance — the MMI's unit of
/// observation and control. It is a thin binder: metadata (for rendering) plus typed
/// read/write delegates the source generator supplies, and a <see cref="Changed"/>
/// notification.
/// <para>
/// Access is lock-free: the generated delegates do a typed <c>Volatile.Read</c> /
/// <c>Volatile.Write</c> on the model's backing field (a plain set is atomic on 64-bit),
/// so any thread can read the current value and write a new one. The model's own typed
/// property uses the field directly (no boxing); only the MMI path boxes.
/// </para>
/// </summary>
public sealed class Knob
{
    /// <summary>Full dotted instance path plus knob name, e.g. <c>SimName.Path.To.Model.Speed</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Display label.</summary>
    public required string Label { get; init; }

    /// <summary>Broad category (drives rendering).</summary>
    public required KnobKind Kind { get; init; }

    /// <summary>True for a display-only knob (the model drives it; the MMI cannot write).</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>The value's CLR type name (e.g. <c>System.Int32</c>); null for triggers.</summary>
    public string? ValueType { get; init; }

    /// <summary>Minimum for a numeric knob, if bounded.</summary>
    public double? Min { get; init; }

    /// <summary>Maximum for a numeric knob, if bounded.</summary>
    public double? Max { get; init; }

    /// <summary>Decimal places for a float knob.</summary>
    public int? DecimalPlaces { get; init; }

    /// <summary>Rendering style for a numeric knob (Input/Slider/Rotary).</summary>
    public string? Style { get; init; }

    /// <summary>Maximum text length for a text knob.</summary>
    public int? MaxLength { get; init; }

    /// <summary>Whether a text knob permits empty values.</summary>
    public bool? AllowEmpty { get; init; }

    /// <summary>Whether a text knob permits newlines.</summary>
    public bool? AllowNewLine { get; init; }

    /// <summary>Typed volatile read of the value, boxed. Null for triggers.</summary>
    public Func<object?>? ReadBoxed { get; init; }

    /// <summary>Typed volatile write of the value (unboxed inside). Null for read-only knobs and triggers.</summary>
    public Action<object?>? WriteBoxed { get; init; }

    /// <summary>Invokes a trigger knob (posts the method onto the model's site). Null for value knobs.</summary>
    public Action? Invoke { get; init; }

    /// <summary>Raised whenever the value changes (from either the model or a write).</summary>
    public event Action? Changed;

    /// <summary>Reads the current value (boxed).</summary>
    public object? Read() => ReadBoxed?.Invoke();

    /// <summary>
    /// Writes a new value (no-op for read-only). The generated <see cref="WriteBoxed"/> does the
    /// atomic volatile write and posts the change-notification onto the model's site, so any
    /// model subscribed to <see cref="Changed"/> runs on its own thread.
    /// </summary>
    public void Write(object? value) => WriteBoxed?.Invoke(value);

    /// <summary>Raises <see cref="Changed"/>. Called by the generated model setter and by <see cref="Write"/>.</summary>
    public void RaiseChanged() => Changed?.Invoke();
}
