using SimulaSharp.Kernel.Logging;
using SimulaSharp.Kernel.Services;

namespace SimulaSharp.Kernel.Tests;

[TestFixture]
public sealed class ServiceEventChannelTests
{
    [Test]
    public void Trigger_posts_every_subscribers_callback()
    {
        var channel = new ServiceEventChannel();
        var sched = new SyncScheduler();
        int a = 0, b = 0;
        channel.Register(sched, () => a++);
        channel.Register(sched, () => b++);

        channel.Trigger();

        Assert.Multiple(() =>
        {
            Assert.That(a, Is.EqualTo(1));
            Assert.That(b, Is.EqualTo(1));
        });
    }

    [Test]
    public void A_channel_with_no_subscribers_is_a_noop()
    {
        Assert.DoesNotThrow(() => new ServiceEventChannel().Trigger());
    }

    [Test]
    public void Generic_channel_delivers_the_payload()
    {
        var channel = new ServiceEventChannel<int>();
        var sched = new SyncScheduler();
        int received = 0;
        channel.Register(sched, v => received = v);

        channel.Trigger(99);

        Assert.That(received, Is.EqualTo(99));
    }
}

[TestFixture]
public sealed class ServiceBaseTests
{
    private static readonly DateTime Epoch = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    public void A_service_exposes_its_own_site_and_the_injected_dependencies()
    {
        using var clock = new SimClock(Epoch);
        var logger = new ComponentLogger("Sim.Svc", clock, new CapturingSink());
        using var svc = new TestService(clock, logger);

        Assert.Multiple(() =>
        {
            Assert.That(svc.TimeKeeper, Is.SameAs(clock));
            Assert.That(svc.Logger, Is.SameAs(logger));
            Assert.That(svc.Site, Is.Not.Null, "a service must own a site (its marshalling + lifecycle surface)");
        });
    }

    [Test]
    public void Configure_and_Dispose_hooks_run()
    {
        using var clock = new SimClock(Epoch);
        var svc = new TestService(clock, new ComponentLogger("Sim.Svc", clock, new CapturingSink()));

        svc.Configure();
        svc.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(svc.Configured, Is.True);
            Assert.That(svc.Disposed, Is.True);
        });
    }
}
