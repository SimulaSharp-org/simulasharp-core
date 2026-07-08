namespace SimulaSharp.Kernel.Knobs;

/// <summary>
/// The MMI-implemented sink for knob observability and control (registered on the
/// simulator, like <c>ILogSink</c>). On registration the simulator hands it the
/// discovered knobs, then pushes a notification whenever any knob's value changes.
/// <para>
/// Both callbacks run on a model's <b>site thread</b>, so implementations must be
/// non-blocking — enqueue and return. The MMI reads and writes values through the
/// <see cref="Knob"/> objects it receives (<see cref="Knob.Read"/> / <see cref="Knob.Write"/>).
/// </para>
/// </summary>
public interface IKnobChannel
{
    /// <summary>
    /// Delivers the simulation's knobs. Called once per channel — at registration if
    /// discovery has already happened, otherwise when it does.
    /// </summary>
    void OnKnobsDiscovered(IReadOnlyList<Knob> knobs);

    /// <summary>Called when a knob's value changes, on the owning model's site thread.</summary>
    void OnKnobChanged(Knob knob);
}
