namespace SimulaSharp.CodeGen.Tests;

[TestFixture]
public sealed class ParameterGeneratorTests
{
    private const string Model = """
        using SimulaSharp.Kernel.Attributes;
        using SimulaSharp.Kernel.Logging;
        using SimulaSharp.Kernel.Model;
        using SimulaSharp.Kernel.Services;
        namespace Demo;
        public enum Mode { A, B }
        public partial class Cfg : ModelBase
        {
            public Cfg(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
            [Parameter(3)] public int Count { get; set; }
            [Parameter(1.5)] public double Rate { get; set; }
            [Parameter(true)] public bool On { get; set; }
            [Parameter("hi", Key = "custom")] public string Name { get; set; } = "";
            [Parameter(Mode.B)] public Mode M { get; set; }
            [Parameter(Required = true)] public int Must { get; set; }
        }
        """;

    [Test]
    public void Emits_typed_invariant_parsing_per_member()
    {
        var r = GeneratorHarness.Run(new ParameterGenerator(), Model);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("__ApplyParameters"));
            Assert.That(r.AllSource, Does.Contain("global::System.Int32.Parse(__value, global::System.Globalization.CultureInfo.InvariantCulture)"));
            Assert.That(r.AllSource, Does.Contain("global::System.Double.Parse(__value"));
            Assert.That(r.AllSource, Does.Contain("global::System.Boolean.Parse(__value)"));
            Assert.That(r.AllSource, Does.Contain("global::System.Enum.Parse(typeof(global::Demo.Mode)"));
        });
    }

    [Test]
    public void Honours_a_custom_key()
    {
        var r = GeneratorHarness.Run(new ParameterGenerator(), Model);
        Assert.That(r.AllSource, Does.Contain("config.TryGetValue(\"custom\""));
    }

    [Test]
    public void Applies_the_attribute_default_when_a_key_is_absent()
    {
        var r = GeneratorHarness.Run(new ParameterGenerator(), Model);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("Count = (int)(3)"));
            Assert.That(r.AllSource, Does.Contain("Rate = (double)(1.5)"));
            Assert.That(r.AllSource, Does.Contain("On = (bool)(true)"));
        });
    }

    [Test]
    public void A_required_parameter_throws_on_a_missing_key_and_a_defaulted_one_does_not()
    {
        var r = GeneratorHarness.Run(new ParameterGenerator(), Model);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("Missing required parameter 'Must'"));
            Assert.That(r.AllSource, Does.Not.Contain("Missing required parameter 'Count'"), "a defaulted parameter emitted a throw");
        });
    }

    [Test]
    public void Emits_CollectParameters_with_boxed_binders()
    {
        var r = GeneratorHarness.Run(new ParameterGenerator(), Model);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("IParameterProvider.CollectParameters"));
            Assert.That(r.AllSource, Does.Contain("new global::SimulaSharp.Kernel.Parameters.ParameterInfo"));
            Assert.That(r.AllSource, Does.Contain("Path = prefix + \".Count\""));
            Assert.That(r.AllSource, Does.Contain("ReadBoxed = () => Count"));
            Assert.That(r.AllSource, Does.Contain("WriteBoxed = __v => Count = (int)__v!"));
        });
    }

    // A model that carries no [Parameter] members at all.
    private const string Parameterless = """
        using SimulaSharp.Kernel.Logging;
        using SimulaSharp.Kernel.Model;
        using SimulaSharp.Kernel.Services;
        namespace Demo;
        public partial class Bare : ModelBase
        {
            public Bare(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
        }
        """;

    [Test]
    public void A_parameterless_model_still_emits_an_empty_provider()
    {
        // The generated simulator calls __ApplyParameters unconditionally, so even a parameterless model must
        // expose the (empty) method and implement IParameterProvider.
        var r = GeneratorHarness.Run(new ParameterGenerator(), Parameterless);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("__ApplyParameters"));
            Assert.That(r.AllSource, Does.Contain("IParameterProvider"));
            Assert.That(r.HasDiagnostic("SIM029"), Is.False, "a partial model was wrongly flagged");
        });
    }

    [Test]
    public void A_non_partial_model_reports_SIM029()
    {
        const string source = """
            using SimulaSharp.Kernel.Logging;
            using SimulaSharp.Kernel.Model;
            using SimulaSharp.Kernel.Services;
            namespace Demo;
            public sealed class Bare : ModelBase
            {
                public Bare(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
            }
            """;
        var r = GeneratorHarness.Run(new ParameterGenerator(), source);
        Assert.That(r.HasDiagnostic("SIM029"), Is.True);
    }

    [Test]
    public void A_non_model_non_service_class_is_not_required_to_be_partial()
    {
        // SIM029 targets models/services only — a plain POCO (or an event class) needs no generation.
        const string source = """
            namespace Demo;
            public sealed class PlainPoco { public int X; }
            """;
        var r = GeneratorHarness.Run(new ParameterGenerator(), source);
        Assert.That(r.HasDiagnostic("SIM029"), Is.False);
    }
}
