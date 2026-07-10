using System.Collections.Concurrent;
using SimulaSharp.Kernel.Logging;
using SimulaSharp.Kernel.Model;
using SimulaSharp.Kernel.Services;
using static SimulaSharp.Kernel.Tests.TestSupport;

namespace SimulaSharp.Kernel.Tests;

/// <summary>
/// Tests for the reshaped <see cref="LooselyCoupledServiceBase"/> — a dispatcher site plus run-state
/// hooks. They verify that <see cref="LooselyCoupledServiceBase.OnRunStarted"/>/<c>OnRunStopped</c> fire
/// inline on the clock thread at clock start/pause, and that work marshalled to <c>Site</c> runs on the
/// service's own (dispatcher) thread.
/// </summary>
[TestFixture]
[CancelAfter(15000)]
public sealed class LooselyCoupledServiceTests
{
    private static readonly DateTime Epoch = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A stand-in EGSE: records the run-state hooks (with the thread they fired on) and a marshalled request.</summary>
    private sealed class RecordingEgse(IClock clock, ILogger logger) : LooselyCoupledServiceBase(clock, logger, "Egse")
    {
        public readonly ConcurrentQueue<string> Log = new();
        private int _runHookThreadId, _dispatcherThreadId;
        public int RunHookThreadId => Volatile.Read(ref _runHookThreadId);
        public int DispatcherThreadId => Volatile.Read(ref _dispatcherThreadId);

        protected override void OnRunStarted()
        {
            Interlocked.Exchange(ref _runHookThreadId, Environment.CurrentManagedThreadId);
            Log.Enqueue("started");
        }

        protected override void OnRunStopped() => Log.Enqueue("stopped");

        /// <summary>Stands in for a generated Ch*Tx handler: marshalled onto the dispatcher and run there.</summary>
        public Task RecordOnDispatcher() =>
            Site.InjectVoidAsync(() =>
            {
                Interlocked.Exchange(ref _dispatcherThreadId, Environment.CurrentManagedThreadId);
                Log.Enqueue("dispatch");
            });
    }

    private static RecordingEgse NewEgse(IClock clock) =>
        new(clock, new ComponentLogger("Sim.Egse", clock, new CapturingSink()));

    [Test]
    public void OnRunStarted_fires_inline_on_the_clock_thread_at_start()
    {
        using var clock = new SimClock(Epoch);
        var egse = NewEgse(clock);
        egse.Site.Start();

        clock.Start();   // fires Started synchronously on THIS (test) thread

        Assert.Multiple(() =>
        {
            Assert.That(egse.Log, Does.Contain("started"));
            Assert.That(egse.RunHookThreadId, Is.EqualTo(Environment.CurrentManagedThreadId),
                "OnRunStarted must run inline on the clock/caller thread, not the dispatcher");
        });

        clock.Pause();
        egse.Site.Stop();
    }

    [Test]
    public void OnRunStopped_fires_when_the_clock_pauses()
    {
        using var clock = new SimClock(Epoch);
        var egse = NewEgse(clock);
        egse.Site.Start();
        clock.Start();

        clock.Pause();

        Assert.That(WaitUntil(() => egse.Log.Contains("stopped")), Is.True, "OnRunStopped did not fire on pause");

        egse.Site.Stop();
    }

    [Test]
    public void Marshalled_work_runs_on_the_dispatcher_thread()
    {
        using var clock = new SimClock(Epoch);
        var egse = NewEgse(clock);
        egse.Site.Start();

        egse.RecordOnDispatcher().Wait();

        Assert.Multiple(() =>
        {
            Assert.That(egse.Log, Does.Contain("dispatch"));
            Assert.That(egse.DispatcherThreadId, Is.Not.EqualTo(Environment.CurrentManagedThreadId),
                "marshalled work must run on the service's own dispatcher thread, not the caller");
        });

        egse.Site.Stop();
    }

    [Test]
    public void A_start_pause_cycle_records_the_hooks_in_order()
    {
        using var clock = new SimClock(Epoch);
        var egse = NewEgse(clock);
        egse.Site.Start();

        clock.Start();
        Assert.That(WaitUntil(() => egse.Log.Contains("started")), Is.True);
        clock.Pause();
        Assert.That(WaitUntil(() => egse.Log.Contains("stopped")), Is.True);

        egse.Site.Stop();
        egse.Dispose();

        Assert.That(egse.Log, Is.EqualTo(new[] { "started", "stopped" }));
    }
}
