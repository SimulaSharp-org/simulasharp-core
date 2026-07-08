namespace SimulaSharp.Kernel.Knobs;

/// <summary>The broad category of a <see cref="Knob"/>, telling the MMI how to render it.</summary>
public enum KnobKind
{
    /// <summary>A boolean toggle (tick box).</summary>
    Bool,

    /// <summary>A numeric value (int/long/float/double); see the knob's metadata for range/style.</summary>
    Number,

    /// <summary>A text value.</summary>
    Text,

    /// <summary>A method trigger (no value).</summary>
    Trigger
}
