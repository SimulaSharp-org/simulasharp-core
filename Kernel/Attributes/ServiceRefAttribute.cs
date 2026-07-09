namespace SimulaSharp.Kernel.Attributes;

/// <summary>
/// Marks a public field or property (typed as a service) as a <b>service dependency</b>.
/// The assembly's <c>Deps</c> block binds it by service name: the generated simulator
/// sets the member to the named service instance during assembly, so the holder can call
/// the service's generated interaction surface (<c>RegisterCallback_</c> / request
/// callers). The member must be settable.
/// <para>
/// A <c>[ServiceRef]</c> may live on a <b>model</b> or on another <b>service</b> — services
/// can depend on services, including <b>cyclically</b> (A→B and B→A). The simulator constructs
/// every service before binding any dependency, so a cycle resolves by setter injection.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class ServiceRefAttribute : Attribute;
