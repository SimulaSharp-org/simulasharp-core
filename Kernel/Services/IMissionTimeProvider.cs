namespace SimulaSharp.Kernel.Services;

/// <summary>
/// A pluggable provider of <b>mission time</b> for a clock. Installed via
/// <see cref="IClock.SetMissionTimeProvider"/>; once set, the clock's <see cref="IClock.MissionNowUs"/> and the
/// mission field of <see cref="IClock.Now"/> report what it returns, instead of the framework default (mission
/// epoch + horizon). This lets an authority inside the simulation — e.g. an FES service whose mission clock is
/// fast-forwarded between ground-station passes — drive the mission time every model and service reads, so
/// uptime / RTC values track that clock rather than real (sim) time.
/// <para>
/// <see cref="MissionNowUs"/> is read <b>cross-thread</b>, on every mission-time read (any site's models, plus the
/// plot sweep), so an implementation must be cheap, non-blocking, and thread-safe — typically a single
/// <c>Volatile.Read</c> of a value the authority keeps up to date (optionally interpolated from
/// <see cref="IClock.SimNowUs"/>). The provider may be the authority itself, or a small holder the authority updates.
/// </para>
/// </summary>
public interface IMissionTimeProvider
{
    /// <summary>The current absolute mission time in microseconds. Called on any thread; must be non-blocking.</summary>
    long MissionNowUs { get; }
}
