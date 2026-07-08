using SimulaSharp.Kernel.Logging;
using SimulaSharp.Kernel.Services;

namespace SimulaSharp.Kernel.Tests;

[TestFixture]
public sealed class LoggingTests
{
    private static readonly DateTime Epoch = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Test]
    public void LogRouter_fans_out_to_every_registered_sink()
    {
        var router = new LogRouter();
        var a = new CapturingSink();
        var b = new CapturingSink();
        router.Add(a);
        router.Add(b);

        router.Submit(Entry("x"));

        Assert.Multiple(() =>
        {
            Assert.That(a.Entries, Has.Count.EqualTo(1));
            Assert.That(b.Entries, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void LogRouter_with_no_sinks_is_silent()
    {
        var router = new LogRouter();
        Assert.DoesNotThrow(() => router.Submit(Entry("x")));
    }

    [Test]
    public void LogRouter_Add_rejects_null()
    {
        Assert.That(() => new LogRouter().Add(null!), Throws.ArgumentNullException);
    }

    [Test]
    public void ComponentLogger_stamps_issuer_level_times_message_and_call_site()
    {
        using var clock = new SimClock(Epoch);
        var sink = new CapturingSink();
        ILogger logger = new ComponentLogger("Sim.Widget", clock, sink);

        logger.LogWarning("hello");

        Assert.That(sink.Entries, Has.Count.EqualTo(1));
        LogEntry e = sink.Entries[0];
        Assert.Multiple(() =>
        {
            Assert.That(e.Issuer, Is.EqualTo("Sim.Widget"));
            Assert.That(e.Level, Is.EqualTo(LogLevel.Warning));
            Assert.That(e.Message, Is.EqualTo("hello"));
            Assert.That(e.MissionTimeUs, Is.EqualTo(e.SimTimeUs + (long)((Epoch - DateTime.UnixEpoch).TotalSeconds * 1_000_000.0)));
            Assert.That(e.Member, Is.EqualTo(nameof(ComponentLogger_stamps_issuer_level_times_message_and_call_site)));
            Assert.That(e.File, Does.EndWith("LoggingTests.cs"));
            Assert.That(e.Line, Is.GreaterThan(0));
        });
    }

    [Test]
    public void ComponentLogger_LogTrace_maps_depth_to_the_trace_levels()
    {
        using var clock = new SimClock(Epoch);
        var sink = new CapturingSink();
        ILogger logger = new ComponentLogger("Sim.Widget", clock, sink);

        logger.LogTrace(3, "t");
        Assert.That(sink.Entries[0].Level, Is.EqualTo(LogLevel.Trace3));
        Assert.That(() => logger.LogTrace(0, "bad"), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void LogLevel_ToCode_returns_the_three_letter_codes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LogLevel.Error.ToCode(), Is.EqualTo("ERR"));
            Assert.That(LogLevel.Information.ToCode(), Is.EqualTo("INF"));
            Assert.That(LogLevel.Trace6.ToCode(), Is.EqualTo("TR6"));
        });
    }

    private static LogEntry Entry(string message) =>
        new(0, 0, DateTime.UtcNow, "issuer", LogLevel.Information, message, null, null, 0);
}
