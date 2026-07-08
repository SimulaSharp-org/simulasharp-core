namespace SimulaSharp.CodeGen.Tests;

[TestFixture]
public sealed class ConfigGeneratorTests
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
            [Config] public int Count { get; set; }
            [Config] public double Rate { get; set; }
            [Config] public bool On { get; set; }
            [Config("custom")] public string Name { get; set; } = "";
            [Config(Required = false)] public Mode M { get; set; }
        }
        """;

    [Test]
    public void Emits_typed_invariant_parsing_per_member()
    {
        var r = GeneratorHarness.Run(new ConfigGenerator(), Model);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("__ApplyConfig"));
            Assert.That(r.AllSource, Does.Contain("global::System.Int32.Parse(__value, global::System.Globalization.CultureInfo.InvariantCulture)"));
            Assert.That(r.AllSource, Does.Contain("global::System.Double.Parse(__value"));
            Assert.That(r.AllSource, Does.Contain("global::System.Boolean.Parse(__value)"));
            Assert.That(r.AllSource, Does.Contain("global::System.Enum.Parse(typeof(global::Demo.Mode)"));
        });
    }

    [Test]
    public void Honours_a_custom_key()
    {
        var r = GeneratorHarness.Run(new ConfigGenerator(), Model);
        Assert.That(r.AllSource, Does.Contain("config.TryGetValue(\"custom\""));
    }

    [Test]
    public void A_required_member_throws_on_a_missing_key_and_an_optional_one_does_not()
    {
        var r = GeneratorHarness.Run(new ConfigGenerator(), Model);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("Missing required config 'Count'"));
            Assert.That(r.AllSource, Does.Not.Contain("Missing required config 'M'"), "Required=false member emitted a throw");
        });
    }
}
