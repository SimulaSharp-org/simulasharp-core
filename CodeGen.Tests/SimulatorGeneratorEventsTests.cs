namespace SimulaSharp.CodeGen.Tests;

/// <summary>
/// Tests <see cref="SimulatorGenerator"/> wiring of global events — the <c>Events:</c> block, and each instance's
/// <c>Subscriptions:</c> (<c>[EventHandler]</c>) / <c>Triggers:</c> (<c>[EventReference]</c>) — plus their strict
/// SIM023–SIM028 validation.
/// </summary>
[TestFixture]
public sealed class SimulatorGeneratorEventsTests
{
    private const string World = """
        using System;
        using SimulaSharp.Kernel.Attributes;
        using SimulaSharp.Kernel.Events;
        using SimulaSharp.Kernel.Logging;
        using SimulaSharp.Kernel.Model;
        using SimulaSharp.Kernel.Services;
        namespace Demo;

        public readonly record struct AbortInfo(int Code);

        // A typed producer + consumer of the "Abort" event.
        public partial class Nav : ModelBase
        {
            public Nav(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
            [EventHandler] public void OnAbort(AbortInfo info) { }
            [EventReference] public ISimEventInvocator<AbortInfo> AbortOut { get; set; } = null!;
            public void Plain(AbortInfo info) { }   // NOT an [EventHandler]
        }

        // An argless producer + consumer of the "Ping" event.
        public partial class Beacon : ModelBase
        {
            public Beacon(IClock c, IScheduler s, ILogger l) : base(c, s, l) { }
            [EventHandler] public void OnPing() { }
            [EventReference] public ISimEventInvocator PingOut { get; set; } = null!;
        }

        // A custom routing event that only IBusNode models may subscribe to.
        public interface IBusNode : IModel { }
        public sealed class Bus : ISimEvent<IBusNode>
        {
            public void Register(IBusNode subscriber, Action handler) { }
            public void Trigger() { }
        }

        public sealed class NotAnEvent { }
        """;

    private const string ValidYaml = """
        Simulator:
          Name: Sig
          Events:
            - Name: Abort
              ArgType: Demo.AbortInfo
            - Name: Ping
          Sites:
            - Name: Main
              Models:
                - Name: Nav
                  Type: Demo.Nav
                  Subscriptions:
                    - OnAbort: Abort
                  Triggers:
                    - AbortOut: Abort
                - Name: Beacon
                  Type: Demo.Beacon
                  Subscriptions:
                    - OnPing: Ping
                  Triggers:
                    - PingOut: Ping
        """;

    private static GenResult Run(string yaml, string world = World) =>
        GeneratorHarness.Run(new SimulatorGenerator(), world, yaml);

    [Test]
    public void Events_are_instantiated_and_rooted_by_kind()
    {
        var r = Run(ValidYaml);
        Assert.Multiple(() =>
        {
            Assert.That(r.Diagnostics, Is.Empty, "reported: " + string.Join(",", r.Ids));
            Assert.That(r.AllSource, Does.Contain("RegisterEvent(new global::SimulaSharp.Kernel.Events.SimEvent<global::Demo.AbortInfo>())"),
                "a typed event → SimEvent<T>");
            Assert.That(r.AllSource, Does.Contain("RegisterEvent(new global::SimulaSharp.Kernel.Events.SimEvent())"),
                "an argless event → SimEvent");
        });
    }

    [Test]
    public void Subscriptions_register_the_handler_and_triggers_fill_the_reference()
    {
        var r = Run(ValidYaml);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Match(@"__c\d+\.Register\(__c\d+, __c\d+\.OnAbort\);"), "OnAbort registered on its event");
            Assert.That(r.AllSource, Does.Match(@"__c\d+\.Register\(__c\d+, __c\d+\.OnPing\);"), "OnPing registered on its event");
            Assert.That(r.AllSource, Does.Match(@"__c\d+\.AbortOut = __c\d+;"), "AbortOut producer handle filled");
            Assert.That(r.AllSource, Does.Match(@"__c\d+\.PingOut = __c\d+;"), "PingOut producer handle filled");
        });
    }

    [Test]
    public void A_custom_event_type_is_honored()
    {
        // Demo.Bus is a custom argless ISimEvent<IBusNode>; Beacon merely triggers it (no subscription), so its
        // argless PingOut (ISimEventInvocator) is a valid producer handle.
        const string yaml = """
            Simulator:
              Name: Sig
              Events:
                - Name: Wire
                  Type: Demo.Bus
              Sites:
                - Name: Main
                  Models:
                    - Name: Beacon
                      Type: Demo.Beacon
                      Triggers:
                        - PingOut: Wire
            """;
        var r = Run(yaml);
        Assert.That(r.Diagnostics, Is.Empty, "reported: " + string.Join(",", r.Ids));
        Assert.That(r.AllSource, Does.Contain("RegisterEvent(new global::Demo.Bus())"));
    }

    [Test]
    public void The_generated_event_wiring_compiles()
    {
        var errors = GeneratorHarness.CompileWithAllGenerators(World, ValidYaml);
        Assert.That(errors, Is.Empty, "generated wiring did not compile: " + string.Join("\n", errors.Select(d => d.ToString())));
    }

    // ── Diagnostics ─────────────────────────────────────────────────────────────

    [Test]
    public void SIM023_unknown_event()
    {
        const string yaml = """
            Simulator:
              Name: Sig
              Events:
                - Name: Abort
                  ArgType: Demo.AbortInfo
              Sites:
                - Name: Main
                  Models:
                    - Name: Nav
                      Type: Demo.Nav
                      Subscriptions:
                        - OnAbort: Nope
            """;
        Assert.That(Run(yaml).HasDiagnostic("SIM023"), Is.True);
    }

    [Test]
    public void SIM024_handler_not_attributed()
    {
        const string yaml = """
            Simulator:
              Name: Sig
              Events:
                - Name: Abort
                  ArgType: Demo.AbortInfo
              Sites:
                - Name: Main
                  Models:
                    - Name: Nav
                      Type: Demo.Nav
                      Subscriptions:
                        - Plain: Abort
            """;
        Assert.That(Run(yaml).HasDiagnostic("SIM024"), Is.True);
    }

    [Test]
    public void SIM025_handler_signature_mismatch()
    {
        // OnAbort takes AbortInfo, but Ping is argless → the handler must take no parameters.
        const string yaml = """
            Simulator:
              Name: Sig
              Events:
                - Name: Ping
              Sites:
                - Name: Main
                  Models:
                    - Name: Nav
                      Type: Demo.Nav
                      Subscriptions:
                        - OnAbort: Ping
            """;
        Assert.That(Run(yaml).HasDiagnostic("SIM025"), Is.True);
    }

    [Test]
    public void SIM026_subscriber_not_assignable_to_TSub()
    {
        // Beacon (not an IBusNode) subscribes a valid argless handler to a Bus event whose TSub is IBusNode.
        const string yaml = """
            Simulator:
              Name: Sig
              Events:
                - Name: Wire
                  Type: Demo.Bus
              Sites:
                - Name: Main
                  Models:
                    - Name: Beacon
                      Type: Demo.Beacon
                      Subscriptions:
                        - OnPing: Wire
            """;
        Assert.That(Run(yaml).HasDiagnostic("SIM026"), Is.True);
    }

    [Test]
    public void SIM027_event_reference_wrong_invocator()
    {
        // PingOut is an argless ISimEventInvocator, but Abort is a typed event → member type mismatch.
        const string yaml = """
            Simulator:
              Name: Sig
              Events:
                - Name: Abort
                  ArgType: Demo.AbortInfo
              Sites:
                - Name: Main
                  Models:
                    - Name: Beacon
                      Type: Demo.Beacon
                      Triggers:
                        - PingOut: Abort
            """;
        Assert.That(Run(yaml).HasDiagnostic("SIM027"), Is.True);
    }

    [Test]
    public void SIM028_event_type_is_not_a_sim_event()
    {
        const string yaml = """
            Simulator:
              Name: Sig
              Events:
                - Name: Bad
                  Type: Demo.NotAnEvent
              Sites:
                - Name: Main
                  Models:
                    - Name: Beacon
                      Type: Demo.Beacon
            """;
        Assert.That(Run(yaml).HasDiagnostic("SIM028"), Is.True);
    }
}
