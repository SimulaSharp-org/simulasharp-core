using System.Diagnostics;

namespace SimulaSharp.Kernel.Tests;

/// <summary>
/// Shared helpers for the real-time kernel tests. Time-dependent tests run against the
/// real <see cref="Services.SimClock"/> (there is no manual clock by design), so an
/// off-thread effect is observed with a bounded spin rather than a fixed sleep.
/// </summary>
internal static class TestSupport
{
    /// <summary>
    /// Spins until <paramref name="condition"/> holds or <paramref name="timeoutMs"/> elapses.
    /// Returns the final value so a failure fails the assertion instead of hanging.
    /// </summary>
    public static bool WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(2);
        }
        return condition();
    }

    /// <summary>
    /// Asserts a measured wall-clock duration is within <paramref name="tolMs"/> of
    /// <paramref name="expectedMs"/>. Tolerance is deliberately generous: these tests share the
    /// machine with the build/other tests, so real-time waits can run long under load — we only
    /// assert the clock tracks real time to roughly 1:1, not to the microsecond.
    /// </summary>
    public static void AssertElapsed(long actualMs, long expectedMs, long tolMs)
        => Assert.That(actualMs, Is.InRange(expectedMs - tolMs, expectedMs + tolMs),
            $"expected ~{expectedMs}ms of wall time (±{tolMs}), measured {actualMs}ms");
}
