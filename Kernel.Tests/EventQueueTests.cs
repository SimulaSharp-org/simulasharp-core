using SimulaSharp.Kernel.Primitives;
using SimulaSharp.Kernel.Services;

namespace SimulaSharp.Kernel.Tests;

/// <summary>
/// Drives <see cref="EventQueue"/> synchronously via <see cref="EventQueue.ProcessDue"/> with an
/// explicit "now" — no clock, no threads — so every timing behaviour is fully deterministic.
/// </summary>
[TestFixture]
public sealed class EventQueueTests
{
    [Test]
    public void OneShot_fires_once_when_due_and_not_before()
    {
        var q = new EventQueue(8);
        int n = 0;
        q.AddEvent(() => n++, dueTimeUs: 100);

        q.ProcessDue(50);
        Assert.That(n, Is.EqualTo(0), "fired before due");

        q.ProcessDue(100);
        Assert.That(n, Is.EqualTo(1), "did not fire at due");

        q.ProcessDue(1000);
        Assert.That(n, Is.EqualTo(1), "one-shot fired again");
    }

    [Test]
    public void Due_events_fire_in_due_time_order()
    {
        var q = new EventQueue(8);
        var order = new List<int>();
        q.AddEvent(() => order.Add(3), 300);
        q.AddEvent(() => order.Add(1), 100);
        q.AddEvent(() => order.Add(2), 200);

        q.ProcessDue(1000);
        Assert.That(order, Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void Work_scheduled_during_a_pass_runs_on_the_next_pass()
    {
        var q = new EventQueue(8);
        int inner = 0;
        q.AddEvent(() => q.AddEvent(() => inner++, dueTimeUs: 0), dueTimeUs: 0);

        q.ProcessDue(0);
        Assert.That(inner, Is.EqualTo(0), "re-scheduled work ran within the same pass");

        q.ProcessDue(0);
        Assert.That(inner, Is.EqualTo(1), "re-scheduled work did not run on the next pass");
    }

    [Test]
    public void Periodic_rearms_absolutely_and_replays_backlog_across_passes()
    {
        var q = new EventQueue(8);
        int n = 0;
        q.AddPeriodic(() => n++, dueTimeUs: 100, periodUs: 100);

        q.ProcessDue(100);                 // fires @100, re-arms to 200
        Assert.That(n, Is.EqualTo(1));

        // Stuck at now=500: each pass replays one missed period (absolute re-arm = burst/catch-up),
        // firing @200, @300, @400, @500 across successive passes, then stops.
        for (int i = 0; i < 6; i++) q.ProcessDue(500);
        Assert.That(n, Is.EqualTo(5), "absolute re-arm did not replay the backlog one-per-pass");
    }

    [Test]
    public void Periodic_keeping_up_fires_once_per_period()
    {
        var q = new EventQueue(8);
        int n = 0;
        q.AddPeriodic(() => n++, 100, 100);

        q.ProcessDue(100);
        q.ProcessDue(200);
        q.ProcessDue(300);
        Assert.That(n, Is.EqualTo(3));
    }

    [Test]
    public void RemoveEvent_cancels_a_pending_event()
    {
        var q = new EventQueue(8);
        int n = 0;
        int id = q.AddEvent(() => n++, 100);

        q.RemoveEvent(id);
        q.ProcessDue(1000);
        Assert.That(n, Is.EqualTo(0));
    }

    [Test]
    public void AddEvent_returns_minus_one_when_full_or_negative()
    {
        var q = new EventQueue(2);
        Assert.Multiple(() =>
        {
            Assert.That(q.AddEvent(() => { }, 10), Is.GreaterThanOrEqualTo(0));
            Assert.That(q.AddEvent(() => { }, 10), Is.GreaterThanOrEqualTo(0));
            Assert.That(q.AddEvent(() => { }, 10), Is.EqualTo(-1), "did not reject when full");
            Assert.That(new EventQueue(4).AddEvent(() => { }, -1), Is.EqualTo(-1), "did not reject a negative due");
        });
    }

    [Test]
    public void PeekNextDueTime_reports_the_earliest_due_or_minus_one_when_empty()
    {
        var q = new EventQueue(8);
        Assert.That(q.PeekNextDueTime(), Is.EqualTo(-1));

        q.AddEvent(() => { }, 300);
        q.AddEvent(() => { }, 100);
        Assert.That(q.PeekNextDueTime(), Is.EqualTo(100));
    }

    [Test]
    public void Coroutine_materializes_on_first_fire_and_reschedules_by_its_yielded_delay()
    {
        var q = new EventQueue(8);
        int step = 0;
        IEnumerator<SimYield> Body()
        {
            step = 1; yield return SimYield.Delay(50);
            step = 2; yield return SimYield.Immediate;
            step = 3;
        }
        q.AddCoroutine(Body, dueTimeUs: 0);

        q.ProcessDue(0);
        Assert.That(step, Is.EqualTo(1), "coroutine body did not run to its first yield");

        q.ProcessDue(50);
        Assert.That(step, Is.EqualTo(2), "did not resume after the yielded delay");

        q.ProcessDue(50);                  // Immediate re-armed at the same due; next pass resumes
        Assert.That(step, Is.EqualTo(3));

        q.ProcessDue(50);                  // completed: slot freed, nothing more to run
        Assert.That(q.PeekNextDueTime(), Is.EqualTo(-1));
    }

    [Test]
    public void Awaited_coroutine_parks_until_ReArm()
    {
        var q = new EventQueue(8);
        int captureSlot = -1, captureVersion = -1;
        q.OnAwait = (slot, version, _) => { captureSlot = slot; captureVersion = version; };

        var tcs = new TaskCompletionSource();
        int step = 0;
        IEnumerator<SimYield> Body()
        {
            step = 1; yield return SimYield.Await(tcs.Task);
            step = 2;
        }
        q.AddCoroutine(Body, 0);

        q.ProcessDue(0);
        Assert.Multiple(() =>
        {
            Assert.That(step, Is.EqualTo(1));
            Assert.That(captureSlot, Is.GreaterThanOrEqualTo(0), "OnAwait was not invoked");
            Assert.That(q.PeekNextDueTime(), Is.EqualTo(-1), "parked coroutine was still time-scheduled");
        });

        q.ReArm(captureSlot, captureVersion, nowUs: 10);
        q.ProcessDue(10);
        Assert.That(step, Is.EqualTo(2), "coroutine did not resume after ReArm");
    }

    [Test]
    public void A_throwing_entry_is_dropped_and_its_slot_reclaimed()
    {
        var q = new EventQueue(2);
        int ok = 0;
        q.AddEvent(() => throw new InvalidOperationException("boom"), 10);

        Assert.DoesNotThrow(() => q.ProcessDue(10), "exception escaped ProcessDue");

        // The slot was reclaimed (queue capacity 2 is available again).
        Assert.Multiple(() =>
        {
            Assert.That(q.AddEvent(() => ok++, 20), Is.GreaterThanOrEqualTo(0));
            Assert.That(q.AddEvent(() => ok++, 20), Is.GreaterThanOrEqualTo(0));
        });
        q.ProcessDue(20);
        Assert.That(ok, Is.EqualTo(2));
    }

    [Test]
    public void ReArm_after_a_slot_is_freed_does_not_reschedule()
    {
        var q = new EventQueue(1);
        // Occupy and free the only slot via a completed one-shot (Version bumps on free).
        int slot = q.AddEvent(() => { }, 0);
        q.ProcessDue(0);                   // fires and frees the slot

        // A late ReArm against the freed slot (guarded by the version bump) must not resurrect it.
        q.ReArm(slot, version: 0, nowUs: 0);
        Assert.That(q.PeekNextDueTime(), Is.EqualTo(-1), "ReArm rescheduled a freed slot");
    }
}
