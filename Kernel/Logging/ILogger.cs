using System.Runtime.CompilerServices;

namespace SimulaSharp.Kernel.Logging;

/// <summary>
/// A component-scoped logging facade. An instance is bound to a single issuer and
/// clock, so callers just supply a level and message — the issuer, the three
/// timestamps, and the call site are filled in automatically. The convenience
/// methods are default implementations, so a custom logger only needs
/// <see cref="Log"/>.
/// </summary>
public interface ILogger
{
    /// <summary>
    /// Records a message at <paramref name="level"/>. Call-site parameters are
    /// captured by the compiler; do not pass them explicitly.
    /// </summary>
    void Log(
        LogLevel level,
        string message,
        [CallerMemberName] string? member = null,
        [CallerFilePath] string? file = null,
        [CallerLineNumber] int line = 0);

    /// <summary>Logs at <see cref="LogLevel.Error"/>.</summary>
    void LogError(string message, [CallerMemberName] string? member = null, [CallerFilePath] string? file = null, [CallerLineNumber] int line = 0)
        => Log(LogLevel.Error, message, member, file, line);

    /// <summary>Logs at <see cref="LogLevel.Warning"/>.</summary>
    void LogWarning(string message, [CallerMemberName] string? member = null, [CallerFilePath] string? file = null, [CallerLineNumber] int line = 0)
        => Log(LogLevel.Warning, message, member, file, line);

    /// <summary>Logs at <see cref="LogLevel.Event"/>.</summary>
    void LogEvent(string message, [CallerMemberName] string? member = null, [CallerFilePath] string? file = null, [CallerLineNumber] int line = 0)
        => Log(LogLevel.Event, message, member, file, line);

    /// <summary>Logs at <see cref="LogLevel.Information"/>.</summary>
    void LogInformation(string message, [CallerMemberName] string? member = null, [CallerFilePath] string? file = null, [CallerLineNumber] int line = 0)
        => Log(LogLevel.Information, message, member, file, line);

    /// <summary>Logs at <see cref="LogLevel.Debug"/> (trace level 0).</summary>
    void LogDebug(string message, [CallerMemberName] string? member = null, [CallerFilePath] string? file = null, [CallerLineNumber] int line = 0)
        => Log(LogLevel.Debug, message, member, file, line);

    /// <summary>
    /// Logs at trace <paramref name="depth"/> 1..6 (<see cref="LogLevel.Trace1"/>..<see cref="LogLevel.Trace6"/>).
    /// For trace 0, use <see cref="LogDebug"/>.
    /// </summary>
    void LogTrace(int depth, string message, [CallerMemberName] string? member = null, [CallerFilePath] string? file = null, [CallerLineNumber] int line = 0)
        => Log(depth is >= 1 and <= 6
                ? LogLevel.Trace1 + (depth - 1)
                : throw new ArgumentOutOfRangeException(nameof(depth), depth, "Trace depth must be 1..6 (use LogDebug for 0)."),
            message, member, file, line);
}
