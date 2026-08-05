namespace SimulaSharp.CodeGen.Tests;

/// <summary>
/// Tests <see cref="SimulatorGenerator"/> emission and its strict SIM001–SIM018 validation.
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
        [Contains<Unit>("Units")]
        public partial class Plant : ModelBase
        {
            public Plant(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
            [ServiceRef] public Svc Dep { get; set; } = null!;
            [Parameter] public int Capacity { get; set; }
        }
        [Reference<Unit>("Peers")]
        public partial class Unit : ModelBase
        {
            public Unit(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
        }
        public sealed partial class Svc : ServiceBase
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
                          Refs:
                            - Peers: [ Widget.Root.Child2 ]
                        - Name: Child2
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
            Assert.That(r.AllSource, Does.Contain("__ApplyParameters"));
            Assert.That(r.AllSource, Does.Contain("CollectKnobsFrom"));
            Assert.That(r.AllSource, Does.Contain("CollectPlottablesFrom"));
            // Containment is built bottom-up through a ContainerFactory, then frozen into the parent's init.
            Assert.That(r.AllSource, Does.Contain("new global::SimulaSharp.Kernel.Primitives.ContainerFactory()"));
            Assert.That(r.AllSource, Does.Contain("Units = "));
            Assert.That(r.AllSource, Does.Contain(".Build<global::Demo.Unit>()"));
            // An intra-site [Reference<T>] is wired directly (no Ref) through the WireLocalReference seam.
            Assert.That(r.AllSource, Does.Contain(".WireLocalReference(\"Peers\","));
            Assert.That(r.AllSource, Does.Contain("new global::System.Collections.Generic.List<global::SimulaSharp.Kernel.Model.IModel>"));
        });
    }

    [Test]
    public void A_service_gets_knob_discovery()
    {
        // Services can carry knobs now, so the simulator must run knob discovery on them too. The service Dep is
        // minted first, so it is __c0 (as the cyclic-deps test relies on).
        var r = Run(ValidYaml);
        Assert.That(r.AllSource, Does.Contain("CollectKnobsFrom(__c0"), "the service did not get knob discovery");
    }

    [Test]
    public void A_service_gets_plottable_discovery()
    {
        // Services can carry plottables now, so the simulator must run plottable discovery on them too.
        var r = Run(ValidYaml);
        Assert.That(r.AllSource, Does.Contain("CollectPlottablesFrom(__c0"), "the service did not get plottable discovery");
    }

    [Test]
    public void A_loosely_coupled_service_routes_to_RegisterLooselyCoupledService()
    {
        // A service whose type derives LooselyCoupledServiceBase must register through the
        // clock-ungated entry point, not the sim-paced RegisterService.
        const string world = World + """

            public sealed partial class Egse : LooselyCoupledServiceBase
            {
                public Egse(IClock c, ILogger l) : base(c, l, "Egse") { }
                protected override System.Threading.Tasks.Task _EntryPoint() => System.Threading.Tasks.Task.CompletedTask;
            }
            """;
        var r = Run("""
            Simulator:
              Name: Widget
              Services:
                - Name: Link
                  Type: Demo.Egse
              Sites: []
            """, world);

        Assert.Multiple(() =>
        {
            Assert.That(r.Diagnostics, Is.Empty, "loosely-coupled service reported: " + string.Join(",", r.Ids));
            Assert.That(r.AllSource, Does.Contain("RegisterLooselyCoupledService(new global::Demo.Egse("));
            Assert.That(r.AllSource, Does.Not.Contain("RegisterService(new global::Demo.Egse("));
        });
    }

    [Test]
    public void Services_can_reference_services_including_cyclically()
    {
        // Two services that [ServiceRef] each other. The simulator constructs both before binding any
        // dep, so the cycle resolves by setter injection — A.Other = B and B.Other = A.
        const string world = World + """

            public partial class SvcA : ServiceBase
            {
                public SvcA(IClock c, ILogger l) : base(c, l, "A") { }
                [ServiceRef] public SvcB Other { get; set; } = null!;
            }
            public partial class SvcB : ServiceBase
            {
                public SvcB(IClock c, ILogger l) : base(c, l, "B") { }
                [ServiceRef] public SvcA Other { get; set; } = null!;
            }
            """;
        var r = Run("""
            Simulator:
              Name: Widget
              Services:
                - Name: A
                  Type: Demo.SvcA
                  Deps:
                    - Other: B
                - Name: B
                  Type: Demo.SvcB
                  Deps:
                    - Other: A
              Sites: []
            """, world);

        Assert.Multiple(() =>
        {
            Assert.That(r.Diagnostics, Is.Empty, "cyclic service deps reported: " + string.Join(",", r.Ids));
            // A → __c0, B → __c1 (services minted first); each is assigned the other after construction.
            Assert.That(r.AllSource, Does.Contain("__c0.Other = __c1;"));
            Assert.That(r.AllSource, Does.Contain("__c1.Other = __c0;"));
        });
    }

    [Test]
    public void A_service_dep_naming_an_unknown_service_reports_SIM014()
    {
        const string world = World + """

            public partial class SvcA : ServiceBase
            {
                public SvcA(IClock c, ILogger l) : base(c, l, "A") { }
                [ServiceRef] public Svc Other { get; set; } = null!;
            }
            """;
        var r = Run("""
            Simulator:
              Name: W
              Services:
                - Name: A
                  Type: Demo.SvcA
                  Deps:
                    - Other: Nope
              Sites: []
            """, world);
        Assert.That(r.HasDiagnostic("SIM014"), Is.True);
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
    public void SIM016_warns_on_an_unknown_config_key()
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
        Assert.That(r.HasDiagnostic("SIM016"), Is.True);
    }

    [Test]
    public void Parameters_are_applied_even_without_a_config_block()
    {
        // Regression: a model with [Parameter] members but no Config: block must still have __ApplyParameters
        // called (with an empty dict) so the attribute defaults are stamped. Previously the call was skipped
        // entirely when a model had no Config block, leaving defaulted parameters unset.
        var r = Run("""
            Simulator:
              Name: W
              Sites:
                - Name: Main
                  Models:
                    - Name: Root
                      Type: Demo.Plant
            """);
        Assert.Multiple(() =>
        {
            Assert.That(r.Diagnostics, Is.Empty, "reported: " + string.Join(",", r.Ids));
            Assert.That(r.AllSource, Does.Contain(".__ApplyParameters(new global::System.Collections.Generic.Dictionary<string, string?> {})"));
        });
    }

    [Test]
    public void An_empty_string_config_value_roundtrips()
    {
        const string world = """
            using SimulaSharp.Kernel.Attributes;
            using SimulaSharp.Kernel.Logging;
            using SimulaSharp.Kernel.Model;
            using SimulaSharp.Kernel.Services;
            namespace Demo;
            public partial class Cfg : ModelBase
            {
                public Cfg(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
                [Parameter("def")] public string Label { get; set; }
            }
            """;
        var r = Run("""
            Simulator:
              Name: W
              Sites:
                - Name: Main
                  Models:
                    - Name: Root
                      Type: Demo.Cfg
                      Config:
                        Label: ""
            """, world);
        // An explicit empty-string config value must reach __ApplyParameters as "" (not null → not the default).
        Assert.That(r.AllSource, Does.Contain("[\"Label\"] = \"\""), "empty-string config value did not round-trip to a \"\" literal");
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

    [Test]
    public void SIM007_reports_an_unknown_container_name()
    {
        // Plant declares [Contains<Unit>("Units")] — a container named "Nope" does not exist.
        var r = Run("""
            Simulator:
              Name: W
              Sites:
                - Name: Main
                  Models:
                    - Name: Root
                      Type: Demo.Plant
                      Models:
                        - Nope:
                            - { Name: C, Type: Demo.Unit }
            """);
        Assert.That(r.HasDiagnostic("SIM007"), Is.True);
    }

    [Test]
    public void SIM008_reports_a_contained_type_not_assignable_to_the_container()
    {
        // Gadget is a model, but not a Unit — so it cannot go into Plant's Container<Unit>.
        const string world = World + """

            public sealed partial class Gadget : ModelBase
            {
                public Gadget(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
            }
            """;
        var r = Run("""
            Simulator:
              Name: W
              Sites:
                - Name: Main
                  Models:
                    - Name: Root
                      Type: Demo.Plant
                      Models:
                        - Units:
                            - { Name: C, Type: Demo.Gadget }
            """, world);
        Assert.That(r.HasDiagnostic("SIM008"), Is.True);
    }

    [Test]
    public void SIM009_reports_a_contained_child_that_is_not_a_model()
    {
        // Svc is a service (IService, not IModel) — invalid as a contained child.
        var r = Run("""
            Simulator:
              Name: W
              Sites:
                - Name: Main
                  Models:
                    - Name: Root
                      Type: Demo.Plant
                      Models:
                        - Units:
                            - { Name: C, Type: Demo.Svc }
            """);
        Assert.That(r.HasDiagnostic("SIM009"), Is.True);
    }

    [Test]
    public void SIM010_reports_an_unknown_reference_name()
    {
        // Unit declares [Reference<Unit>("Peers")] — a reference named "Nope" does not exist.
        var r = Run("""
            Simulator:
              Name: W
              Sites:
                - Name: Main
                  Models:
                    - Name: Root
                      Type: Demo.Unit
                      Refs:
                        - Nope: [ W.Root ]
            """);
        Assert.That(r.HasDiagnostic("SIM010"), Is.True);
    }

    [Test]
    public void SIM011_reports_an_unresolved_reference_path()
    {
        var r = Run("""
            Simulator:
              Name: W
              Sites:
                - Name: Main
                  Models:
                    - Name: Root
                      Type: Demo.Unit
                      Refs:
                        - Peers: [ W.DoesNotExist ]
            """);
        Assert.That(r.HasDiagnostic("SIM011"), Is.True);
    }

    [Test]
    public void SIM012_reports_a_referenced_instance_not_assignable_to_the_reference()
    {
        // Peers is [Reference<Unit>]; a Plant is a model but not a Unit.
        var r = Run("""
            Simulator:
              Name: W
              Sites:
                - Name: Main
                  Models:
                    - Name: U
                      Type: Demo.Unit
                      Refs:
                        - Peers: [ W.P ]
                    - Name: P
                      Type: Demo.Plant
            """);
        Assert.That(r.HasDiagnostic("SIM012"), Is.True);
    }

    [Test]
    public void SIM017_reports_an_intra_site_reference_that_crosses_a_site()
    {
        // Peers is an intra-site [Reference<Unit>], but its target lives on a different site.
        var r = Run("""
            Simulator:
              Name: W
              Sites:
                - Name: Main
                  Models:
                    - Name: U
                      Type: Demo.Unit
                      Refs:
                        - Peers: [ W.V ]
                - Name: Other
                  Models:
                    - Name: V
                      Type: Demo.Unit
            """);
        Assert.That(r.HasDiagnostic("SIM017"), Is.True);
    }

    [Test]
    public void SIM018_reports_a_cross_reference_whose_target_is_not_an_interface()
    {
        // A [CrossReference<T>] must target an interface; Gizmo targets the concrete Unit.
        const string world = World + """

            [CrossReference<Unit>("Buddies")]
            public partial class Gizmo : ModelBase
            {
                public Gizmo(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
            }
            """;
        var r = Run("""
            Simulator:
              Name: W
              Sites:
                - Name: Main
                  Models:
                    - Name: G
                      Type: Demo.Gizmo
                      CRefs:
                        - Buddies: [ W.U ]
                    - Name: U
                      Type: Demo.Unit
            """, world);
        Assert.That(r.HasDiagnostic("SIM018"), Is.True);
    }

    [Test]
    public void Contains_attribute_emits_an_init_only_container_accessor()
    {
        const string source = """
            using SimulaSharp.Kernel.Attributes;
            using SimulaSharp.Kernel.Logging;
            using SimulaSharp.Kernel.Model;
            using SimulaSharp.Kernel.Services;
            namespace Demo;
            public sealed partial class Cog : ModelBase
            {
                public Cog(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
            }
            [Contains<Cog>("Cogs")]
            public partial class Machine : ModelBase
            {
                public Machine(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
            }
            """;
        var r = GeneratorHarness.Run(new ContainmentGenerator(), source);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("public global::SimulaSharp.Kernel.Primitives.Container<global::Demo.Cog> Cogs { get; init; }"));
            Assert.That(r.AllSource, Does.Contain("Container<global::Demo.Cog>.Empty"));
        });
    }
}
