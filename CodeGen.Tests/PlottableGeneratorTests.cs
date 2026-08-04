namespace SimulaSharp.CodeGen.Tests;

[TestFixture]
public sealed class PlottableGeneratorTests
{
    private static string Model(string members) => $$"""
        using SimulaSharp.Kernel.Attributes;
        using SimulaSharp.Kernel.Logging;
        using SimulaSharp.Kernel.Model;
        using SimulaSharp.Kernel.Services;
        namespace Demo;
        public enum PowerMode { Off, On }
        public partial class Widget : ModelBase
        {
            public Widget(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
            {{members}}
        }
        """;

    [Test]
    public void Materializes_a_plottable_only_partial_and_implements_IPlottableProvider()
    {
        var r = GeneratorHarness.Run(new PlottableGenerator(),
            Model("""[Plottable("Load", Unit = "W")] public partial double Load { get; set; }"""));
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("IPlottableProvider"));
            Assert.That(r.AllSource, Does.Contain("private double __Load"));
            Assert.That(r.AllSource, Does.Contain("Volatile.Read(ref __Load)"));
            Assert.That(r.AllSource, Does.Contain("Sample = () => (double)this.Load"));
            Assert.That(r.AllSource, Does.Contain("Unit = \"W\""));
        });
    }

    [Test]
    public void A_knob_plottable_coexisting_partial_is_not_re_declared()
    {
        // With a knob attribute present the knob generator owns materialization; the plottable
        // generator must only reference the property, not re-declare its backing field.
        var r = GeneratorHarness.Run(new PlottableGenerator(),
            Model("""
                [NumberKnob<double>("Ratio", IsReadOnly = true)]
                [Plottable("Ratio", Unit = "%")]
                public partial double Ratio { get; set; }
                """));
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Not.Contain("private double __Ratio"), "re-declared a knob-owned field");
            Assert.That(r.AllSource, Does.Contain("Sample = () => (double)this.Ratio"));
        });
    }

    [Test]
    public void Projects_bool_and_enum_to_double()
    {
        var r = GeneratorHarness.Run(new PlottableGenerator(),
            Model("""
                [Plottable("Lit")] public partial bool Lit { get; set; }
                [Plottable("Mode")] public partial PowerMode Mode { get; set; }
                """));
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("this.Lit ? 1.0 : 0.0"));
            Assert.That(r.AllSource, Does.Contain("this.Mode"));
            Assert.That(r.HasDiagnostic("SIM020"), Is.False, "bool/enum should be supported");
        });
    }

    [Test]
    public void An_unsupported_type_reports_SIM020()
    {
        var r = GeneratorHarness.Run(new PlottableGenerator(),
            Model("""[Plottable("Tag")] public partial string Tag { get; set; }"""));
        Assert.That(r.HasDiagnostic("SIM020"), Is.True, "a non-numeric plottable did not report SIM020");
    }

    [Test]
    public void Keys_the_map_entry_by_full_path()
    {
        var r = GeneratorHarness.Run(new PlottableGenerator(),
            Model("""[Plottable("Load")] public partial double Load { get; set; }"""));
        Assert.That(r.AllSource, Does.Contain("into[prefix + \".Load\"]"));
    }

    [Test]
    public void A_service_with_a_plottable_implements_IPlottableProvider()
    {
        // Plottables are pure volatile sampling — no dispatch — so a service supports them the same as a model.
        const string src = """
            using SimulaSharp.Kernel.Attributes;
            using SimulaSharp.Kernel.Logging;
            using SimulaSharp.Kernel.Model;
            using SimulaSharp.Kernel.Services;
            namespace Demo;
            public partial class Link : ServiceBase
            {
                public Link(IClock c, ILogger l) : base(c, l, "Link") { }
                [Plottable("Latency", Unit = "us")] public partial double Latency { get; set; }
            }
            """;
        var r = GeneratorHarness.Run(new PlottableGenerator(), src);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("IPlottableProvider"));
            Assert.That(r.AllSource, Does.Contain("Sample = () => (double)this.Latency"));
            Assert.That(r.AllSource, Does.Contain("Unit = \"us\""));
        });
    }
}
