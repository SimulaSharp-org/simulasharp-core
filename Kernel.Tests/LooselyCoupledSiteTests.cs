using System.Collections.Concurrent;
using SimulaSharp.Kernel.Services;
using static SimulaSharp.Kernel.Tests.TestSupport;

namespace SimulaSharp.Kernel.Tests;

/// <summary>
/// Tests for <see cref="LooselyCoupledSite"/> — the clock-ungated I/O site. Unlike a
/// <see cref="SimSite"/> it has no clock, never parks, and does not auto-drain: the owning service's
/// loop (here a tiny test loop) drains the inbox itself. These verify the ungated execution, the
/// <see cref="IDispatcher"/> marshalling, the single-thread guarantee, and the lifecycle.
/// </summary>
[TestFixture]
[CancelAfter(15000)]   // safety net: these run in well under a second; a hang fails fast
public sealed class LooselyCoupledSiteTests
{
    /// <summary>
    /// Builds a site whose entry point loops until shutdown, draining the inbox each pass. Records the
    /// thread it runs on and whether it exited cleanly, so tests can assert the single-thread guarantee
    /// and the shutdown handshake.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        public readonly LooselyCoupledSite Site;
        public int LoopThreadId;
        public bool Exited;

        public Harness()
        {
            LooselyCoupledSite site = null!;
            async Task Entry()
            {
                Interlocked.Exchange(ref LoopThreadId, Environment.CurrentManagedThreadId);
                while (!site.IsStopping)
                {
                    site.DrainInbox();
                    await Task.Delay(1);       // yields CPU; its continuation resumes on THIS thread
                    // Every await must resume on the site thread — assert it stays put.
                    Interlocked.Exchange(ref LoopThreadId, Environment.CurrentManagedThreadId);
                }
                Volatile.Write(ref Exited, true);
            }
            Site = site = new LooselyCoupledSite("test", Entry);
        }

        public void Dispose() => Site.Dispose();
    }

    [Test]
    public void Posted_work_runs_even_though_no_clock_ever_started()
    {
        // The site has no clock at all: a SimSite would park while paused; this one just runs.
        using var h = new Harness();
        h.Site.Start();
        int n = 0;

        h.Site.Post(() => Interlocked.Increment(ref n));

        Assert.That(WaitUntil(() => Volatile.Read(ref n) == 1), Is.True, "ungated work did not run");
    }

    [Test]
    public void InjectAsync_returns_the_result_and_faults_on_throw()
    {
        using var h = new Harness();
        h.Site.Start();

        Assert.That(h.Site.InjectAsync(() => 42).Result, Is.EqualTo(42));
        Assert.That(() => h.Site.InjectAsync<int>(() => throw new InvalidOperationException("boom")).GetAwaiter().GetResult(),
            Throws.InvalidOperationException);
    }

    [Test]
    public void InjectVoidAsync_completes_after_the_work_runs()
    {
        using var h = new Harness();
        h.Site.Start();
        int n = 0;

        h.Site.InjectVoidAsync(() => Interlocked.Increment(ref n)).Wait();

        Assert.That(Volatile.Read(ref n), Is.EqualTo(1), "the task completed before its work ran");
    }

    [Test]
    public void All_inbox_work_and_await_continuations_run_on_the_one_site_thread()
    {
        using var h = new Harness();
        h.Site.Start();
        var ids = new ConcurrentBag<int>();
        const int threads = 8, per = 200;

        Parallel.For(0, threads, _ =>
        {
            for (int i = 0; i < per; i++)
                h.Site.Post(() => ids.Add(Environment.CurrentManagedThreadId));
        });

        Assert.That(WaitUntil(() => ids.Count == threads * per, 5000), Is.True, "not all posted work ran");
        Assert.Multiple(() =>
        {
            Assert.That(ids.Distinct().Count(), Is.EqualTo(1), "inbox work ran on more than one thread");
            Assert.That(ids.First(), Is.EqualTo(Volatile.Read(ref h.LoopThreadId)),
                "inbox work did not run on the loop thread (await continuations must resume there too)");
        });
    }

    [Test]
    public void Stop_signals_shutdown_lets_the_entry_point_finish_and_joins()
    {
        using var h = new Harness();
        h.Site.Start();
        Assert.That(WaitUntil(() => Volatile.Read(ref h.LoopThreadId) != 0), Is.True, "entry point never started");

        h.Site.Stop();   // joins the thread, so by the time it returns the loop has exited

        Assert.Multiple(() =>
        {
            Assert.That(h.Site.IsStopping, Is.True);
            Assert.That(Volatile.Read(ref h.Exited), Is.True, "entry loop did not exit on shutdown");
        });
    }

    [Test]
    public void Start_before_stop_is_safe_and_stop_dispose_are_idempotent()
    {
        var h = new Harness();
        h.Site.Start();
        h.Site.Start();   // second start is a no-op (already running)

        Assert.DoesNotThrow(() =>
        {
            h.Site.Stop();
            h.Site.Stop();       // idempotent
            h.Site.Dispose();
            h.Site.Dispose();    // idempotent
        });
    }
}
