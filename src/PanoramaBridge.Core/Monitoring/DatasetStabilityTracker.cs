using System.Collections.Concurrent;

namespace PanoramaBridge.Core.Monitoring;

/// <summary>
/// Decides when a directory acquisition has finished being written.
/// </summary>
/// <remarks>
/// <para>
/// The folder counterpart of <see cref="FileStabilityTracker"/>, and it answers with the same
/// two independent signals, for the same reason: nothing may hold a file inside open, and the
/// folder must have stopped changing. Either alone is not enough. An instrument leaves its output
/// readable while still writing, so no lock does not mean finished; and a folder can hold still
/// for a moment between one file being closed and the next being created, so quiet does not mean
/// finished either.
/// </para>
/// <para>
/// A folder needs a third thing a file does not: it must not be empty. A <c>.d</c> that has been
/// created but not yet written to is quiet, unlocked, and complete-looking, and zipping it would
/// produce a valid archive of nothing.
/// </para>
/// </remarks>
public sealed class DatasetStabilityTracker
{
    private readonly TimeSpan _quietPeriod;
    private readonly Func<DateTimeOffset> _clock;

    private readonly ConcurrentDictionary<string, Sample> _samples =
        new(StringComparer.OrdinalIgnoreCase);

    public DatasetStabilityTracker(TimeSpan quietPeriod, Func<DateTimeOffset>? clock = null)
    {
        if (quietPeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(quietPeriod));
        }

        _quietPeriod = quietPeriod;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>How many folders are being watched.</summary>
    public int Count => _samples.Count;

    /// <summary>The measurement a ready folder settled at, so a caller need not take it again.</summary>
    public DatasetStamp? SettledAt { get; private set; }

    /// <summary>
    /// Examines a folder and reports whether it is ready to be archived.
    /// </summary>
    /// <remarks>
    /// Call repeatedly. The first call never returns ready, whatever the folder looks like: one
    /// observation cannot distinguish a finished acquisition from a pause between files.
    /// </remarks>
    public FileReadiness Check(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var now = _clock();
        var stamp = DatasetFolder.Measure(path);

        if (stamp is null)
        {
            _samples.TryRemove(path, out _);
            return FileReadiness.Missing(path);
        }

        var current = stamp.Value;

        if (current.IsEmpty)
        {
            // Created but not yet written into. Keep watching rather than archiving nothing.
            _samples[path] = new Sample(current, now);
            return FileReadiness.Settling(0, TimeSpan.Zero, _quietPeriod);
        }

        var previous = _samples.TryGetValue(path, out var sample) ? sample : (Sample?)null;

        if (previous is null || previous.Value.Stamp != current)
        {
            // First sighting, or something inside changed. Either way the clock starts now.
            _samples[path] = new Sample(current, now);

            return previous is null
                ? FileReadiness.Settling(current.TotalBytes, TimeSpan.Zero, _quietPeriod)
                : FileReadiness.Growing(previous.Value.Stamp.TotalBytes, current.TotalBytes);
        }

        // Unchanged. Asked last, because it opens every file in the folder and there is no point
        // paying for that while the size is still moving.
        if (DatasetFolder.IsAnythingWriting(path))
        {
            return FileReadiness.Locked(current.TotalBytes, path);
        }

        var quietFor = now - sample.SettledAt;
        if (quietFor < _quietPeriod)
        {
            return FileReadiness.Settling(current.TotalBytes, quietFor, _quietPeriod);
        }

        _samples.TryRemove(path, out _);
        SettledAt = current;

        return FileReadiness.Ready(current.TotalBytes);
    }

    /// <summary>Stops watching a folder, for example once it has been queued.</summary>
    public void Forget(string path) => _samples.TryRemove(path, out _);

    /// <summary>Forgets everything.</summary>
    public void Clear() => _samples.Clear();

    private readonly record struct Sample(DatasetStamp Stamp, DateTimeOffset SettledAt);
}
