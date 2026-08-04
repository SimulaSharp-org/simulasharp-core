using SimulaSharp.Kernel.Logging;
using SimulaSharp.Kernel.Primitives;

namespace SimulaSharp.Kernel.Services;

/// <summary>
/// The scheduling data structure owned by a single <see cref="SimSite"/>.
/// <para>
/// <b>Threading:</b> this type is <em>not</em> thread-safe by design. It is only
/// ever touched by its owning SimSite's thread — model code runs on that thread,
/// and cross-thread work is marshalled onto it via the site's inbox before it
/// reaches the queue. Keeping it lock-free is the whole point of the
/// "closely-bound models call each other directly" model.
/// </para>
/// <para>
/// <b>Storage:</b> a fixed-capacity <see cref="Slot"/> array with a
/// <see cref="Queue{Int32}"/> free-list, so steady-state scheduling is
/// allocation-free. A <see cref="PriorityQueue{Int32,Int64}"/> keyed by due-time
/// (microseconds) orders pending work. Slot indices double as event ids.
/// </para>
/// </summary>
public sealed class EventQueue
{
    /// <summary>
    /// One scheduled entry. Exactly one of <see cref="Factory"/>/<see cref="Coroutine"/>
    /// or <see cref="OneShot"/> is active; the others are null.
    /// </summary>
    private struct Slot
    {
        /// <summary>Absolute simulation time (µs) at which this entry is next due.</summary>
        public long DueTimeUs;

        /// <summary>
        /// Deferred coroutine constructor. Held until the entry first fires, then
        /// invoked to build <see cref="Coroutine"/>. Deferring construction means the
        /// coroutine's body doesn't start running until its scheduled time.
        /// </summary>
        public Func<IEnumerator<SimYield>>? Factory;

        /// <summary>The live coroutine once materialized from <see cref="Factory"/>.</summary>
        public IEnumerator<SimYield>? Coroutine;

        /// <summary>A one-shot or periodic action (null for coroutine entries).</summary>
        public Action? OneShot;

        /// <summary>Repeat interval (µs) for a periodic action; 0 for one-shot.</summary>
        public long PeriodUs;

        /// <summary>Bumped when the slot is freed; guards a stale <see cref="ReArm"/> against a reused slot.</summary>
        public int Version;
    }

    private readonly Slot[] _slots;
    private readonly Queue<int> _freeSlots;
    private readonly PriorityQueue<int, long> _pq;

    /// <summary>The owning site's error channel: a scheduled entry that throws is logged here and dropped. Shared with
    /// the <see cref="SimSite"/>. Null only for tests that construct a bare queue.</summary>
    private readonly ILogger? _logger;

    /// <summary>
    /// Reusable buffer for the slots due in the current <see cref="ProcessDue"/> pass.
    /// Snapshotting the due set before executing gives correct "next pass" semantics:
    /// work (re)scheduled during a pass — including zero-delay yields — runs on the
    /// <em>next</em> pass, not this one, so a tight coroutine can't starve the loop.
    /// </summary>
    private readonly List<int> _dueBatch;

    /// <summary>
    /// Invoked when a coroutine yields an unbounded <see cref="SimYield.Await(Task)"/>, with (slot, version, task).
    /// The owner (SimSite) must arrange to call <see cref="ReArm"/> on its own thread once the
    /// task completes; set it before running awaiting coroutines.
    /// </summary>
    public Action<int, int, Task>? OnAwait;

    /// <summary>
    /// Invoked when a coroutine yields a <b>bounded</b> <see cref="SimYield.Await(Task, long, SimAwaitToken)"/>, with
    /// (slot, version, task, timeoutUs, token). The owner (SimSite) resumes the coroutine via <see cref="ReArm"/> on
    /// its own thread when the task completes <em>or</em> the timeout elapses — whichever comes first — resolving the
    /// token to the matching outcome. Set it before running awaiting coroutines.
    /// </summary>
    public Action<int, int, Task, long, SimAwaitToken>? OnAwaitTimed;

    /// <summary>Creates a queue that can hold at most <paramref name="maxCapacity"/> pending entries.</summary>
    /// <param name="maxCapacity">Maximum pending entries.</param>
    /// <param name="logger">The owning site's error channel; a throwing entry is logged here instead of being swallowed.</param>
    public EventQueue(int maxCapacity, ILogger? logger = null)
    {
        _logger = logger;
        _slots = new Slot[maxCapacity];
        _freeSlots = new Queue<int>(maxCapacity);
        _pq = new PriorityQueue<int, long>(maxCapacity);
        _dueBatch = new List<int>(maxCapacity);

        for (int i = 0; i < maxCapacity; i++)
            _freeSlots.Enqueue(i);
    }

    /// <summary>Schedules a one-shot action for <paramref name="dueTimeUs"/>.</summary>
    /// <returns>The event id (slot), or -1 if the time is negative or the queue is full.</returns>
    public int AddEvent(Action action, long dueTimeUs)
    {
        if (dueTimeUs < 0 || _freeSlots.Count == 0) return -1;

        int slot = _freeSlots.Dequeue();
        ref Slot s = ref _slots[slot];
        s.OneShot = action;
        s.Coroutine = null;
        s.Factory = null;
        s.PeriodUs = 0;
        s.DueTimeUs = dueTimeUs;
        _pq.Enqueue(slot, dueTimeUs);
        return slot;
    }

    /// <summary>
    /// Schedules a repeating action: first firing at <paramref name="dueTimeUs"/>,
    /// then every <paramref name="periodUs"/> thereafter.
    /// </summary>
    /// <returns>The event id (slot), or -1 on invalid arguments or a full queue.</returns>
    public int AddPeriodic(Action action, long dueTimeUs, long periodUs)
    {
        if (dueTimeUs < 0 || periodUs <= 0 || _freeSlots.Count == 0) return -1;

        int slot = _freeSlots.Dequeue();
        ref Slot s = ref _slots[slot];
        s.OneShot = action;
        s.Coroutine = null;
        s.Factory = null;
        s.PeriodUs = periodUs;
        s.DueTimeUs = dueTimeUs;
        _pq.Enqueue(slot, dueTimeUs);
        return slot;
    }

    /// <summary>
    /// Schedules a coroutine "process". The <paramref name="factory"/> is invoked at
    /// <paramref name="dueTimeUs"/> (not now), and each subsequent step is re-scheduled
    /// by the delay the coroutine yields (<see cref="SimYield"/>).
    /// </summary>
    /// <returns>The event id (slot), or -1 if the time is negative or the queue is full.</returns>
    public int AddCoroutine(Func<IEnumerator<SimYield>> factory, long dueTimeUs)
    {
        if (dueTimeUs < 0 || _freeSlots.Count == 0) return -1;

        int slot = _freeSlots.Dequeue();
        ref Slot s = ref _slots[slot];
        s.Factory = factory;
        s.Coroutine = null;
        s.OneShot = null;
        s.PeriodUs = 0;
        s.DueTimeUs = dueTimeUs;
        _pq.Enqueue(slot, dueTimeUs);
        return slot;
    }

    /// <summary>
    /// Cancels a pending event by id. No effect if the id is unknown or the event has
    /// already fired (or is in the current due-batch, which cannot be cancelled mid-pass).
    /// </summary>
    public void RemoveEvent(int slot)
    {
        if (slot < 0 || slot >= _slots.Length) return;
        if (_pq.Remove(slot, out _, out _))
            FreeSlot(slot);
    }

    /// <summary>The due time (µs) of the earliest pending event, or -1 if the queue is empty.</summary>
    public long PeekNextDueTime() => _pq.Count == 0 ? -1 : _slots[_pq.Peek()].DueTimeUs;

    /// <summary>
    /// Fires every entry due at or before <paramref name="nowUs"/>.
    /// <para>
    /// Uses a two-phase snapshot: first the due slots are collected (in due-time
    /// order), then executed. Anything scheduled while executing — periodic
    /// re-arms, coroutine continuations, zero-delay yields, or brand-new events —
    /// lands back in the priority queue for a <em>later</em> pass, never this one.
    /// </para>
    /// </summary>
    public void ProcessDue(long nowUs)
    {
        // Phase 1 — snapshot the due set.
        while (_pq.Count > 0 && _slots[_pq.Peek()].DueTimeUs <= nowUs)
            _dueBatch.Add(_pq.Dequeue());

        // Phase 2 — execute. Each entry either re-enqueues itself or frees its slot.
        foreach (int slot in _dueBatch)
            Fire(slot);

        _dueBatch.Clear();
    }

    /// <summary>Executes one due entry, with exception isolation and guaranteed cleanup.</summary>
    private void Fire(int slot)
    {
        ref Slot s = ref _slots[slot];
        try
        {
            // Materialize a coroutine on its first firing.
            if (s.Factory is not null)
            {
                s.Coroutine = s.Factory();
                s.Factory = null;
            }

            if (s.Coroutine is not null)
            {
                if (s.Coroutine.MoveNext())
                {
                    var y = s.Coroutine.Current;
                    if (y.Awaited is { } awaited)
                    {
                        // Park: keep the slot, don't time-schedule; resume via ReArm when the task completes — or,
                        // for a bounded await (a token is present), when the timeout elapses first. The owner arms
                        // both triggers and the token's single-shot resolve picks the winner.
                        if (y.Token is { } token)
                            OnAwaitTimed?.Invoke(slot, s.Version, awaited, y.TimeoutUs, token);
                        else
                            OnAwait?.Invoke(slot, s.Version, awaited);
                    }
                    else
                    {
                        // Re-schedule for the yielded delay (0 => next pass).
                        s.DueTimeUs += y.DelayUs;
                        _pq.Enqueue(slot, s.DueTimeUs);
                    }
                }
                else
                {
                    s.Coroutine.Dispose();
                    FreeSlot(slot);
                }
                return;
            }

            if (s.OneShot is not null)
            {
                s.OneShot();
                if (s.PeriodUs > 0)
                {
                    s.DueTimeUs += s.PeriodUs;
                    _pq.Enqueue(slot, s.DueTimeUs);
                }
                else
                {
                    FreeSlot(slot);
                }
                return;
            }

            // Nothing active (should not happen) — reclaim the slot defensively.
            FreeSlot(slot);
        }
        catch (Exception ex)
        {
            // A failing entry is dropped, never retried; its coroutine is disposed so no iterator resources leak, and
            // the slot is always reclaimed. The failure is logged to the site's error channel rather than swallowed.
            _logger?.LogError($"a scheduled entry threw and was dropped: {ex}");
            _slots[slot].Coroutine?.Dispose();
            FreeSlot(slot);
        }
    }

    /// <summary>
    /// Re-arms a coroutine parked by <see cref="SimYield.Await(Task)"/> so it resumes at
    /// <paramref name="nowUs"/>. Must be called on the owning site's thread (via its inbox).
    /// No-ops if the slot was freed/reused since parking (guarded by <paramref name="version"/>).
    /// </summary>
    public void ReArm(int slot, int version, long nowUs)
    {
        if (slot < 0 || slot >= _slots.Length) return;
        ref Slot s = ref _slots[slot];
        if (s.Coroutine is null || s.Version != version) return;
        s.DueTimeUs = nowUs;
        _pq.Enqueue(slot, nowUs);
    }

    /// <summary>Clears a slot and returns it to the free-list for reuse.</summary>
    private void FreeSlot(int slot)
    {
        ref Slot s = ref _slots[slot];
        s.OneShot = null;
        s.Coroutine = null;
        s.Factory = null;
        s.PeriodUs = 0;
        s.DueTimeUs = -1;
        s.Version++;
        _freeSlots.Enqueue(slot);
    }
}
