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
            Assert.That(r.AllSource, Does.Contain("Scheduler.InjectVoidAsync(() => Handle_DoIt())"));
        });
    }

    [Test]
    public void A_returning_request_marshals_via_InjectAsync_with_the_result_type()
    {
        var r = GeneratorHarness.Run(new ServiceGenerator(), Service);
        Assert.Multiple(() =>
        {
            Assert.That(r.AllSource, Does.Contain("Task<int> Compute(int x)"));
            Assert.That(r.AllSource, Does.Contain("Scheduler.InjectAsync(() => Handle_Compute(x))"));
        });
    }
}
