namespace SimulaSharp.Kernel.Attributes;


/// <summary>
/// Decorates model members that should be exposed as knobs in the UI.
/// </summary>
/// <param name="label">The knob label</param>
public abstract class KnobAttribute(string label) : Attribute
{
    public string Label { get; set; } = label;
    public bool IsReadOnly { get; set; } = false;
}

/// <summary>
/// Represents a text-based knob UI element, allowing customization of properties such as
/// whether empty values, new lines, or a specific maximum text length are permitted.
/// </summary>
/// <param name="label">The label displayed for the text knob in the UI.</param>\
/// <remarks>
/// In ReadOnly mode, it's just a label.
/// In Read/Write mode, it's a text input field that, depending on the `AllowNewLine` property, may appear as a text area.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class TextKnobAttribute(string label) : KnobAttribute(label)
{
    public bool AllowEmpty { get; set; } = true;
    public bool AllowNewLine { get; set; } = false;
    public int MaxLength { get; set; } = int.MaxValue;
}

/// <summary>
/// Represents a UI element as a tick box knob, allowing users to toggle a boolean value.
/// </summary>
/// <param name="label">The label displayed for the tick box in the UI.</param>
/// <remarks>
/// In ReadOnly mode, it's just a light indicator.
/// In Read/Write mode, it's a proper checkbox that be toggled by both the user and the model.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
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

public enum NumberKnobType
{
    Input,
    Slider,
    Rotary
}

/// <summary>
/// Decorates model members that represent integer values to be exposed as knobs in the UI.
/// </summary>
/// <param name="label">The knob label</param>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class IntegerKnobAttribute(string label) : KnobAttribute(label)
{
    public NumberKnobType KnobType { get; set; } = NumberKnobType.Input;
    public long Min { get; set; } = long.MinValue;
    public long Max { get; set; } = long.MaxValue;
}

/// <summary>
/// Decorates model members to be represented as floating-point knobs in the UI.
/// Supports options for input precision, range, and decimal formatting.
/// </summary>
/// <param name="label">The label for the knob.</param>
public class FloatKnobAttribute(string label) : KnobAttribute(label)
{
    public NumberKnobType KnobType { get; set; } = NumberKnobType.Input;
    public float Min { get; set; } = float.MinValue;
    public float Max { get; set; } = float.MaxValue;
    public uint DecimalPlaces { get; set; } = 2;
}

