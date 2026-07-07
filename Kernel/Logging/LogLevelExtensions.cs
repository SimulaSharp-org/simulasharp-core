namespace SimulaSharp.Kernel.Logging;

/// <summary>Helpers for <see cref="LogLevel"/>.</summary>
public static class LogLevelExtensions
{
    /// <summary>The canonical three-letter display code for a level (e.g. <c>ERR</c>, <c>TR3</c>).</summary>
    public static string ToCode(this LogLevel level) => level switch
    {
        LogLevel.Error => "ERR",
        LogLevel.Warning => "WRN",
        LogLevel.Event => "EVT",
        LogLevel.Information => "INF",
        LogLevel.Debug => "DBG",
        LogLevel.Trace1 => "TR1",
        LogLevel.Trace2 => "TR2",
        LogLevel.Trace3 => "TR3",
        LogLevel.Trace4 => "TR4",
        LogLevel.Trace5 => "TR5",
        LogLevel.Trace6 => "TR6",
        _ => level.ToString()
    };
}
