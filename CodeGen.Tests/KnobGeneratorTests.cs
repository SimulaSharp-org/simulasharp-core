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
            [IntegerKnob("Speed", Min = 0, Max = 100)] public partial int Speed { get; set; }
            [TickBoxKnob("On")] public partial bool On { get; set; }
            [FloatKnob("Ratio", IsReadOnly = true)] public partial double Ratio { get; set; }
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
