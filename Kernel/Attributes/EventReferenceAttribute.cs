using System;
using SimulaSharp.Kernel.Events;

namespace SimulaSharp.Kernel.Attributes;

/// <summary>
/// Marks a model or service property or field as a producer handle to a global event. The member must be typed as an
/// <see cref="ISimEventInvocator"/> (argless) or <see cref="ISimEventInvocator{T}"/> (with payload). The assembly's
/// <c>Triggers</c> block binds a named event to the member; the simulator fills it with the shared event instance so the
/// model can <c>Trigger</c> it. The invocator surface is trigger-only — a producer cannot register subscribers, which
/// keeps subscription the simulator's exclusive job.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class EventReferenceAttribute : Attribute;
