namespace SimulaSharp.Kernel.Attributes;

/// <summary>
/// Marks a service method as a named <b>entry point</b> (a request/response channel
/// from models into the service). The decorated handler is written by the service
/// author and must be named <c>Handle_&lt;RequestName&gt;</c>; the source generator
/// derives the request name by stripping the <c>Handle_</c> prefix (unless
/// <see cref="Name"/> overrides it) and emits the public cross-thread caller that
/// models use.
/// <para>
/// The handler may take at most one argument and may return a result. The generated
/// caller marshals the call onto the service's own site (scheduler) and, when the
/// handler returns a value, exposes it as an awaitable result.
/// </para>
/// <example>
/// <code>
/// [ServiceRequest]                              // request "TriggerSomething"
/// protected void Handle_TriggerSomething() { }
///
/// [ServiceRequest]                              // request "UpdateAValue"
/// protected void Handle_UpdateAValue(int val) { }
/// </code>
/// </example>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ServiceRequestAttribute(string? name = null) : Attribute
{
    /// <summary>
    /// Optional explicit request name. When null, the generator infers it from the
    /// handler method name by stripping the <c>Handle_</c> prefix.
    /// </summary>
    public string? Name { get; } = name;
}
