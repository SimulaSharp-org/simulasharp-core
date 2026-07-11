namespace SimulaSharp.Kernel.Attributes;

/// <summary>
/// Base for attributes that decorate model members to be exposed as knobs in the UI.
/// The source generator materializes the UI binding (and, for read/write knobs, the
/// two-way sync) for each decorated member.
/// </summary>
/// <param name="label">The knob label.</param>
public abstract class KnobAttribute(string label) : Attribute
{
    /// <summary>The label shown for the knob in the UI.</summary>
    public string Label { get; set; } = label;

    /// <summary>
    /// When true the knob is display-only (RO — a label/indicator); when false it is
    /// read/write (RW — the operator, as well as the model, can drive it).
    /// </summary>
    public bool IsReadOnly { get; set; } = false;
}

/// <summary>
/// Represents a text-based knob UI element, allowing customization of properties such as
/// whether empty values, new lines, or a specific maximum text length are permitted.
/// </summary>
/// <param name="label">The label displayed for the text knob in the UI.</param>
/// <remarks>
/// In ReadOnly mode, it's just a label.
/// In Read/Write mode, it's a text input field that, depending on the <see cref="AllowNewLine"/> property, may appear as a text area.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public class TextKnobAttribute(string label) : KnobAttribute(label)
{
    /// <summary>Whether an empty value is permitted (RW).</summary>
    public bool AllowEmpty { get; set; } = true;

    /// <summary>Whether newlines are permitted; when true the input renders as a text area (RW).</summary>
    public bool AllowNewLine { get; set; } = false;

    /// <summary>Maximum permitted text length.</summary>
    public int MaxLength { get; set; } = int.MaxValue;
}

/// <summary>
/// Represents a UI element as a tick box knob, allowing users to toggle a boolean value.
/// </summary>
/// <param name="label">The label displayed for the tick box in the UI.</param>
/// <remarks>
/// In ReadOnly mode, it's just a light indicator.
/// In Read/Write mode, it's a proper checkbox that can be toggled by both the user and the model.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public class TickBoxKnobAttribute(string label) : KnobAttribute(label);

/// <summary>
/// Represents a trigger-type knob UI element, intended for executing actions or methods
/// within a model when interacted with in the UI. Trigger knobs wrap the actual method within code
/// responsible for executing the method when triggered, as well as informing UI that the method has been triggered,
/// when this happens from the model side.
/// </summary>
/// <param name="label">The label displayed for the trigger in the UI.</param>
/// <remarks>
/// This attribute is used to decorate methods that should be exposed as actionable triggers,
/// typically represented as light-bearing button in RW mode (lights up when triggered regardless of the caller)
/// and as a plain light indicator in RO mode.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public class TriggerKnobAttribute(string label) : KnobAttribute(label);

/// <summary>How a numeric knob is presented in the UI.</summary>
public enum NumberKnobType
{
    /// <summary>A plain numeric input field.</summary>
    Input,

    /// <summary>A linear slider between the knob's min and max.</summary>
    Slider,

    /// <summary>A rotary dial between the knob's min and max.</summary>
    Rotary
}

/// <summary>
/// Decorates a model member that represents a <b>numeric</b> value to be exposed as a knob in the UI. One generic
/// attribute covers every numeric type <typeparamref name="T"/> — there is no separate integer vs floating-point
/// attribute. <see cref="Min"/>/<see cref="Max"/> are typed as <typeparamref name="T"/>, and precision
/// (<see cref="DecimalPlaces"/>) simply carries through: it is a no-op the UI ignores when <typeparamref name="T"/>
/// is an integer type, so the same attribute reads naturally for both <c>[NumberKnob&lt;int&gt;("Mode", Max = 3)]</c>
/// and <c>[NumberKnob&lt;double&gt;("Vbus", Max = 32, DecimalPlaces = 2)]</c>.
/// </summary>
/// <typeparam name="T">The numeric CLR type of the member (e.g. <c>int</c>, <c>long</c>, <c>double</c>, <c>float</c>).</typeparam>
/// <param name="label">The knob label.</param>
[AttributeUsage(AttributeTargets.Property)]
public class NumberKnobAttribute<T>(string label) : KnobAttribute(label)
{
    /// <summary>How the knob is presented.</summary>
    public NumberKnobType KnobType { get; set; } = NumberKnobType.Input;

    /// <summary>Minimum permitted value (unbounded when unset).</summary>
    public T Min { get; set; } = default!;

    /// <summary>Maximum permitted value (unbounded when unset).</summary>
    public T Max { get; set; } = default!;

    /// <summary>Number of decimal places shown/accepted. A no-op for integer <typeparamref name="T"/>.</summary>
    public uint DecimalPlaces { get; set; } = 2;
}
