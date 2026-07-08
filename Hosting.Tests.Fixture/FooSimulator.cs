using SimulaSharp.Kernel.Services;

// Global namespace, matching the generated <Name>Simulator convention, so the loader's
// reflection scan discovers it and the suffix-strip yields the name "Foo".
public sealed class FooSimulator : SimulatorBase
{
    public FooSimulator() : base(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)) { }

    // A pack with no topology — enough to exercise discovery, construction, and lifecycle.
    protected override void OnInitialize() { }
}
