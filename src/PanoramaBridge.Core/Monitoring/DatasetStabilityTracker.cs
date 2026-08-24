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
/// <para>
/// Nothing here holds the measurement a folder settled at. One instance tracks every folder at
/// once, so a property carrying "the last one to settle" belongs to whichever call finished most
/// recently -- and a caller reading it after checking folder A would get folder B's file count
/// and timestamps, with nothing to indicate the swap. There was such a property; it was removed
/// rather than locked, because a correct one still answers a question the caller did not ask.
/// The measurement a caller can rely on is the one in the returned <see cref="FileReadiness"/>,
/// which describes the folder that was passed in and nothing else.
/// </para>
/// <para>
/// Anyone needing the file count or the newest write calls <see cref="DatasetFolder.Measure"/>
/// and walks the folder again. That second walk is not waste waiting to be optimised away, and
/// it should not be replaced by handing this one's stamp forward: <c>TransferCoordinator</c>
/// takes its measurement at pack time because the number it needs is the one true then. A folder
/// can sit between the gate and the worker for minutes and go on changing, and what it measures
/// there becomes the ledger row and the baseline that decides whether the upload was superseded.
/// A readiness-time stamp carried forward would describe neither what was packed nor what is on
/// the server. What would have to move is the measurement's moment, not where it is stored.
/// </para>
/// <para>
/// On threading: the dictionary makes concurrent checks of <em>different</em> folders safe, which
/// is what the sweep does. Overlapping calls for the <em>same</em> folder are not supported --
/// the sample is read, rewritten and removed as separate operations, so two of them could both
/// see a settled folder and both report it ready. <see cref="ReadinessGate"/> drives this from a
/// single loop, which is what keeps that from arising.
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

        // Keyed on the path with any trailing separator removed, so one folder cannot end up
        // with two clocks running under two spellings -- and so Forget with either spelling
        // reaches the sample. DatasetFolder.ArchiveNameFor trims for the same reason.
        path = Key(path);

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
            // Created but not yet written into. Keep watching rather than archiving nothing --
            // but for a bounded time.
            //
            // The clock used to restart on every look, which made quietFor permanently zero and
            // gave this state no way to end. An acquisition abandoned before anything was
            // written into it, or emptied after the sweep had already offered it, was re-walked
            // on the instrument's own disk every pass for the life of the process. Nothing
            // upstream could stop it either: Settling clears the gate's give-up counter, and
            // only Locked has a path past it.
            //
            // So the sample is kept when the folder is still empty, and once it has been empty
            // for the quiet period it is given up on. Nothing is lost by that: the sweep never
            // offers an empty acquisition, so if one is written into later, the sweep is what
            // brings it back.
            var seen = _samples.TryGetValue(path, out var first) && first.Stamp == current
                ? first
                : new Sample(current, now);

            _samples[path] = seen;

            var emptyFor = now - seen.SettledAt;

            if (emptyFor < _quietPeriod)
            {
                return FileReadiness.Settling(0, emptyFor, _quietPeriod);
            }

            _samples.TryRemove(path, out _);
            return FileReadiness.Empty(path);
        }

        var previous = _samples.TryGetValue(path, out var sample) ? sample : (Sample?)null;

        if (previous is null || previous.Value.Stamp != current)
        {
            // First sighting, or something inside changed. Either way the clock starts now.
            _samples[path] = new Sample(current, now);

            return previous is null
                ? FileReadiness.Settling(current.TotalBytes, TimeSpan.Zero, _quietPeriod)
                : FileReadiness.DatasetGrowing(previous.Value.Stamp, current);
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

        return FileReadiness.Ready(current.TotalBytes);
    }

    /// <summary>Stops watching a folder, for example once it has been queued.</summary>
    public void Forget(string path) => _samples.TryRemove(Key(path), out _);

    /// <summary>
    /// The dictionary key for a folder: its path without a trailing separator.
    /// </summary>
    /// <remarks>
    /// Only the separator is normalised, not the whole path. Both entry points already hand over
    /// a <see cref="FileSystemInfo.FullName"/>, so casing and relative segments do not arise in
    /// practice, and calling <see cref="Path.GetFullPath(string)"/> on every folder on every pass
    /// would be paying on the instrument for a problem nothing has produced. A trailing separator
    /// is the one variation that does occur, which is why ArchiveNameFor already handles it.
    /// </remarks>
    private static string Key(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Forgets everything.</summary>
    public void Clear() => _samples.Clear();

    private readonly record struct Sample(DatasetStamp Stamp, DateTimeOffset SettledAt);
}
