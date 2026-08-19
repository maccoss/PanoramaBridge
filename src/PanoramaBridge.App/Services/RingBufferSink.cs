using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace PanoramaBridge.App.Services;

/// <summary>A single rendered log line held in memory for the Activity Log pane.</summary>
/// <param name="Timestamp">When the event was raised.</param>
/// <param name="Level">Severity, so the pane can filter and colour.</param>
/// <param name="Message">The rendered message, already redacted by the enricher.</param>
public readonly record struct LogLine(DateTimeOffset Timestamp, LogEventLevel Level, string Message);

/// <summary>
/// Bounded in-memory sink backing the Activity Log pane.
/// </summary>
/// <remarks>
/// Bounded on purpose: an unbounded UI log is a slow memory leak on a machine that uploads
/// for weeks without being restarted. Old lines age out; the file sink remains the record.
/// </remarks>
public sealed class RingBufferSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogLine> _lines = new();
    private readonly int _capacity;

    public RingBufferSink(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>Raised on the logging thread whenever a line is appended.</summary>
    public event Action<LogLine>? LineAppended;

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var line = new LogLine(
            logEvent.Timestamp,
            logEvent.Level,
            logEvent.RenderMessage());

        _lines.Enqueue(line);

        while (_lines.Count > _capacity && _lines.TryDequeue(out _))
        {
            // Drop the oldest lines until we are back within capacity.
        }

        LineAppended?.Invoke(line);
    }

    /// <summary>Snapshot of the buffered lines, oldest first.</summary>
    public IReadOnlyList<LogLine> Snapshot() => _lines.ToArray();
}
