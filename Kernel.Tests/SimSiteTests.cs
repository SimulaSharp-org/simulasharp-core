using SimulaSharp.Kernel.Primitives;
using SimulaSharp.Kernel.Services;
using static SimulaSharp.Kernel.Tests.TestSupport;

namespace SimulaSharp.Kernel.Tests;

/// <summary>
/// Real-time tests for <see cref="SimSite"/>: scheduling on the site thread, cross-thread
/// marshalling, mission-time scheduling, and clock-driven parking. Scheduling calls are marshalled
/// onto the site thread with <see cref="SimSite.InjectVoidAsync"/> (the queue is single-threaded by
/// design), and off-thread effects are observed with a bounded <see cref="WaitUntil"/>.
/// </summary>
[TestFixture]
[CancelAfter(15000)]   // safety net: real-time tests take well under a second; a hang fails fast, not for 39 min
public sealed class SimSiteTests
{
    private static readonly DateTime Epoch = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Builds a started site over a started clock; disposes both at the end of the test.</summary>
    private static (SimClock clock, SimSite site) NewRunning()
    {
        var clock = new SimClock(Epoch);
        var site = new SimSite("test", clock);
        site.Start();
        clock.Start();
        return (clock, site);
    }

    [Test]
    public void AddEvent_fires_after_its_delay()
    {
        var (clock, site) = NewRunning();
        using var _ = clock; using var __ = site;
        int n = 0;

        site.InjectVoidAsync(() => site.AddEvent(() => Interlocked.Increment(ref n), 20_000)).Wait();
        clock.RunFor(40_000);

        Assert.That(WaitUntil(() => Volatile.Read(ref n) == 1), Is.True);
    }

    [Test]
    public void AddPeriodicEvent_fires_repeatedly()
    {
        var (clock, site) = NewRunning();
        using var _ = clock; using var __ = site;
        int n = 0;

        site.InjectVoidAsync(() => site.AddPeriodicEvent(() => Interlocked.Increment(ref n), 10_000)).Wait();
        clock.RunFor(55_000);

        Assert.That(WaitUntil(() => Volatile.Read(ref n) >= 4), Is.True, "periodic did not fire ~5 times over 55ms");
    }

    [Test]
    public void A_tight_periodic_keeps_its_cadence_under_precise_waking()
    {
        // Regression guard for clock-driven waking: a 5 ms periodic over 50 ms must fire ~10 times.
        // A park that trusts the OS timer (~15 ms granularity) would drop most of these — this asserts
        // the clock delivers the on-time wake, so idle sites park (no busy-spin) without losing cadence.
        var (clock, site) = NewRunning();
        using var _ = clock; using var __ = site;
        int n = 0;

        site.InjectVoidAsync(() => site.AddPeriodicEvent(() => Interlocked.Increment(ref n), 5_000)).Wait();
        clock.RunFor(50_000);

        Assert.That(WaitUntil(() => Volatile.Read(ref n) >= 8), Is.True,
            $"tight periodic fired only {Volatile.Read(ref n)} times in 50 ms — waking is not on time");
    }

    [Test]
    public void AddCoroutine_steps_across_yields()
    {
        var (clock, site) = NewRunning();
        using var _ = clock; using var __ = site;
        int step = 0;
        IEnumerator<SimYield> Body()
        {
            Interlocked.Exchange(ref step, 1); yield return SimYield.Delay(10_000);
            Interlocked.Exchange(ref step, 2); yield return SimYield.Delay(10_000);
            Interlocked.Exchange(ref step, 3);
        }

        site.InjectVoidAsync(() => site.AddCoroutine(Body, 5_000)).Wait();
        clock.RunFor(40_000);

        Assert.That(WaitUntil(() => Volatile.Read(ref step) == 3), Is.True);
    }

    [Test]
    public void AddEventAtMission_fires_at_the_instant_and_drops_past_instants()
    {
        var (clock, site) = NewRunning();
        using var _ = clock; using var __ = site;
        int future = 0, past = 0;

        long pastId = site.InjectAsync(() =>
            site.AddEventAtMission(() => Interlocked.Increment(ref past), clock.MissionNowUs - 10_000)).Result;
        Assert.That(pastId, Is.EqualTo(-1), "a past mission instant was not dropped");

        site.InjectVoidAsync(() =>
            site.AddEventAtMission(() => Interlocked.Increment(ref future), clock.MissionNowUs + 20_000)).Wait();
        clock.RunFor(40_000);

        Assert.Multiple(() =>
        {
            Assert.That(WaitUntil(() => Volatile.Read(ref future) == 1), Is.True, "future mission event did not fire");
            Assert.That(Volatile.Read(ref past), Is.EqualTo(0));
        });
    }

    [Test]
    public void Post_runs_on_the_site_thread()
    {
        var (clock, site) = NewRunning();
        using var _ = clock; using var __ = site;
        string? threadName = null;

        site.Post(() => threadName = Thread.CurrentThread.Name);

        Assert.That(WaitUntil(() => threadName is not null), Is.True);
        Assert.That(threadName, Does.StartWith("SimSite-"));
    }

    [Test]
    public void InjectAsync_returns_the_result_and_faults_on_throw()
    {
        var (clock, site) = NewRunning();
        using var _ = clock; using var __ = site;

        Assert.That(site.InjectAsync(() => 42).Result, Is.EqualTo(42));
        Assert.That(() => site.InjectAsync<int>(() => throw new InvalidOperationException("boom")).GetAwaiter().GetResult(),
            Throws.InvalidOperationException);
    }

    [Test]
    public void Cross_thread_posts_run_serialized_on_the_one_site_thread()
    {
        var (clock, site) = NewRunning();
        using var _ = clock; using var __ = site;
        int shared = 0;                    // deliberately NON-atomic: serialization must protect it
        const int threads = 8, per = 500;
        var tasks = new List<Task>();

        Parallel.For(0, threads, _ =>
        {
            for (int i = 0; i < per; i++)
                lock (tasks) tasks.Add(site.InjectVoidAsync(() => shared++));
        });

        Assert.That(WaitUntil(() => Task.WhenAll(tasks).IsCompleted, 5000), Is.True);
        Assert.That(shared, Is.EqualTo(threads * per), "non-atomic increments raced — work was not serialized");
    }

    [Test]
    public void An_awaiting_coroutine_resumes_when_its_task_completes()
    {
        var (clock, site) = NewRunning();
        using var _ = clock; using var __ = site;
        var gate = new TaskCompletionSource();
        int step = 0;
        IEnumerator<SimYield> Body()
        {
            Interlocked.Exchange(ref step, 1);
            yield return SimYield.Await(gate.Task);
            Interlocked.Exchange(ref step, 2);
        }

        site.InjectVoidAsync(() => site.AddCoroutine(Body, 0)).Wait();
        clock.RunFor(20_000);
        Assert.That(WaitUntil(() => Volatile.Read(ref step) == 1), Is.True, "coroutine did not reach the await");

        gate.SetResult();                  // completing the task must re-arm the coroutine on its site
        clock.RunFor(20_000);
        Assert.That(WaitUntil(() => Volatile.Read(ref step) == 2), Is.True, "coroutine did not resume after its task completed");
    }

    [Test]
    public void A_paused_clock_parks_the_site_so_no_work_runs()
    {
        var (clock, site) = NewRunning();
        using var _ = clock; using var __ = site;
        int n = 0;

        site.InjectVoidAsync(() => site.AddPeriodicEvent(() => Interlocked.Increment(ref n), 10_000)).Wait();
        clock.RunFor(45_000);              // fires a few times, then RunFor pauses the clock
        int atPause = Volatile.Read(ref n);

        Thread.Sleep(50);                  // clock is paused → site is parked
        Assert.That(Volatile.Read(ref n), Is.EqualTo(atPause), "work ran while the clock was paused");
    }

    [Test]
    public void RemoveEvent_cancels_a_periodic()
    {
        var (clock, site) = NewRunning();
        using var _ = clock; using var __ = site;
        int n = 0;

        int id = site.InjectAsync(() => site.AddPeriodicEvent(() => Interlocked.Increment(ref n), 10_000)).Result;
        clock.RunFor(25_000);
        Assert.That(WaitUntil(() => Volatile.Read(ref n) >= 2), Is.True);

        // RunFor left the clock paused (site parked); resume so the site drains the removal request.
        clock.Start();
        site.InjectVoidAsync(() => site.RemoveEvent(id)).Wait();
        int afterRemove = Volatile.Read(ref n);

        clock.RunFor(40_000);
        Assert.That(Volatile.Read(ref n), Is.EqualTo(afterRemove), "periodic fired after RemoveEvent");
    }
}
