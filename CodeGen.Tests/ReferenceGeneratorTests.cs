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

    [Test]
    public void An_interface_reference_target_mints_a_companion_interface_its_impls_Ref_implements()
    {
        // IPowerSource is a framework-pure interface used as a [References<IPowerSource>] target; Panel implements
        // it. The companion IPowerSourceRef must be minted, and PanelRef must implement it.
        const string source = """
            using SimulaSharp.Kernel.Attributes;
            using SimulaSharp.Kernel.Logging;
            using SimulaSharp.Kernel.Model;
            using SimulaSharp.Kernel.Services;
            namespace Demo;
            public interface IPowerSource { double AvailablePowerW { get; } }
            public sealed class Panel : ModelBase, IPowerSource
            {
                public Panel(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
                public double AvailablePowerW => 0;
            }
            [References<IPowerSource>("Sources")]
            public partial class Grid : ModelBase
            {
                public Grid(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
            }
            """;
        var r = GeneratorHarness.Run(new RefGenerator(), source);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("interface IPowerSourceRef : global::SimulaSharp.Kernel.Model.IModelRef"));
            Assert.That(r.AllSource, Does.Contain("GetAvailablePowerW(global::SimulaSharp.Kernel.Services.IScheduler callerScheduler);"));
            Assert.That(r.AllSource, Does.Contain("sealed class PanelRef : global::SimulaSharp.Kernel.Model.IModelRef, global::Demo.IPowerSourceRef"));
        });
    }
}

[TestFixture]
public sealed class ReferenceGeneratorTests
{
    private const string Model = """
        using SimulaSharp.Kernel.Attributes;
        using SimulaSharp.Kernel.Logging;
        using SimulaSharp.Kernel.Model;
        using SimulaSharp.Kernel.Services;
        namespace Demo;
        public sealed class Tracker : ModelBase
        {
            public Tracker(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
        }
        [References<Tracker>("Peers")]
        public partial class Hub : ModelBase
        {
            public Hub(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
        }
        """;

    [Test]
    public void Emits_a_readonly_IReference_accessor_and_a_wire_seam_per_reference()
    {
        var r = GeneratorHarness.Run(new ReferenceGenerator(), Model);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("global::SimulaSharp.Kernel.Model.IReferenceWiring"));
            Assert.That(r.AllSource, Does.Contain("global::SimulaSharp.Kernel.Primitives.IReference<global::Demo.TrackerRef> Peers"));
            Assert.That(r.AllSource, Does.Contain("new global::SimulaSharp.Kernel.Primitives.FillableReference<global::Demo.TrackerRef>()"));
            Assert.That(r.AllSource, Does.Contain(".WireReference(string member, global::SimulaSharp.Kernel.Primitives.RefCollection refs)"));
            Assert.That(r.AllSource, Does.Contain("(global::Demo.TrackerRef)__r"));
        });
    }
}
