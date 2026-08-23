using System.Collections.Concurrent;

namespace PanoramaBridge.Core.Monitoring;

/// <summary>
/// Decides when a file has stopped being written and can safely be uploaded.
/// </summary>
/// <remarks>
/// <para>
/// This is the most important correctness property in the application: uploading a partially
/// written acquisition is worse than not uploading it at all, because the copy looks complete
/// and will verify against its own truncated content.
/// </para>
/// <para>
/// Two independent signals are required, and neither is sufficient alone.
/// </para>
/// <para>
/// <b>An exclusive-open probe</b> answers "does anything else hold this file?". It catches an
/// instrument acquiring into a file and a copy still in progress. On its own it is not enough,
/// because a writer that opens, appends and closes for each block leaves gaps in which nothing
/// holds the file and it still is not finished.
/// </para>
/// <para>
/// <b>Size stability</b> answers "has it stopped growing?". On its own it is not enough either,
/// and for a subtle reason: while a file has an open write handle, Windows does not keep the
/// directory entry up to date, so <see cref="FileInfo.Length"/> can report a stale size that
/// does not change between samples. A file being actively written can therefore look perfectly
/// stable. The size here is read from an opened handle instead, which reflects the real end of
/// file, and the lock probe covers the case where even that is not conclusive.
/// </para>
/// <para>
/// A file is released only when nothing else holds it <em>and</em> its size has been unchanged
/// for the whole quiet period.
/// </para>
/// </remarks>
public sealed class FileStabilityTracker
{
    private readonly TimeSpan _quietPeriod;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, Sample> _samples =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The folder counterpart, for the directory acquisitions the sweep also offers.
    /// </summary>
    /// <remarks>
    /// Held here rather than beside it so that everything downstream -- the gate, the monitor,
    /// their tests -- keeps handing paths to one tracker and does not have to know which kind of
    /// thing it just received. The two answer the same question with the same vocabulary; only
    /// the way they measure differs.
    /// </remarks>
    private readonly DatasetStabilityTracker _datasets;

    public FileStabilityTracker(TimeSpan quietPeriod, Func<DateTimeOffset>? clock = null)
    {
        if (quietPeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(quietPeriod));
        }

        _quietPeriod = quietPeriod;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _datasets = new DatasetStabilityTracker(_quietPeriod, _clock);
    }

    /// <summary>How many files are being watched for stability.</summary>
    public int Count => _samples.Count;

    /// <summary>Paths currently being watched.</summary>
    public IReadOnlyCollection<string> Tracked => _samples.Keys.ToArray();

    /// <summary>
    /// Examines a file and reports whether it is ready.
    /// </summary>
    /// <remarks>
    /// Call repeatedly. The first call never returns ready, whatever the file looks like: a
    /// single observation cannot distinguish a finished file from one that happens to be between
    /// writes.
    /// </remarks>
    public FileReadiness Check(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // A directory here is an acquisition the sweep chose to offer whole -- a Bruker .d --
        // and none of what follows applies to one: there is no handle to open and no single
        // length to read.
        if (Directory.Exists(path))
        {
            return _datasets.Check(path);
        }

        var now = _clock();

        // Reading the length from an open handle rather than from the directory entry, which
        // Windows leaves stale while a write handle is open.
        var probe = Probe(path);

        if (probe.Reason is ReadinessReason.Missing or ReadinessReason.Unreadable)
        {
            // Stop tracking: neither state improves by asking again.
            _samples.TryRemove(path, out _);
            return probe;
        }

        var length = probe.Length;
        var previous = _samples.TryGetValue(path, out var sample) ? sample : (Sample?)null;

        if (previous is null || previous.Value.Length != length)
        {
            // First sighting, or it changed size. Either way the clock starts now.
            _samples[path] = new Sample(length, now);

            return previous is null
                ? FileReadiness.Settling(length, TimeSpan.Zero, _quietPeriod)
                : FileReadiness.Growing(previous.Value.Length, length);
        }

        // Size is unchanged. If anything still holds the file it is not finished, however long
        // it has looked quiet -- an instrument can hold a file open for an entire run without
        // flushing its size to the directory entry.
        if (probe.Reason == ReadinessReason.Locked)
        {
            return FileReadiness.Locked(length, path);
        }

        var quietFor = now - sample.FirstSeenUnchanged;
        if (quietFor < _quietPeriod)
        {
            return FileReadiness.Settling(length, quietFor, _quietPeriod);
        }

        _samples.TryRemove(path, out _);
        return FileReadiness.Ready(length);
    }

    /// <summary>Stops tracking a file, for example once it has been queued.</summary>
    public void Forget(string path)
    {
        _samples.TryRemove(path, out _);
        _datasets.Forget(path);
    }

    /// <summary>Forgets everything.</summary>
    public void Clear()
    {
        _samples.Clear();
        _datasets.Clear();
    }

    /// <summary>
    /// Opens the file to establish both its true length and whether anyone else holds it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two opens, in this order, because they answer different questions.
    /// </para>
    /// <para>
    /// The first requests <see cref="FileShare.None"/>, which fails if any other handle to the
    /// file exists. That is the "is an instrument or a copy using this?" test, and it is the only
    /// reliable one -- a file being acquired into is frequently still readable, so a plain read
    /// open would succeed and prove nothing.
    /// </para>
    /// <para>
    /// If that fails, the second opens shared so the length can still be read from the file
    /// object rather than the stale directory entry. Sharing includes
    /// <see cref="FileShare.Delete"/>, so the check never blocks a process that wants to replace
    /// or remove the file underneath it.
    /// </para>
    /// </remarks>
    private static FileReadiness Probe(string path)
    {
        try
        {
            using var exclusive = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);

            return FileReadiness.Ready(exclusive.Length);
        }
        catch (FileNotFoundException)
        {
            return FileReadiness.Missing(path);
        }
        catch (DirectoryNotFoundException)
        {
            return FileReadiness.Missing(path);
        }
        catch (IOException)
        {
            // Someone else holds it. Fall through and measure it without demanding exclusivity.
        }
        catch (UnauthorizedAccessException ex)
        {
            return FileReadiness.Unreadable(path, ex.Message);
        }

        try
        {
            using var shared = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            return FileReadiness.Locked(shared.Length, path);
        }
        catch (FileNotFoundException)
        {
            return FileReadiness.Missing(path);
        }
        catch (IOException)
        {
            // Held so exclusively that even a shared read is refused. Some instrument software
            // does this. The size is unknown, which is reported as zero and simply means the
            // stability clock cannot start yet.
            return FileReadiness.Locked(0, path);
        }
        catch (UnauthorizedAccessException ex)
        {
            return FileReadiness.Unreadable(path, ex.Message);
        }
    }

    private readonly record struct Sample(long Length, DateTimeOffset FirstSeenUnchanged);
}
