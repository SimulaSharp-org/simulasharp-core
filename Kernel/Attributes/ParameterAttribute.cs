namespace SimulaSharp.Kernel.Attributes;

/// <summary>
/// Marks a public field or property as a <b>parameter</b>: a build-time tunable. It is populated from this
/// instance's <c>Config</c> block in the assembly YAML and can be overridden programmatically via
/// <c>ISimulator.SetParam</c> <b>before</b> <c>Configure()</c> runs. The source generator collects every
/// <see cref="ParameterAttribute"/>-marked member so the framework can bind the (weakly-typed) YAML value into
/// the strongly-typed member and expose it through <c>ISimulator.GetParams()</c>.
/// <para>
/// The <b>default</b> — applied when the assembly omits this parameter — is supplied in the attribute
/// constructor: <c>[Parameter(1.5)]</c>, <c>[Parameter("")]</c>, <c>[Parameter(false)]</c>. Parameters are
/// therefore <b>optional by default</b>; set <see cref="Required"/> to force the assembly to provide a value.
/// A parameter member must be a value/constant type (a number, <c>bool</c>, <c>enum</c>, <c>char</c> or
/// <c>string</c>), and it may <em>also</em> carry <c>[Knob]</c> and/or <c>[Plottable]</c>.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
public sealed class ParameterAttribute(object? @default = null) : Attribute
{
    /// <summary>
    /// The value applied when the assembly omits this parameter (given as the constructor argument). Null
    /// means no default was supplied — the member keeps its own initializer/default, unless <see cref="Required"/>.
    /// </summary>
    public object? Default { get; } = @default;

    /// <summary>The assembly <c>Config</c> key to read. Defaults to the member's own name when not given.</summary>
    public string? Key { get; set; }

    /// <summary>
    /// Whether the assembly <b>must</b> supply a value. When true, a missing key is an error; when false
    /// (the default), a missing key falls back to <see cref="Default"/> (or the member's own default).
    /// </summary>
    public bool Required { get; set; }
}
