namespace SimulaSharp.CodeGen.Tests;

/// <summary>
/// End-to-end: runs the whole generator set and <b>compiles</b> the output, so integration bugs that surface only
/// when every generator's code is combined — reference-seam casts, <c>IReference&lt;T&gt;Ref</c> field types,
/// companion-interface conformance, the simulator's Ref wiring, containment <c>init</c> accessors — are caught.
/// Inspection-only tests assert emitted text but never compile it together.
/// </summary>
[TestFixture]
public sealed class PipelineCompileTests
{
    [Test]
    public void A_cross_site_interface_reference_compiles_end_to_end()
    {
        // Grid on site A references, through a framework-pure IPowerSource, a Panel on site B — exercising the
        // per-interface IPowerSourceRef and all three member tiers: default async (GetAvailableW), [VolatileAccess]
        // gauge (LiveDrawW, a plain synchronous getter) and [Triggerable] fire-and-forget (SetBudget).
        const string source = """
            using System.Threading.Tasks;
            using SimulaSharp.Kernel.Attributes;
            using SimulaSharp.Kernel.Logging;
            using SimulaSharp.Kernel.Model;
            using SimulaSharp.Kernel.Services;
            namespace Demo;

            public interface IPowerSource
            {
                double AvailableW { get; }
                [VolatileAccess] double LiveDrawW { get; }
                [Triggerable] void SetBudget(double w);
            }

            public sealed partial class Panel : ModelBase, IPowerSource
            {
                public Panel(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
                public double AvailableW => 42.0;
                public double LiveDrawW => 3.0;
                public void SetBudget(double w) { }
            }

            [CrossReference<IPowerSource>("Sources")]
            public partial class Grid : ModelBase
            {
                public Grid(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
                public async Task<double> Total()
                {
                    double sum = 0;
                    foreach (var src in Sources)
                    {
                        sum += await src.GetAvailableW();   // default → async request/response
                        sum += src.LiveDrawW;               // [VolatileAccess] → synchronous gauge read
                        src.SetBudget(10);                  // [Triggerable] → fire-and-forget
                    }
                    return sum;
                }
            }
            """;
        const string yaml = """
            Simulator:
              Name: E2E
              Sites:
                - Name: A
                  Models:
                    - Name: Grid
                      Type: Demo.Grid
                      CRefs:
                        - Sources: [ E2E.Panel ]
                - Name: B
                  Models:
                    - Name: Panel
                      Type: Demo.Panel
            """;

        var errors = GeneratorHarness.CompileWithAllGenerators(source, yaml);
        Assert.That(errors, Is.Empty, "generated code did not compile:\n" + string.Join("\n", errors));
    }

    [Test]
    public void Containment_and_a_class_reference_compile_end_to_end()
    {
        // One model carrying BOTH a [Contains<Wheel>] container and an intra-site [Reference<Wheel>] — the latter
        // holds the same-site Wheel instances directly, so it is read synchronously (no Ref, no await).
        const string source = """
            using SimulaSharp.Kernel.Attributes;
            using SimulaSharp.Kernel.Logging;
            using SimulaSharp.Kernel.Model;
            using SimulaSharp.Kernel.Services;
            namespace Demo;

            public sealed partial class Wheel : ModelBase
            {
                public Wheel(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
                public int Rpm { get; set; }
            }

            [Contains<Wheel>("Wheels")]
            [Reference<Wheel>("Spare")]
            public partial class Car : ModelBase
            {
                public Car(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
                public int WheelCount => Wheels.Count;
                public int SpareRpm() => Spare[0].Rpm;
            }
            """;
        const string yaml = """
            Simulator:
              Name: Garage
              Sites:
                - Name: Main
                  Models:
                    - Name: Car
                      Type: Demo.Car
                      Models:
                        - Wheels:
                            - { Name: W0, Type: Demo.Wheel }
                            - { Name: W1, Type: Demo.Wheel }
                      Refs:
                        - Spare: [ Garage.Car.W0 ]
            """;

        var errors = GeneratorHarness.CompileWithAllGenerators(source, yaml);
        Assert.That(errors, Is.Empty, "generated code did not compile:\n" + string.Join("\n", errors));
    }

    [Test]
    public void A_defaulted_parameter_model_compiles_without_a_config_block()
    {
        // A model with a [Parameter] default and NO Config: block must compile end to end: the parameter
        // generator emits __ApplyParameters, and the simulator calls it unconditionally so the default is stamped.
        const string source = """
            using SimulaSharp.Kernel.Attributes;
            using SimulaSharp.Kernel.Logging;
            using SimulaSharp.Kernel.Model;
            using SimulaSharp.Kernel.Services;
            namespace Demo;

            public partial class Tank : ModelBase
            {
                public Tank(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
                [Parameter(42)] public int Capacity { get; set; }
            }
            """;
        const string yaml = """
            Simulator:
              Name: Depot
              Sites:
                - Name: Main
                  Models:
                    - Name: Tank
                      Type: Demo.Tank
            """;

        var errors = GeneratorHarness.CompileWithAllGenerators(source, yaml);
        Assert.That(errors, Is.Empty, "generated code did not compile:\n" + string.Join("\n", errors));
    }
}
