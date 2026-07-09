using System.Collections.Concurrent;
using SimulaSharp.Kernel.Logging;
using SimulaSharp.Kernel.Model;
using SimulaSharp.Kernel.Services;
using static SimulaSharp.Kernel.Tests.TestSupport;

namespace SimulaSharp.Kernel.Tests;

/// <summary>
/// Tests for <see cref="LooselyCoupledServiceBase"/> — the clock-ungated service base. They drive its
/// author contract (<c>_EntryPoint</c> + <see cref="LooselyCoupledServiceBase.WaitRunning"/> gating +
/// <c>ServiceInbox</c>) through the canonical halt/unhalt loop, asserting the loop proceeds only once
/// the clock runs, breaks on end-of-life, and runs marshalled work on its own thread.
/// </summary>
[TestFixture]
[CancelAfter(15000)]   // safety net: real-time gating; a hang fails fast, not indefinitely
public sealed class LooselyCoupledServiceTests
{
    private static readonly DateTime Epoch = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A stand-in EGSE: the canonical loop that halts "hardware" at setup, unhalts on the rising edge
    /// (clock started), halts again on the falling edge (clock paused), and closes at end-of-life. Each
    /// transition is recorded so a test can assert order; marshalled work records the thread it ran on.
    /// </summary>
    private sealed class RecordingEgse(IClock clock, ILogger logger) : LooselyCoupledServiceBase(clock, logger, "Egse")
    {
        public readonly ConcurrentQueue<string> Log = new();
        private int _entryThreadId;
        public int EntryThreadId => Volatile.Read(ref _entryThreadId);

        protected override async Task _EntryPoint()
        {
            Interlocked.Exchange(ref _entryThreadId, Environment.CurrentManagedThreadId);
            Log.Enqueue("connect");
            Log.Enqueue("halt");                          // setup: hardware halted
            while (!await WaitRunning())                  // false = clock running; true = disposing → exit
            {
                Log.Enqueue("unhalt");                    // rising edge — in lockstep with the sim
                while (TimeKeeper.IsRunning && !Stopping)
                {
                    ServiceInbox();                       // run any marshalled requests/events inline
                    await Task.Delay(1);
                }
                Log.Enqueue("halt");                      // falling edge — freeze with the sim
            }
            Log.Enqueue("close");
        }
    }

    private static RecordingEgse NewEgse(IClock clock) =>
        new(clock, new ComponentLogger("Sim.Egse", clock, new CapturingSink()));

    private static bool LogContains(RecordingEgse e, string entry, int timeoutMs = 2000) =>
        WaitUntil(() => e.Log.Contains(entry), timeoutMs);

    [Test]
    public void The_loop_halts_at_setup_and_waits_before_the_clock_starts()
    {
        using var clock = new SimClock(Epoch);   // not started
        var egse = NewEgse(clock);
        egse.Site.Start();

        Assert.That(LogContains(egse, "halt"), Is.True, "setup halt did not run");
        Assert.Multiple(() =>
        {
            Assert.That(egse.Log, Does.Contain("connect"));
            Assert.That(egse.Log, Does.Not.Contain("unhalt"), "unhalted before the clock started");
        });

        egse.Site.Stop();
    }

    [Test]
    public void WaitRunning_proceeds_once_the_clock_starts()
    {
        using var clock = new SimClock(Epoch);
        var egse = NewEgse(clock);
        egse.Site.Start();
        Assert.That(LogContains(egse, "halt"), Is.True);

        clock.Start();   // rising edge

        Assert.That(LogContains(egse, "unhalt"), Is.True, "the loop did not proceed when the clock started");

        clock.Pause();
        egse.Site.Stop();
    }

    [Test]
    public void The_loop_halts_again_when_the_clock_pauses()
    {
        using var clock = new SimClock(Epoch);
        var egse = NewEgse(clock);
        egse.Site.Start();
        clock.Start();
        Assert.That(LogContains(egse, "unhalt"), Is.True);

        clock.Pause();   // falling edge

        // After the falling edge the loop re-parks in WaitRunning; the second "halt" must appear.
        Assert.That(WaitUntil(() => egse.Log.Count(x => x == "halt") == 2), Is.True, "no halt on the falling edge");

        egse.Site.Stop();
    }

    [Test]
    public void WaitRunning_breaks_on_end_of_life_so_the_loop_closes()
    {
        using var clock = new SimClock(Epoch);   // never started
        var egse = NewEgse(clock);
        egse.Site.Start();
        Assert.That(LogContains(egse, "halt"), Is.True);

        egse.Site.Stop();   // end-of-life while parked in WaitRunning → returns true → exit

        Assert.Multiple(() =>
        {
            Assert.That(egse.Log, Does.Contain("close"), "the loop did not close on end-of-life");
            Assert.That(egse.Log, Does.Not.Contain("unhalt"), "unhalted though the clock never started");
        });
    }

    [Test]
    public void ServiceInbox_runs_marshalled_work_on_the_service_thread()
    {
        using var clock = new SimClock(Epoch);
        var egse = NewEgse(clock);
        egse.Site.Start();
        clock.Start();
        Assert.That(LogContains(egse, "unhalt"), Is.True);
        int workThreadId = 0;

        // Marshal work through the same IDispatcher facet the [ServiceRequest]/[ServiceEvent] duo uses.
        egse.Site.InjectVoidAsync(() => Interlocked.Exchange(ref workThreadId, Environment.CurrentManagedThreadId)).Wait();

        Assert.That(Volatile.Read(ref workThreadId), Is.EqualTo(egse.EntryThreadId),
            "marshalled work did not run on the service's own thread");

        clock.Pause();
        egse.Site.Stop();
    }

    [Test]
    public void A_full_start_stop_dispose_cycle_records_the_expected_transitions()
    {
        using var clock = new SimClock(Epoch);
        var egse = NewEgse(clock);

        egse.Site.Start();
        Assert.That(LogContains(egse, "halt"), Is.True);
        clock.Start();
        Assert.That(LogContains(egse, "unhalt"), Is.True);
        clock.Pause();
        Assert.That(WaitUntil(() => egse.Log.Count(x => x == "halt") == 2), Is.True);
        egse.Site.Stop();
        egse.Dispose();

        Assert.That(egse.Log, Is.EqualTo(new[] { "connect", "halt", "unhalt", "halt", "close" }));
    }
}
