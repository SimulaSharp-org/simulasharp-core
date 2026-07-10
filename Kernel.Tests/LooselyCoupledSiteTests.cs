using System.Collections.Concurrent;
using SimulaSharp.Kernel.Services;
using static SimulaSharp.Kernel.Tests.TestSupport;

namespace SimulaSharp.Kernel.Tests;

/// <summary>
/// Tests for <see cref="LooselyCoupledSite"/> — the clock-ungated dispatcher site. It is a pure
/// Post-driven serial executor: it runs marshalled work on its one thread and sleeps when idle. There
/// is no clock and no event queue, so these verify the executor drains/serializes work, parks and wakes
/// on <c>Post</c>, and tears down cleanly.
/// </summary>
[TestFixture]
[CancelAfter(15000)]   // safety net: these run in well under a second; a hang fails fast
public sealed class LooselyCoupledSiteTests
{
    private static LooselyCoupledSite NewStarted()
    {
        var site = new LooselyCoupledSite("test");
        site.Start();
        return site;
    }

    [Test]
    public void Posted_work_runs_with_no_clock_involved()
    {
        // The site is clock-ungated — no clock exists here at all; a Post just runs.
        using var site = NewStarted();
        int n = 0;

        site.Post(() => Interlocked.Increment(ref n));

        Assert.That(WaitUntil(() => Volatile.Read(ref n) == 1), Is.True, "posted work did not run");
    }

    [Test]
    public void InjectAsync_returns_the_result_and_faults_on_throw()
    {
        using var site = NewStarted();

        Assert.That(site.InjectAsync(() => 42).Result, Is.EqualTo(42));
        Assert.That(() => site.InjectAsync<int>(() => throw new InvalidOperationException("boom")).GetAwaiter().GetResult(),
            Throws.InvalidOperationException);
    }

    [Test]
    public void InjectVoidAsync_completes_after_the_work_runs()
    {
        using var site = NewStarted();
        int n = 0;

        site.InjectVoidAsync(() => Interlocked.Increment(ref n)).Wait();

        Assert.That(Volatile.Read(ref n), Is.EqualTo(1), "the task completed before its work ran");
    }

    [Test]
    public void All_work_runs_serialized_on_the_one_site_thread()
    {
        using var site = NewStarted();
        var ids = new ConcurrentBag<int>();
        int shared = 0;                    // deliberately NON-atomic: serialization must protect it
        const int threads = 8, per = 200;

        Parallel.For(0, threads, _ =>
        {
            for (int i = 0; i < per; i++)
                site.Post(() => { shared++; ids.Add(Environment.CurrentManagedThreadId); });
        });

        Assert.That(WaitUntil(() => ids.Count == threads * per, 5000), Is.True, "not all posted work ran");
        Assert.Multiple(() =>
        {
            Assert.That(ids.Distinct().Count(), Is.EqualTo(1), "work ran on more than one thread");
            Assert.That(shared, Is.EqualTo(threads * per), "non-atomic increments raced — work was not serialized");
        });
    }

    [Test]
    public void An_idle_executor_parks_then_wakes_on_a_later_post()
    {
        using var site = NewStarted();
        int n = 0;

        site.Post(() => Interlocked.Increment(ref n));
        Assert.That(WaitUntil(() => Volatile.Read(ref n) == 1), Is.True);

        Thread.Sleep(50);                  // the executor is now parked (inbox empty)
        site.Post(() => Interlocked.Increment(ref n));

        Assert.That(WaitUntil(() => Volatile.Read(ref n) == 2), Is.True, "a post did not wake the parked executor");
    }

    [Test]
    public void Stop_and_Dispose_are_idempotent()
    {
        var site = NewStarted();
        Assert.DoesNotThrow(() =>
        {
            site.Stop();
            site.Stop();
            site.Dispose();
            site.Dispose();
        });
    }
}
