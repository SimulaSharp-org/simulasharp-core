using System.Runtime.CompilerServices;
using SimulaSharp.Kernel.Primitives;
using SimulaSharp.Kernel.Services;

namespace SimulaSharp.Kernel.Logging;

/// <summary>
/// The Kernel's default <see cref="ILogger"/>: scopes logging to one issuer and one
/// clock, stamps each message with a consistent time snapshot plus the wall clock,
/// and forwards the resulting <see cref="LogEntry"/> to an <see cref="ILogSink"/>
/// (the MMI's async queue). One instance is created per model/service.
/// </summary>
public sealed class ComponentLogger(string issuer, IClock clock, ILogSink sink) : ILogger
{
    /// <inheritdoc/>
    public void Log(
        LogLevel level,
        string message,
        [CallerMemberName] string? member = null,
        [CallerFilePath] string? file = null,
        [CallerLineNumber] int line = 0)
    {
        TimeSnapshot t = clock.Now();
        sink.Submit(new LogEntry(
            t.SimTimeUs, t.MissionTimeUs, DateTime.UtcNow,
            issuer, level, message, member, file, line));
    }
}
