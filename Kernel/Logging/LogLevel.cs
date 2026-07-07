namespace SimulaSharp.Kernel.Logging;

/// <summary>
/// Severity of a <see cref="LogEntry"/>, declared most-important first. The trace
/// family is a depth scale: <see cref="Debug"/> is trace 0, then
/// <see cref="Trace1"/>..<see cref="Trace6"/> go progressively more verbose.
/// Three-letter display codes are available via <see cref="LogLevelExtensions.ToCode"/>.
/// </summary>
public enum LogLevel
{
    /// <summary>ERR — a failure.</summary>
    Error,

    /// <summary>WRN — something suspect but non-fatal.</summary>
    Warning,

    /// <summary>EVT — a notable simulation event.</summary>
    Event,

    /// <summary>INF — general information.</summary>
    Information,

    /// <summary>DBG — debug detail; conceptually trace level 0.</summary>
    Debug,

    /// <summary>TR1 — trace level 1.</summary>
    Trace1,

    /// <summary>TR2 — trace level 2.</summary>
    Trace2,

    /// <summary>TR3 — trace level 3.</summary>
    Trace3,

    /// <summary>TR4 — trace level 4.</summary>
    Trace4,

    /// <summary>TR5 — trace level 5.</summary>
    Trace5,

    /// <summary>TR6 — trace level 6.</summary>
    Trace6
}
