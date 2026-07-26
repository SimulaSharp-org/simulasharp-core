namespace SimulaSharp.CodeGen.Tests;

[TestFixture]
public sealed class RefGeneratorTests
{
    // An interface used as a [CrossReference<I>] target, exercising all three member tiers.
    private const string Source = """
        using SimulaSharp.Kernel.Attributes;
        using SimulaSharp.Kernel.Logging;
        using SimulaSharp.Kernel.Model;
        using SimulaSharp.Kernel.Services;
        namespace Demo;
        public interface IProbe
        {
            double Interrogate(int ping);                // default → async request/response
            [VolatileAccess] double LiveValue { get; }   // gauge → synchronous getter
            [Triggerable] void Nudge(int by);            // notify → fire-and-forget void
        }
        public sealed partial class Sensor : ModelBase, IProbe
        {
            public Sensor(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
            public double Interrogate(int ping) => ping;
            public double LiveValue => 1.0;
            public void Nudge(int by) { }
        }
        [CrossReference<IProbe>("Probes")]
        public partial class Hub : ModelBase
        {
            public Hub(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
        }
        """;

    [Test]
    public void Emits_one_Ref_per_cross_reference_interface_with_captured_schedulers()
    {
        var r = GeneratorHarness.Run(new RefGenerator(), Source);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("sealed class IProbeRef : global::SimulaSharp.Kernel.Model.IModelRef"));
            // The ctor captures the interface target + both schedulers once — no per-call scheduler argument.
            Assert.That(r.AllSource, Does.Contain("public IProbeRef(global::Demo.IProbe target, global::SimulaSharp.Kernel.Services.IScheduler targetSched, global::SimulaSharp.Kernel.Services.IScheduler callerSched)"));
            // No per-model Ref is minted — the target is always the interface.
            Assert.That(r.AllSource, Does.Not.Contain("class SensorRef"));
        });
    }

    [Test]
    public void Default_members_are_async_and_dispatch_via_captured_schedulers()
    {
        var r = GeneratorHarness.Run(new RefGenerator(), Source);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("global::System.Threading.Tasks.Task<double> Interrogate(int ping)"));
            Assert.That(r.AllSource, Does.Contain("object.ReferenceEquals(_callerSched, _targetSched)"));
            Assert.That(r.AllSource, Does.Contain("_targetSched.InjectAsync(() => _target.Interrogate(ping))"));
        });
    }

    [Test]
    public void A_Triggerable_method_is_fire_and_forget_void()
    {
        var r = GeneratorHarness.Run(new RefGenerator(), Source);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("public void Nudge(int by)"));
            Assert.That(r.AllSource, Does.Contain("_targetSched.Post(() => _target.Nudge(by))"));
        });
    }

    [Test]
    public void A_VolatileAccess_property_is_a_direct_synchronous_getter()
    {
        var r = GeneratorHarness.Run(new RefGenerator(), Source);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("public double LiveValue => _target.LiveValue;"));
            Assert.That(r.AllSource, Does.Not.Contain("GetLiveValue("));
        });
    }

    [Test]
    public void SIM019_reports_a_Triggerable_method_that_is_not_void()
    {
        const string source = """
            using SimulaSharp.Kernel.Attributes;
            namespace Demo;
            public interface IBad { [Triggerable] int Ping(); }
            [CrossReference<IBad>("X")] public partial class Holder { }
            """;
        var r = GeneratorHarness.Run(new RefGenerator(), source);
        Assert.That(r.HasDiagnostic("SIM019"), Is.True);
    }

    [Test]
    public void SIM021_reports_a_VolatileAccess_property_that_is_not_a_get_only_gauge()
    {
        // A settable property (not get-only) can't be a gauge.
        const string source = """
            using SimulaSharp.Kernel.Attributes;
            namespace Demo;
            public interface IBad { [VolatileAccess] double Draw { get; set; } }
            [CrossReference<IBad>("X")] public partial class Holder { }
            """;
        var r = GeneratorHarness.Run(new RefGenerator(), source);
        Assert.That(r.HasDiagnostic("SIM021"), Is.True);
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
        public interface ITracker { }
        [CrossReference<ITracker>("Peers")]
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
            Assert.That(r.AllSource, Does.Contain("global::SimulaSharp.Kernel.Primitives.IReference<global::Demo.ITrackerRef> Peers"));
            Assert.That(r.AllSource, Does.Contain("new global::SimulaSharp.Kernel.Primitives.FillableReference<global::Demo.ITrackerRef>()"));
            Assert.That(r.AllSource, Does.Contain(".WireReference(string member, global::SimulaSharp.Kernel.Primitives.RefCollection refs)"));
            Assert.That(r.AllSource, Does.Contain("(global::Demo.ITrackerRef)__r"));
        });
    }
}

[TestFixture]
public sealed class LocalReferenceGeneratorTests
{
    private const string Model = """
        using SimulaSharp.Kernel.Attributes;
        using SimulaSharp.Kernel.Logging;
        using SimulaSharp.Kernel.Model;
        using SimulaSharp.Kernel.Services;
        namespace Demo;
        public sealed partial class Tracker : ModelBase
        {
            public Tracker(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
        }
        [Reference<Tracker>("Peers")]
        public partial class Hub : ModelBase
        {
            public Hub(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
        }
        """;

    [Test]
    public void Emits_a_readonly_IReadOnlyList_accessor_and_a_local_wire_seam()
    {
        // An intra-site [Reference<T>] holds the target instances directly (no Ref, no marshalling): a
        // read-only IReadOnlyList<T> accessor filled by the ILocalReferenceWiring seam.
        var r = GeneratorHarness.Run(new LocalReferenceGenerator(), Model);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("global::SimulaSharp.Kernel.Model.ILocalReferenceWiring"));
            Assert.That(r.AllSource, Does.Contain("global::System.Collections.Generic.IReadOnlyList<global::Demo.Tracker> Peers"));
            Assert.That(r.AllSource, Does.Contain("new global::System.Collections.Generic.List<global::Demo.Tracker>()"));
            Assert.That(r.AllSource, Does.Contain(".WireLocalReference(string member, global::System.Collections.Generic.IReadOnlyList<global::SimulaSharp.Kernel.Model.IModel> targets)"));
            Assert.That(r.AllSource, Does.Contain("(global::Demo.Tracker)__m"));
        });
    }
}
