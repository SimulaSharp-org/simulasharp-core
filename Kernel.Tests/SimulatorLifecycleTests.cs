using SimulaSharp.Kernel.Logging;
using SimulaSharp.Kernel.Services;

namespace SimulaSharp.Kernel.Tests;

/// <summary>
/// Exercises <see cref="SimulatorBase"/> lifecycle, discovery, and teardown through the hand-wired
/// <see cref="ProbeSimulator"/> (no codegen), covering the shared surface the MMI relies on. The topology is
/// assembled in the constructor — there is no separate <c>Initialize()</c> phase.
/// </summary>
[TestFixture]
[CancelAfter(15000)]   // safety net for the started-clock/site lifecycle tests
public sealed class SimulatorLifecycleTests
{
    private static long EpochUs =>
        (long)((new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds * 1_000_000.0);

    [Test]
    public void A_constructed_simulator_is_assembled_then_advances_to_Running_and_Stop_freezes()
    {
        using var sim = new ProbeSimulator();
        Assert.That(sim.State, Is.EqualTo(SimulatorState.Initialized), "the constructor should assemble the topology");

        sim.Configure();
        Assert.That(sim.State, Is.EqualTo(SimulatorState.Configured));

        sim.Start();
        Assert.Multiple(() =>
        {
            Assert.That(sim.State, Is.EqualTo(SimulatorState.Running));
            Assert.That(sim.IsRunning, Is.True);
        });

        sim.Stop();
        Assert.Multiple(() =>
        {
            Assert.That(sim.State, Is.EqualTo(SimulatorState.Running), "Stop should freeze without leaving Running");
            Assert.That(sim.IsRunning, Is.False);
        });
    }

    [Test]
    public void Start_before_Configure_is_guarded()
    {
        using var sim = new ProbeSimulator();
        Assert.That(sim.Start, Throws.InvalidOperationException, "Start ran before Configure");
    }

    [Test]
    public void Configure_runs_each_models_Configure()
    {
        using var sim = new ProbeSimulator();
        sim.Configure();
        Assert.That(sim.Model.Configured, Is.True);
    }

    [Test]
    public void Construction_discovers_knobs_and_a_registered_channel_receives_them_and_changes()
    {
        using var sim = new ProbeSimulator();       // knobs discovered during construction
        var channel = new RecordingKnobChannel();
        sim.RegisterKnobChannel(channel);

        Assert.Multiple(() =>
        {
            Assert.That(sim.Knobs, Has.Count.EqualTo(1));
            Assert.That(channel.Discovered, Has.Count.EqualTo(1));
            Assert.That(channel.Discovered[0].Path, Is.EqualTo("Sim.Probe.Speed"));
        });

        sim.Knobs[0].RaiseChanged();
        Assert.That(channel.Changes, Does.Contain("Sim.Probe.Speed"));
    }

    [Test]
    public void Construction_collects_plottables_and_SamplePlottables_stamps_mission_time()
    {
        using var sim = new ProbeSimulator();
        sim.Model.Load = 12.5;

        Assert.That(sim.Plottables.Keys, Does.Contain("Sim.Probe.Load"));

        var (missionUs, values) = sim.SamplePlottables();
        Assert.Multiple(() =>
        {
            Assert.That(values["Sim.Probe.Load"], Is.EqualTo(12.5));
            Assert.That(missionUs, Is.EqualTo(EpochUs), "sweep was not stamped with mission time (clock at horizon 0)");
        });
    }

    [Test]
    public void Parameters_are_discovered_and_settable_before_Configure_only()
    {
        using var sim = new ProbeSimulator();
        var speed = sim.GetParams().Single(p => p.Path == "Sim.Probe.RatedSpeed");
        Assert.That(speed.Read(), Is.EqualTo(100), "default parameter value");

        sim.SetParam(speed, 250);                       // allowed before Configure
        Assert.That(sim.Model.RatedSpeed, Is.EqualTo(250));

        sim.Configure();
        Assert.That(() => sim.SetParam(speed, 300), Throws.InvalidOperationException,
            "parameters must be fixed once configured");
        Assert.That(sim.Model.RatedSpeed, Is.EqualTo(250), "the rejected write must not take effect");
    }

    [Test]
    public void A_registered_log_sink_receives_component_logs()
    {
        using var sim = new ProbeSimulator();
        var sink = new CapturingSink();
        sim.RegisterLogSink(sink);

        sim.Model.Logger.LogInformation("running");

        Assert.That(WaitUntilCaptured(sink), Is.True);
        Assert.That(sink.Entries[0].Issuer, Is.EqualTo("Sim.Probe"));
    }

    [Test]
    public void Dispose_tears_down_the_models_and_the_clock()
    {
        var sim = new ProbeSimulator();
        sim.Configure();
        sim.Start();
        var model = sim.Model;

        sim.Dispose();

        Assert.That(model.Disposed, Is.True, "model was not disposed");
        Assert.That(sim.Start, Throws.TypeOf<ObjectDisposedException>(), "a disposed (single-use) simulator restarted");
    }

    private static bool WaitUntilCaptured(CapturingSink sink)
    {
        for (int i = 0; i < 100 && sink.Entries.Count == 0; i++) Thread.Sleep(2);
        return sink.Entries.Count > 0;
    }
}
