using SimulaSharp.Kernel.Knobs;
using SimulaSharp.Kernel.Plotting;
using SimulaSharp.Kernel.Primitives;

namespace SimulaSharp.Kernel.Tests;

[TestFixture]
public sealed class SimYieldTests
{
    [Test]
    public void Immediate_is_a_zero_delay_non_awaited_yield()
    {
        var y = SimYield.Immediate;
        Assert.Multiple(() =>
        {
            Assert.That(y.DelayUs, Is.EqualTo(0));
            Assert.That(y.Awaited, Is.Null);
        });
    }

    [Test]
    public void Delay_carries_the_requested_microseconds()
    {
        var y = SimYield.Delay(1234);
        Assert.Multiple(() =>
        {
            Assert.That(y.DelayUs, Is.EqualTo(1234));
            Assert.That(y.Awaited, Is.Null);
        });
    }

    [Test]
    public void Await_carries_the_task_and_no_delay()
    {
        var task = Task.CompletedTask;
        var y = SimYield.Await(task);
        Assert.Multiple(() =>
        {
            Assert.That(y.Awaited, Is.SameAs(task));
            Assert.That(y.DelayUs, Is.EqualTo(0));
            Assert.That(y.Token, Is.Null, "an unbounded await has no token");
        });
    }

    [Test]
    public void Bounded_Await_carries_the_task_timeout_and_token()
    {
        var task = Task.CompletedTask;
        var tok = new SimAwaitToken();
        var y = SimYield.Await(task, timeoutUs: 5_000, tok);
        Assert.Multiple(() =>
        {
            Assert.That(y.Awaited, Is.SameAs(task));
            Assert.That(y.DelayUs, Is.EqualTo(0));
            Assert.That(y.TimeoutUs, Is.EqualTo(5_000));
            Assert.That(y.Token, Is.SameAs(tok));
        });
    }
}

[TestFixture]
public sealed class SimAwaitTokenTests
{
    [Test]
    public void A_fresh_token_is_unresolved()
    {
        var t = new SimAwaitToken();
        Assert.Multiple(() =>
        {
            Assert.That(t.TimedOut, Is.False);
            Assert.That(t.Completed, Is.False);
        });
    }

    [Test]
    public void TryResolve_is_single_shot_first_writer_wins()
    {
        var t = new SimAwaitToken();
        Assert.That(t.TryResolve(timedOut: true), Is.True, "the first resolve should win");
        Assert.That(t.TryResolve(timedOut: false), Is.False, "a second resolve must be ignored");
        Assert.Multiple(() =>
        {
            Assert.That(t.TimedOut, Is.True);
            Assert.That(t.Completed, Is.False, "the losing outcome must not stick");
        });
    }

    [Test]
    public void Resolving_completed_sets_Completed_not_TimedOut()
    {
        var t = new SimAwaitToken();
        Assert.That(t.TryResolve(timedOut: false), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(t.Completed, Is.True);
            Assert.That(t.TimedOut, Is.False);
        });
    }
}

[TestFixture]
public sealed class AssociationTests
{
    [Test]
    public void ContainerFactory_builds_a_frozen_indexable_container()
    {
        var a = new DummyModel();
        var b = new DummyModel();
        var factory = new ContainerFactory();
        factory.Add(a);
        factory.Add(b);
        var c = factory.Build<DummyModel>();

        Assert.Multiple(() =>
        {
            Assert.That(c.Count, Is.EqualTo(2));
            Assert.That(c[0], Is.SameAs(a));
            Assert.That(c[1], Is.SameAs(b));
            Assert.That(c.ToArray(), Is.EqualTo(new[] { a, b }));
        });
    }

    [Test]
    public void Container_Empty_is_empty() => Assert.That(Container<DummyModel>.Empty, Is.Empty);

    [Test]
    public void FillableReference_fills_and_reads_as_a_readonly_IReference()
    {
        var m1 = new DummyModel();
        var m2 = new DummyModel();
        var fillable = new FillableReference<DummyModelRef>();
        fillable.Add(new DummyModelRef(m1));
        fillable.Add(new DummyModelRef(m2));

        IReference<DummyModelRef> reference = fillable;   // the model holds only the read-only face

        Assert.Multiple(() =>
        {
            Assert.That(reference.Count, Is.EqualTo(2));
            Assert.That(reference[0].Model, Is.SameAs(m1));
            Assert.That(reference[1].Model, Is.SameAs(m2));
            Assert.That(reference.Select(r => r.Model), Is.EqualTo(new[] { m1, m2 }));
        });
    }

    [Test]
    public void An_unfilled_FillableReference_is_empty() =>
        Assert.That(new FillableReference<DummyModelRef>(), Is.Empty);
}

[TestFixture]
public sealed class KnobTests
{
    [Test]
    public void Read_and_Write_go_through_the_boxed_delegates()
    {
        int backing = 5;
        var k = new Knob
        {
            Path = "p", Label = "L", Kind = KnobKind.Number, ValueType = "int",
            ReadBoxed = () => backing,
            WriteBoxed = v => backing = (int)v!,
        };

        Assert.That(k.Read(), Is.EqualTo(5));
        k.Write(10);
        Assert.That(backing, Is.EqualTo(10));
    }

    [Test]
    public void RaiseChanged_notifies_subscribers()
    {
        var k = new Knob { Path = "p", Label = "L", Kind = KnobKind.Number };
        int fired = 0;
        k.Changed += () => fired++;

        k.RaiseChanged();
        Assert.That(fired, Is.EqualTo(1));
    }

    [Test]
    public void Trigger_invokes_the_bound_action()
    {
        bool fired = false;
        var k = new Knob { Path = "p", Label = "Go", Kind = KnobKind.Trigger, Invoke = () => fired = true };

        k.Trigger();
        Assert.That(fired, Is.True);
    }

    [Test]
    public void A_read_only_knob_ignores_writes()
    {
        int backing = 7;
        var k = new Knob
        {
            Path = "p", Label = "L", Kind = KnobKind.Number, ValueType = "int", IsReadOnly = true,
            ReadBoxed = () => backing,   // no WriteBoxed
        };

        Assert.DoesNotThrow(() => k.Write(99));
        Assert.That(backing, Is.EqualTo(7), "a read-only knob wrote a value");
    }
}

[TestFixture]
public sealed class PlottableTests
{
    [Test]
    public void Sample_returns_the_current_projected_value()
    {
        double v = 1.0;
        var p = new Plottable
        {
            Path = "p", Label = "L", Unit = "W", ValueType = "double",
            Sample = () => v,
        };

        Assert.That(p.Sample(), Is.EqualTo(1.0));
        v = 42.5;
        Assert.That(p.Sample(), Is.EqualTo(42.5), "Sample did not reflect the updated value");
    }
}
