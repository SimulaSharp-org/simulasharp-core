namespace SimulaSharp.Kernel.Attributes;

/// <summary>
/// Marks a public field or property (of a service type) as a <b>service dependency</b>.
/// The assembly's <c>Deps</c> block binds it by service name: the generated simulator
/// sets the member to the named service instance during assembly, so the model can call
/// the service's generated interaction surface (<c>RegisterCallback_</c> / request
/// callers). The member must be settable.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class ServiceRefAttribute : Attribute;
