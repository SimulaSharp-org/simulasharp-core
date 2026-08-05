namespace SimulaSharp.Kernel.Knobs;

/// <summary>
/// One selectable option of an <see cref="KnobKind.Enum"/> (dropdown) knob: an enum member's underlying integer
/// <see cref="Value"/> and its <see cref="Label"/> (the member name). A renderer builds each as
/// <c>&lt;option value="{Value}"&gt;{Label}&lt;/option&gt;</c>; the knob's read/written value is the selected
/// <see cref="Value"/>. Values are widened to <see cref="long"/> so one shape covers every integer underlying type.
/// </summary>
/// <param name="Value">The enum member's underlying integer value.</param>
/// <param name="Label">The enum member's name.</param>
public readonly record struct KnobOption(long Value, string Label);
