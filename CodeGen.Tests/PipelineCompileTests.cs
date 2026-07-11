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
        // companion IPowerSourceRef, PanelRef implementing it, the IReference accessor, and the WireReference seam.
        const string source = """
            using System.Threading.Tasks;
            using SimulaSharp.Kernel.Attributes;
            using SimulaSharp.Kernel.Logging;
            using SimulaSharp.Kernel.Model;
            using SimulaSharp.Kernel.Services;
            namespace Demo;

            public interface IPowerSource { double AvailableW { get; } }

            public sealed class Panel : ModelBase, IPowerSource
            {
                public Panel(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
                public double AvailableW => 42.0;
            }

            [References<IPowerSource>("Sources")]
            public partial class Grid : ModelBase
            {
                public Grid(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
                public async Task<double> Total()
                {
                    double sum = 0;
                    foreach (var src in Sources) sum += await src.GetAvailableW(Scheduler);
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
                      Refs:
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
        // One model carrying BOTH a [Contains<Wheel>] container and a [References<Wheel>] reference.
        const string source = """
            using System.Threading.Tasks;
            using SimulaSharp.Kernel.Attributes;
            using SimulaSharp.Kernel.Logging;
            using SimulaSharp.Kernel.Model;
            using SimulaSharp.Kernel.Services;
            namespace Demo;

            public sealed class Wheel : ModelBase
            {
                public Wheel(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
                public int Rpm { get; set; }
            }

            [Contains<Wheel>("Wheels")]
            [References<Wheel>("Spare")]
            public partial class Car : ModelBase
            {
                public Car(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
                public int WheelCount => Wheels.Count;
                public async Task<int> SpareRpm() => await Spare[0].GetRpm(Scheduler);
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
}
