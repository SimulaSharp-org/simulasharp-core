using SimulaSharp.Kernel.Events;
using SimulaSharp.Kernel.Logging;
using SimulaSharp.Kernel.Model;
using SimulaSharp.Kernel.Primitives;
using SimulaSharp.Kernel.Services;

namespace SimulaSharp.Kernel.Tests;

/// <summary>The stock <see cref="SimEvent"/> / <see cref="SimEvent{T}"/> fan-out: a trigger posts every subscriber's
/// handler onto that subscriber's own scheduler, with the payload for the typed variant.</summary>
[TestFixture]
public sealed class SimEventTests
{
    /// <summary>An <see cref="IScheduler"/> that runs posted work inline and counts how many posts it received.</summary>
    private sealed class RecordingScheduler : IScheduler
    {
        public int Posts { get; private set; }
        public void Post(Action action) { Posts++; action(); }
        public int AddEvent(Action action, long delayUs) => 0;
        public int AddPeriodicEvent(Action action, long periodUs) => 0;
        public int AddCoroutine(Func<IEnumerator<SimYield>> factory, long delayUs) => 0;
        public int AddEventAtMission(Action action, long missionTimeUs) => 0;
        public int AddPeriodicEventAtMission(Action action, long missionTimeUs, long periodUs) => 0;
        public int AddCoroutineAtMission(Func<IEnumerator<SimYield>> factory, long missionTimeUs) => 0;
        public void RemoveEvent(int id) { }
        public Task InjectVoidAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InjectAsync<T>(Func<T> action) => Task.FromResult(action());
        public void Dispose() { }
    }

    /// <summary>A minimal model carrying a given scheduler (its own site).</summary>
    private sealed class SchedModel(IScheduler scheduler) : IModel
    {
        public IClock TimeKeeper => null!;
        public IScheduler Scheduler { get; } = scheduler;
        public ILogger Logger => null!;
        public void Configure() { }
        public void Dispose() { }
    }

    [Test]
    public void Trigger_runs_every_subscribers_handler_on_its_own_scheduler()
    {
        var schedA = new RecordingScheduler();
        var schedB = new RecordingScheduler();
        int a = 0, b = 0;
        var evt = new SimEvent();
        evt.Register(new SchedModel(schedA), schedA, () => a++);
        evt.Register(new SchedModel(schedB), schedB, () => b++);

        ((ISimEventInvocator)evt).Trigger();

        Assert.Multiple(() =>
        {
            Assert.That(a, Is.EqualTo(1), "subscriber A's handler ran");
            Assert.That(b, Is.EqualTo(1), "subscriber B's handler ran");
            Assert.That(schedA.Posts, Is.EqualTo(1), "A's handler was posted onto A's own scheduler");
            Assert.That(schedB.Posts, Is.EqualTo(1), "B's handler was posted onto B's own scheduler");
        });
    }

    [Test]
    public void Trigger_fires_the_handlers_again_each_time()
    {
        var sched = new RecordingScheduler();
        int n = 0;
        var evt = new SimEvent();
        evt.Register(new SchedModel(sched), sched, () => n++);

        ((ISimEventInvocator)evt).Trigger();
        ((ISimEventInvocator)evt).Trigger();

        Assert.That(n, Is.EqualTo(2));
    }

    [Test]
    public void A_typed_event_delivers_the_payload_to_each_subscriber()
    {
        var sched = new RecordingScheduler();
        double got1 = 0, got2 = 0;
        var evt = new SimEvent<double>();
        evt.Register(new SchedModel(sched), sched, v => got1 = v);
        evt.Register(new SchedModel(sched), sched, v => got2 = v);

        ((ISimEventInvocator<double>)evt).Trigger(42.5);

        Assert.Multiple(() =>
        {
            Assert.That(got1, Is.EqualTo(42.5));
            Assert.That(got2, Is.EqualTo(42.5));
        });
    }

    [Test]
    public void An_event_with_no_subscribers_triggers_without_error()
    {
        Assert.DoesNotThrow(() => ((ISimEventInvocator)new SimEvent()).Trigger());
        Assert.DoesNotThrow(() => ((ISimEventInvocator<int>)new SimEvent<int>()).Trigger(7));
    }
}
