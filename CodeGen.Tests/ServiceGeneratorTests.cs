namespace SimulaSharp.CodeGen.Tests;

[TestFixture]
public sealed class ServiceGeneratorTests
{
    private const string Service = """
        using SimulaSharp.Kernel.Attributes;
        using SimulaSharp.Kernel.Logging;
        using SimulaSharp.Kernel.Model;
        using SimulaSharp.Kernel.Services;
        namespace Demo;
        [ServiceEvent("Ping")]
        [ServiceEvent("Data", typeof(int))]
        public partial class Svc : ServiceBase
        {
            public Svc(IClock c, ILogger l) : base(c, l, "Svc") { }
            [ServiceRequest] protected void Handle_DoIt() { }
            [ServiceRequest] protected int Handle_Compute(int x) => x;
        }
        """;

    [Test]
    public void A_payload_less_event_emits_channel_register_and_trigger()
    {
        var r = GeneratorHarness.Run(new ServiceGenerator(), Service);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("__evt_Ping"));
            Assert.That(r.AllSource, Does.Contain("public void RegisterCallback_Ping("));
            Assert.That(r.AllSource, Does.Contain("global::SimulaSharp.Kernel.Services.IDispatcher dispatcher"),
                "subscribers register with their site's IDispatcher facet");
            Assert.That(r.AllSource, Does.Contain("protected void Trigger_Ping()"));
        });
    }

    [Test]
    public void A_payload_event_is_generic_over_its_arg_type()
    {
        var r = GeneratorHarness.Run(new ServiceGenerator(), Service);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("ServiceEventChannel<int> __evt_Data"));
            Assert.That(r.AllSource, Does.Contain("Trigger_Data(int value)"));
        });
    }

    [Test]
    public void A_void_request_marshals_via_InjectVoidAsync()
    {
        var r = GeneratorHarness.Run(new ServiceGenerator(), Service);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("public global::System.Threading.Tasks.Task DoIt()"));
            Assert.That(r.AllSource, Does.Contain("Site.InjectVoidAsync(() => Handle_DoIt())"));
        });
    }

    [Test]
    public void A_returning_request_marshals_via_InjectAsync_with_the_result_type()
    {
        var r = GeneratorHarness.Run(new ServiceGenerator(), Service);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("Task<int> Compute(int x)"));
            Assert.That(r.AllSource, Does.Contain("Site.InjectAsync(() => Handle_Compute(x))"));
        });
    }

    [Test]
    public void Zero_or_one_parameter_requests_do_not_trip_SIM022()
    {
        // The shared fixture has a zero-arg (DoIt) and a one-arg (Compute) request — both legal.
        var r = GeneratorHarness.Run(new ServiceGenerator(), Service);
        Assert.That(r.HasDiagnostic("SIM022"), Is.False);
    }

    [Test]
    public void A_request_with_more_than_one_parameter_is_SIM022_and_is_not_emitted()
    {
        const string twoParam = """
            using SimulaSharp.Kernel.Attributes;
            using SimulaSharp.Kernel.Logging;
            using SimulaSharp.Kernel.Model;
            using SimulaSharp.Kernel.Services;
            namespace Demo;
            public partial class Svc : ServiceBase
            {
                public Svc(IClock c, ILogger l) : base(c, l, "Svc") { }
                [ServiceRequest] protected int Handle_Compute(int x, double y) => x;
            }
            """;

        var r = GeneratorHarness.Run(new ServiceGenerator(), twoParam);
        Assert.Multiple(() =>
        {
            Assert.That(r.HasDiagnostic("SIM022"), Is.True, "a two-param request must be rejected");
            Assert.That(r.AllSource, Does.Not.Contain("Compute("), "the malformed request must not generate a caller");
        });
    }
}
