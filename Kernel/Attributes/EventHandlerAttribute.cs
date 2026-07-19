using System;

namespace SimulaSharp.Kernel.Attributes;

/// <summary>
/// Marks a model method as a global-event subscription target. The method must be <c>void</c> and take either no
/// parameters (for an argless event) or a single parameter of the event's payload type. The assembly's
/// <c>Subscriptions</c> block binds a named event to such a method; the simulator registers it at assembly time so the
/// handler runs on the model's own site whenever the event fires.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class EventHandlerAttribute : Attribute;
