namespace SimulaSharp.CodeGen.Tests;

[TestFixture]
public sealed class KnobGeneratorTests
{
    private const string ModelWithKnobs = """
        using SimulaSharp.Kernel.Attributes;
        using SimulaSharp.Kernel.Logging;
        using SimulaSharp.Kernel.Model;
        using SimulaSharp.Kernel.Services;
        namespace Demo;
        public partial class Widget : ModelBase
        {
            public Widget(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
            [NumberKnob<int>("Speed", Min = 0, Max = 100)] public partial int Speed { get; set; }
            [TickBoxKnob("On")] public partial bool On { get; set; }
            [NumberKnob<double>("Ratio", IsReadOnly = true)] public partial double Ratio { get; set; }
            [TextKnob("Tag", MaxLength = 16)] public partial string Tag { get; set; }
            [TriggerKnob("Reset")] protected void Handle_Reset() { }
        }
        """;

    [Test]
    public void Materializes_a_volatile_partial_property_and_implements_IKnobProvider()
    {
        var r = GeneratorHarness.Run(new KnobGenerator(), ModelWithKnobs);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("IKnobProvider"));
            Assert.That(r.AllSource, Does.Contain("private int __Speed"));
            Assert.That(r.AllSource, Does.Contain("Volatile.Read(ref __Speed)"));
            Assert.That(r.AllSource, Does.Contain("Volatile.Write(ref __Speed, value)"));
            Assert.That(r.AllSource, Does.Contain("__SpeedKnob?.RaiseChanged()"));
        });
    }

    [Test]
    public void Emits_kind_and_numeric_metadata_per_knob()
    {
        var r = GeneratorHarness.Run(new KnobGenerator(), ModelWithKnobs);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("KnobKind.Number"));
            Assert.That(r.AllSource, Does.Contain("KnobKind.Bool"));
            Assert.That(r.AllSource, Does.Contain("KnobKind.Text"));
            Assert.That(r.AllSource, Does.Contain("Min = 0"));
            Assert.That(r.AllSource, Does.Contain("Max = 100"));
        });
    }

    [Test]
    public void A_read_only_knob_has_no_writer()
    {
        var r = GeneratorHarness.Run(new KnobGenerator(), ModelWithKnobs);
        // The read-only Ratio knob's binder must not set WriteBoxed.
        int ratioIdx = r.AllSource.IndexOf("prefix + \".Ratio\"", StringComparison.Ordinal);
        int nextKnob = r.AllSource.IndexOf("into.Add", ratioIdx, StringComparison.Ordinal);
        string ratioBlock = r.AllSource[ratioIdx..nextKnob];
        Assert.That(ratioBlock, Does.Not.Contain("WriteBoxed"), "a read-only knob emitted a writer");
    }

    [Test]
    public void A_trigger_knob_wraps_the_handler_in_a_public_method()
    {
        var r = GeneratorHarness.Run(new KnobGenerator(), ModelWithKnobs);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("public void Reset()"));
            Assert.That(r.AllSource, Does.Contain("Handle_Reset()"));
            Assert.That(r.AllSource, Does.Contain("KnobKind.Trigger"));
        });
    }

    [Test]
    public void A_generic_NumberKnob_carries_bounds_and_decimal_places()
    {
        // The single generic attribute serves both integer and floating-point knobs; DecimalPlaces is meaningful
        // for a double and simply carries through (a no-op the UI ignores for integer T).
        const string src = """
            using SimulaSharp.Kernel.Attributes;
            using SimulaSharp.Kernel.Logging;
            using SimulaSharp.Kernel.Model;
            using SimulaSharp.Kernel.Services;
            namespace Demo;
            public partial class Gauge : ModelBase
            {
                public Gauge(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
                [NumberKnob<double>("Vbus", Max = 32, DecimalPlaces = 3)] public partial double Vbus { get; set; }
                [NumberKnob<int>("Mode", Min = 0, Max = 3)] public partial int Mode { get; set; }
            }
            """;
        var r = GeneratorHarness.Run(new KnobGenerator(), src);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("KnobKind.Number"));
            Assert.That(r.AllSource, Does.Contain("Max = 32"));
            Assert.That(r.AllSource, Does.Contain("DecimalPlaces = 3"));
            Assert.That(r.AllSource, Does.Contain("Min = 0"));
            Assert.That(r.AllSource, Does.Contain("Max = 3"));
        });
    }

    [Test]
    public void A_model_knob_dispatches_through_its_Scheduler()
    {
        var r = GeneratorHarness.Run(new KnobGenerator(), ModelWithKnobs);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("Scheduler.Post(() => __SpeedKnob!.RaiseChanged())"),
                "a model knob write did not marshal through Scheduler");
            Assert.That(r.AllSource, Does.Contain("Scheduler.Post(() => Reset())"), "a model trigger did not marshal through Scheduler");
            Assert.That(r.AllSource, Does.Not.Contain("Site.Post"), "a model must not dispatch through Site");
        });
    }

    [Test]
    public void A_service_with_knobs_dispatches_through_its_Site()
    {
        // Services can carry knobs too; their MMI-facing writes/triggers marshal through the public Site
        // (IDispatcherSite), the surface common to both sim-paced and loosely-coupled services.
        const string src = """
            using SimulaSharp.Kernel.Attributes;
            using SimulaSharp.Kernel.Logging;
            using SimulaSharp.Kernel.Model;
            using SimulaSharp.Kernel.Services;
            namespace Demo;
            public partial class Link : ServiceBase
            {
                public Link(IClock c, ILogger l) : base(c, l, "Link") { }
                [NumberKnob<int>("Rate", Min = 0, Max = 100)] public partial int Rate { get; set; }
                [TriggerKnob("Ping")] protected void Handle_Ping() { }
            }
            """;
        var r = GeneratorHarness.Run(new KnobGenerator(), src);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("IKnobProvider"), "the service did not implement IKnobProvider");
            Assert.That(r.AllSource, Does.Contain("Site.Post(() => __RateKnob!.RaiseChanged())"), "service knob write did not marshal through Site");
            Assert.That(r.AllSource, Does.Contain("Site.Post(() => Ping())"), "service trigger did not marshal through Site");
            Assert.That(r.AllSource, Does.Not.Contain("Scheduler.Post"), "a service must not dispatch through Scheduler");
        });
    }

    [Test]
    public void An_enum_knob_emits_a_dropdown_with_value_label_options()
    {
        const string src = """
            using SimulaSharp.Kernel.Attributes;
            using SimulaSharp.Kernel.Logging;
            using SimulaSharp.Kernel.Model;
            using SimulaSharp.Kernel.Services;
            namespace Demo;
            public enum Polarisation { RHCP, LHCP = 5 }
            public partial class Antenna : ModelBase
            {
                public Antenna(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
                [EnumKnob("Polarisation")] public partial Polarisation Pol { get; set; }
            }
            """;
        var r = GeneratorHarness.Run(new KnobGenerator(), src);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("KnobKind.Enum"));
            // Backing field is the enum's underlying integer; the typed property casts through it.
            Assert.That(r.AllSource, Does.Contain("private int __Pol"));
            Assert.That(r.AllSource, Does.Contain("get => (global::Demo.Polarisation)"));
            // Options are (underlying value, member name) pairs — including explicit values.
            Assert.That(r.AllSource, Does.Contain("KnobOption(0L, \"RHCP\")"));
            Assert.That(r.AllSource, Does.Contain("KnobOption(5L, \"LHCP\")"));
            // The value is exchanged as long over the underlying-int field.
            Assert.That(r.AllSource, Does.Contain("ReadBoxed = () => (long)"));
            Assert.That(r.AllSource, Does.Contain("(int)(long)v!"));
        });
    }

    [Test]
    public void An_EnumKnob_on_a_non_enum_property_reports_SIM030()
    {
        const string src = """
            using SimulaSharp.Kernel.Attributes;
            using SimulaSharp.Kernel.Logging;
            using SimulaSharp.Kernel.Model;
            using SimulaSharp.Kernel.Services;
            namespace Demo;
            public partial class Bad : ModelBase
            {
                public Bad(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
                [EnumKnob("Nope")] public partial int Nope { get; set; }
            }
            """;
        var r = GeneratorHarness.Run(new KnobGenerator(), src);
        Assert.That(r.HasDiagnostic("SIM030"), Is.True);
    }

    [Test]
    public void A_model_with_no_knobs_generates_nothing()
    {
        const string bare = """
            using SimulaSharp.Kernel.Logging;
            using SimulaSharp.Kernel.Model;
            using SimulaSharp.Kernel.Services;
            namespace Demo;
            public partial class Plain : ModelBase
            {
                public Plain(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
            }
            """;
        var r = GeneratorHarness.Run(new KnobGenerator(), bare);
        Assert.That(r.Sources, Is.Empty);
    }
}
