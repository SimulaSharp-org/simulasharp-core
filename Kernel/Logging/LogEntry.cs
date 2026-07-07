namespace SimulaSharp.Kernel.Logging;

/// <summary>
/// A structured log record. Timestamps are captured together so they are mutually
/// consistent: <see cref="SimTimeUs"/> and <see cref="MissionTimeUs"/> come from a
/// single clock snapshot, and <see cref="WallTimeUtc"/> is the real-world clock at
/// the moment of logging.
/// </summary>
/// <param name="SimTimeUs">Elapsed simulation time in microseconds.</param>
/// <param name="MissionTimeUs">Absolute mission time in microseconds.</param>
/// <param name="WallTimeUtc">Real-world UTC time when the entry was created.</param>
/// <param name="Issuer">Identity of the component that logged the entry.</param>
/// <param name="Level">Severity.</param>
/// <param name="Message">The log message.</param>
/// <param name="Member">Calling member name (auto-captured).</param>
/// <param name="File">Calling source file path (auto-captured).</param>
/// <param name="Line">Calling source line number (auto-captured).</param>
public readonly record struct LogEntry(
    long SimTimeUs,
    long MissionTimeUs,
    DateTime WallTimeUtc,
    string Issuer,
    LogLevel Level,
    string Message,
    string? Member,
    string? File,
    int Line);
