namespace SimulaSharp.Kernel.Logging;

/// <summary>
/// A composite <see cref="ILogSink"/> that owns the real sinks and fans each entry out
/// to all of them. A simulation holds one router (created at construction); registering
/// a sink <see cref="Add"/>s it here, and every component logger forwards to this single
/// router — so one registration reaches all logs. The sink list is copy-on-write:
/// fan-out reads are lock-free, registration is locked, and <see cref="Add"/> is safe
/// from any thread at any time (including while the simulation runs).
/// </summary>
public sealed class LogRouter : ILogSink
{
    private readonly object _lock = new();
    private volatile ILogSink[] _sinks = [];

    /// <summary>Registers a sink. Safe to call at any time, from any thread.</summary>
    public void Add(ILogSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_lock)
        {
            var updated = new ILogSink[_sinks.Length + 1];
            Array.Copy(_sinks, updated, _sinks.Length);
            updated[_sinks.Length] = sink;
            _sinks = updated;   // volatile publish
        }
    }

    /// <inheritdoc/>
    public void Submit(in LogEntry entry)
    {
        var sinks = _sinks;   // volatile snapshot
        for (int i = 0; i < sinks.Length; i++)
            sinks[i].Submit(entry);
    }
}
