using System.Diagnostics;
using SimulaSharp.Kernel.Primitives;
using SimulaSharp.Kernel.Services;
using static SimulaSharp.Kernel.Tests.TestSupport;

namespace SimulaSharp.Kernel.Tests;

/// <summary>
/// Real-time tests for <see cref="SimClock"/>. There is no manual clock by design, so these use
/// short real durations and assert both the horizon and the wall-clock elapsed (the hard-real-time
/// 1:1 property). Tolerances are generous to survive shared-machine load.
/// </summary>
[TestFixture]
[CancelAfter(15000)]   // safety net: a real-time hang fails fast instead of blocking the run
public sealed class SimClockTests
{
    private static readonly DateTime Epoch = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static long EpochUs => (long)((Epoch - DateTime.UnixEpoch).TotalSeconds * 1_000_000.0);

    [Test]
    public void RunFor_advances_the_horizon_and_takes_real_time_then_pauses()
    {
        using var clock = new SimClock(Epoch);

        var sw = Stopwatch.StartNew();
        clock.RunFor(200_000);              // 200 ms of sim time == ~200 ms wall time
        sw.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(clock.IsRunning, Is.False, "RunFor did not pause at the bound");
            Assert.That(clock.SimNowUs, Is.InRange(190_000, 260_000), "horizon did not reach ~200ms");
            AssertElapsed(sw.ElapsedMilliseconds, expectedMs: 200, tolMs: 120);
        });
    }

    [Test]
    public void Pause_freezes_the_horizon_and_resume_accumulates()
    {
        using var clock = new SimClock(Epoch);

        clock.RunFor(60_000);
        long h1 = clock.SimNowUs;
        Assert.That(clock.IsRunning, Is.False);

        Thread.Sleep(40);                   // frozen while paused
        Assert.That(clock.SimNowUs, Is.EqualTo(h1), "horizon advanced while paused");

        clock.RunFor(60_000);               // resumes and runs another segment
        Assert.That(clock.SimNowUs, Is.GreaterThan(h1 + 40_000), "resume did not accumulate onto the prior segment");
    }

    [Test]
    public void MissionNow_is_epoch_plus_horizon_and_snapshot_is_consistent()
    {
        using var clock = new SimClock(Epoch);

        Assert.That(clock.MissionNowUs, Is.EqualTo(EpochUs), "mission time at horizon 0 is not the epoch");

        clock.RunFor(50_000);
        TimeSnapshot t = clock.Now();
        Assert.That(t.MissionTimeUs, Is.EqualTo(EpochUs + t.SimTimeUs), "snapshot mission/sim pair is inconsistent");
    }

    private sealed class FixedMissionProvider(long missionUs) : IMissionTimeProvider
    {
        public long MissionNowUs { get; set; } = missionUs;
    }

    [Test]
    public void An_installed_mission_time_provider_overrides_the_default_without_touching_sim_time()
    {
        using var clock = new SimClock(Epoch);
        clock.RunFor(50_000);                 // advance, then paused → horizon is stable
        long simAfter = clock.SimNowUs;

        var fes = new FixedMissionProvider(9_000_000_000L);   // a warped mission time unrelated to real time
        clock.SetMissionTimeProvider(fes);

        Assert.Multiple(() =>
        {
            Assert.That(clock.MissionNowUs, Is.EqualTo(9_000_000_000L), "mission time did not come from the installed provider");
            Assert.That(clock.Now().MissionTimeUs, Is.EqualTo(9_000_000_000L), "Now() mission did not come from the provider");
            Assert.That(clock.Now().SimTimeUs, Is.EqualTo(simAfter), "Now() sim time changed under a mission provider");
            Assert.That(clock.SimNowUs, Is.EqualTo(simAfter), "the mission provider affected sim time");
        });

        // A live provider is read on every access — a later value is reflected immediately.
        fes.MissionNowUs = 9_000_500_000L;
        Assert.That(clock.MissionNowUs, Is.EqualTo(9_000_500_000L), "an updated provider value was not observed");
    }

    [Test]
    public void Clearing_the_mission_time_provider_reverts_to_epoch_plus_horizon()
    {
        using var clock = new SimClock(Epoch);
        clock.RunFor(50_000);
        clock.SetMissionTimeProvider(new FixedMissionProvider(1L));
        Assert.That(clock.MissionNowUs, Is.EqualTo(1L));

        clock.SetMissionTimeProvider(null);   // revert to the framework default
        Assert.That(clock.MissionNowUs, Is.EqualTo(EpochUs + clock.SimNowUs),
            "clearing the provider did not revert to mission epoch + horizon");
    }

    [Test]
    public void PauseAtSimUs_arms_an_auto_pause_and_a_manual_Pause_disarms_it()
    {
        using var clock = new SimClock(Epoch);
        clock.Start();

        clock.PauseAtSimUs(clock.SimNowUs + 5_000_000);   // ~5 s away — won't be hit
        Thread.Sleep(20);
        clock.Pause();                                    // manual freeze before the target
        Assert.That(clock.IsRunning, Is.False);

        clock.Start();                                    // must not re-freeze (target disarmed)
        Thread.Sleep(60);
        Assert.That(clock.IsRunning, Is.True, "a manual Pause did not disarm the auto-pause target");
        clock.Pause();
    }

    [Test]
    public void RunFor_with_a_nonpositive_duration_pauses_immediately()
    {
        using var clock = new SimClock(Epoch);

        var sw = Stopwatch.StartNew();
        clock.RunFor(0);
        sw.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(clock.IsRunning, Is.False);
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(200), "a zero-duration RunFor blocked");
        });
    }

    [Test]
    public void Dispose_releases_a_blocked_RunFor()
    {
        var clock = new SimClock(Epoch);
        var runFor = Task.Run(() => clock.RunFor(10_000_000));   // 10 s — would block without Dispose

        Thread.Sleep(50);
        clock.Dispose();

        Assert.That(WaitUntil(() => runFor.IsCompleted, 3000), Is.True, "Dispose did not release the blocked RunFor");
    }

    [Test]
    public void Start_is_idempotent()
    {
        using var clock = new SimClock(Epoch);
        clock.Start();
        Assert.DoesNotThrow(() => clock.Start());
        Assert.That(clock.IsRunning, Is.True);
        clock.Pause();
    }
}
