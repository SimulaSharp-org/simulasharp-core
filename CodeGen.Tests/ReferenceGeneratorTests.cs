namespace SimulaSharp.CodeGen.Tests;

[TestFixture]
public sealed class RefGeneratorTests
{
    private const string Model = """
        using SimulaSharp.Kernel.Logging;
        using SimulaSharp.Kernel.Model;
        using SimulaSharp.Kernel.Services;
        namespace Demo;
        public sealed class Tracker : ModelBase
        {
            public Tracker(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
            public double Interrogate(int ping) => ping;
            public int Value { get; set; }
        }
        """;

    [Test]
    public void Emits_a_Ref_wrapping_each_public_member()
    {
        var r = GeneratorHarness.Run(new RefGenerator(), Model);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("sealed class TrackerRef : global::SimulaSharp.Kernel.Model.IModelRef"));
            Assert.That(r.AllSource, Does.Contain("Interrogate(global::SimulaSharp.Kernel.Services.IScheduler callerScheduler, int ping)"));
            Assert.That(r.AllSource, Does.Contain("GetValue(global::SimulaSharp.Kernel.Services.IScheduler callerScheduler)"));
            Assert.That(r.AllSource, Does.Contain("SetValue(global::SimulaSharp.Kernel.Services.IScheduler callerScheduler"));
        });
    }

    [Test]
    public void Each_accessor_dispatches_same_site_directly_and_cross_site_via_inject()
    {
        var r = GeneratorHarness.Run(new RefGenerator(), Model);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("object.ReferenceEquals(callerScheduler, _target.Scheduler)"));
            Assert.That(r.AllSource, Does.Contain("_target.Scheduler.InjectAsync(() => _target.Interrogate(ping))"));
        });
    }

    [Test]
    public void ModelBase_plumbing_is_not_routed()
    {
        var r = GeneratorHarness.Run(new RefGenerator(), Model);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Not.Contain("Configure("));
            Assert.That(r.AllSource, Does.Not.Contain("GetTimeKeeper("));
            Assert.That(r.AllSource, Does.Not.Contain("GetScheduler("));
        });
    }
}

[TestFixture]
public sealed class ReferenceAccessorGeneratorTests
{
    private const string Model = """
        using SimulaSharp.Kernel.Logging;
        using SimulaSharp.Kernel.Model;
        using SimulaSharp.Kernel.Primitives;
        using SimulaSharp.Kernel.Services;
        namespace Demo;
        public sealed class Tracker : ModelBase
        {
            public Tracker(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
        }
        public partial class Hub : ModelBase
        {
            public Hub(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
            public Reference<Tracker> Peers { get; } = new();
        }
        """;

    [Test]
    public void Emits_an_accessor_field_and_wiring_per_reference()
    {
        var r = GeneratorHarness.Run(new ReferenceAccessorGenerator(), Model);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("global::SimulaSharp.Kernel.Model.IReferenceWiring"));
            Assert.That(r.AllSource, Does.Contain("ReferenceAccessor<global::Demo.TrackerRef> _Peers"));
            Assert.That(r.AllSource, Does.Contain("WireReferences()"));
            Assert.That(r.AllSource, Does.Contain("ReferenceAccessor.From(Peers, static __m => new global::Demo.TrackerRef(__m))"));
        });
    }
}
