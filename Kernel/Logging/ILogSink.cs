namespace SimulaSharp.Kernel.Logging;

/// <summary>
/// Destination for structured log records. The MMI implements this as an
/// asynchronous queue; the Kernel only produces <see cref="LogEntry"/> values and
/// hands them off. Implementations must be thread-safe: <see cref="Submit"/> is
/// called concurrently from every SimSite thread.
/// </summary>
public interface ILogSink
{
    /// <summary>
    /// Accepts a fully-formed log entry. Must not block the caller — enqueue and
    /// return. Passed by <c>in</c> to avoid copying the record struct.
    /// </summary>
    void Submit(in LogEntry entry);
}
