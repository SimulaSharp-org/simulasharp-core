namespace SimulaSharp.CodeGen.Tests;

/// <summary>
/// Tests <see cref="SimulatorGenerator"/> emission and its strict SIM001–SIM015 validation.
/// Each diagnostic test feeds a source + assembly YAML crafted to trip exactly one rule.
/// </summary>
[TestFixture]
public sealed class SimulatorGeneratorTests
{
    // A valid model/service world the assembly YAMLs below refer to.
    private const string World = """
        using SimulaSharp.Kernel.Attributes;
        using SimulaSharp.Kernel.Logging;
        using SimulaSharp.Kernel.Model;
        using SimulaSharp.Kernel.Primitives;
        using SimulaSharp.Kernel.Services;
        namespace Demo;
        public partial class Plant : ModelBase
        {
            public Plant(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
            public Container<Unit> Units { get; } = new();
            [ServiceRef] public Svc Dep { get; set; } = null!;
            [Config] public int Capacity { get; set; }
        }
        public partial class Unit : ModelBase
        {
            public Unit(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
            public Reference<Unit> Peers { get; } = new();
        }
        public sealed class Svc : ServiceBase
        {
            public Svc(IClock c, ILogger l) : base(c, l, "Svc") { }
        }
        """;

    private static GenResult Run(string yaml, string world = World) =>
        GeneratorHarness.Run(new SimulatorGenerator(), world, yaml);

    private const string ValidYaml = """
        Simulator:
          Name: Widget
          Services:
            - Name: Dep
              Type: Demo.Svc
          Sites:
            - Name: Main
              Models:
                - Name: Root
                  Type: Demo.Plant
                  Config:
                    Capacity: "5"
                  Deps:
                    - Dep: Dep
                  Models:
                    - Units:
                        - Name: Child
                          Type: Demo.Unit
        """;

    [Test]
    public void Valid_assembly_emits_the_simulator_with_no_diagnostics()
    {
        var r = Run(ValidYaml);
        Assert.Multiple(() =>
        {
            Assert.That(r.Diagnostics, Is.Empty, "valid assembly reported: " + string.Join(",", r.Ids));
            Assert.That(r.AllSource, Does.Contain("class WidgetSimulator : global::SimulaSharp.Kernel.Services.SimulatorBase"));
            Assert.That(r.AllSource, Does.Contain("RegisterService(new global::Demo.Svc("));
            Assert.That(r.AllSource, Does.Contain("CreateSite(\"Main\""));
            Assert.That(r.AllSource, Does.Contain("Register(new global::Demo.Plant("));
            Assert.That(r.AllSource, Does.Contain("__ApplyConfig"));
            Assert.That(r.AllSource, Does.Contain("CollectKnobsFrom"));
            Assert.That(r.AllSource, Does.Contain("CollectPlottablesFrom"));
            Assert.That(r.AllSource, Does.Contain(".Units.Add("));
            Assert.That(r.AllSource, Does.Contain("WireReferences"));
        });
    }

    [Test]
    public void SIM001_reports_a_parse_error()
    {
        var r = Run("Simulator: : : [unclosed");
        Assert.That(r.HasDiagnostic("SIM001"), Is.True);
    }

    [Test]
    public void SIM002_reports_a_missing_simulator_name()
    {
        var r = Run("Simulator:\n  Sites: []");
        Assert.That(r.HasDiagnostic("SIM002"), Is.True);
    }

    [Test]
    public void SIM003_reports_a_duplicate_instance_path()
    {
        var r = Run("""
            Simulator:
              Name: W
              Sites:
                - Name: Main
                  Models:
                    - { Name: Root, Type: Demo.Unit }
                    - { Name: Root, Type: Demo.Unit }
            """);
        Assert.That(r.HasDiagnostic("SIM003"), Is.True);
    }

    [Test]
    public void SIM004_reports_an_unknown_type()
    {
        var r = Run("""
            Simulator:
              Name: W
              Sites:
                - Name: Main
                  Models:
                    - { Name: Root, Type: Demo.DoesNotExist }
            """);
        Assert.That(r.HasDiagnostic("SIM004"), Is.True);
    }

    [Test]
    public void SIM005_reports_a_wrong_component_kind()
    {
        // A service type used where a model is expected.
        var r = Run("""
            Simulator:
              Name: W
              Sites:
                - Name: Main
                  Models:
                    - { Name: Root, Type: Demo.Svc }
            """);
        Assert.That(r.HasDiagnostic("SIM005"), Is.True);
    }

    [Test]
    public void SIM015_warns_on_an_unknown_config_key()
    {
        var r = Run("""
            Simulator:
              Name: W
              Sites:
                - Name: Main
                  Models:
                    - Name: Root
                      Type: Demo.Plant
                      Config:
                        NoSuchKey: "1"
            """);
        Assert.That(r.HasDiagnostic("SIM015"), Is.True);
    }

    [Test]
    public void No_simulator_is_emitted_when_an_error_is_reported()
    {
        var r = Run("""
            Simulator:
              Name: W
              Sites:
                - Name: Main
                  Models:
                    - { Name: Root, Type: Demo.DoesNotExist }
            """);
        Assert.That(r.Sources, Is.Empty, "a simulator was emitted despite an error");
    }
}
