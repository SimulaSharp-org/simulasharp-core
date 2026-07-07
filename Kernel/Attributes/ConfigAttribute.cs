namespace SimulaSharp.Kernel.Attributes;

/// <summary>
/// Marks a public field or property to be populated from this instance's
/// <c>Config</c> block in the assembly YAML. The source generator collects every
/// <see cref="ConfigAttribute"/>-marked member so the framework can bind (and
/// validate) the weakly-typed YAML values into strongly-typed members before
/// <c>Configure()</c> runs.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
public sealed class ConfigAttribute(string? key = null) : Attribute
{
    /// <summary>
    /// The config key to read from the assembly. Defaults to the member's own name
    /// when not given.
    /// </summary>
    public string? Key { get; } = key;

    /// <summary>
    /// Whether the assembly must supply a value. When true, a missing key is an
    /// error; when false, the member keeps its existing/default value. Defaults to true.
    /// </summary>
    public bool Required { get; set; } = true;
}
