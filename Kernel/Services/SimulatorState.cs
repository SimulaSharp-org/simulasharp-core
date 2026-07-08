namespace SimulaSharp.Kernel.Services;

/// <summary>
/// The lifecycle state of a simulation, advanced by the <see cref="ISimulator"/>
/// steps. The states are a strict progression.
/// </summary>
public enum SimulatorState
{
    /// <summary>Not yet initialized — nothing has been built.</summary>
    Initial,

    /// <summary>
    /// Models constructed and their containers/references assembled, then config
    /// values applied — the result of <see cref="ISimulator.Initialize"/>.
    /// </summary>
    Initialized,

    /// <summary>Every component's <c>Configure()</c> has run, depth-first — the result of <see cref="ISimulator.Configure"/>.</summary>
    Configured,

    /// <summary>
    /// The run phase, entered by <see cref="ISimulator.Start"/>. Use
    /// <see cref="ISimulator.IsRunning"/> for the live clock status, since
    /// <see cref="ISimulator.Stop"/> freezes without leaving this state.
    /// </summary>
    Running
}
