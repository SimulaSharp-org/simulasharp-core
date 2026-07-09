namespace SimulaSharp.Kernel.Services;

/// <summary>
/// A scheduling domain that offers cross-thread marshalling (<see cref="IDispatcher"/>) and a
/// lifecycle (<see cref="ISite"/>), but <b>no</b> sim-time queue. It is what a service exposes as its
/// <c>Site</c>: enough for the interaction plumbing to marshal onto it and for the simulator to run
/// it. A <see cref="SimSite"/> is one (plus the sim-time queue); a <see cref="LooselyCoupledSite"/>
/// is one (clock-ungated, no queue).
/// </summary>
public interface IDispatcherSite : IDispatcher, ISite;
