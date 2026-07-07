namespace SimulaSharp.Kernel.Primitives;

/// <summary>
/// A consistent capture of the clock's time, taken from a <b>single</b> read of the
/// horizon. Use this instead of reading sim time and mission time separately — two
/// separate reads can straddle a horizon update and disagree by a tick.
/// </summary>
/// <param name="SimTimeUs">Elapsed simulation time in microseconds.</param>
/// <param name="MissionTimeUs">Absolute mission time in microseconds (mission epoch + sim time).</param>
public readonly record struct TimeSnapshot(long SimTimeUs, long MissionTimeUs);
