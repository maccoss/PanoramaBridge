using System.Collections.Concurrent;
using PanoramaBridge.Core.Storage;

namespace PanoramaBridge.Core.Transfer;

/// <summary>Aggregate figures across everything currently known.</summary>
/// <param name="Active">Files with bytes moving.</param>
/// <param name="Queued">Files accepted but not started.</param>
/// <param name="Finished">Files that reached a terminal state.</param>
/// <param name="NeedsAttention">Files failed, conflicted or superseded.</param>
/// <param name="BytesTransferred">Bytes sent for files still in flight.</param>
/// <param name="BytesTotal">Total bytes of the files still in flight.</param>
/// <param name="BytesPerSecond">Combined throughput of the active transfers.</param>
public readonly record struct TransferTotals(
    int Active,
    int Queued,
    int Finished,
    int NeedsAttention,
    long BytesTransferred,
    long BytesTotal,
    double BytesPerSecond)
{
    /// <summary>Overall completion of the work in flight, for the taskbar and status bar.</summary>
    public double? Fraction => BytesTotal > 0
        ? Math.Clamp((double)BytesTransferred / BytesTotal, 0, 1)
        : null;

    /// <summary>Estimated time until the in-flight work finishes.</summary>
    public TimeSpan? Eta => BytesPerSecond > 0 && BytesTotal > BytesTransferred
        ? TimeSpan.FromSeconds((BytesTotal - BytesTransferred) / BytesPerSecond)
        : null;

    /// <summary>The one-line summary shown in the status bar.</summary>
    public string Describe()
    {
        if (Active == 0 && Queued == 0)
        {
            return NeedsAttention > 0
                ? $"Idle - {NeedsAttention} need attention"
                : "Idle";
        }

        var parts = new List<string> { $"{Active} active" };

        if (Queued > 0)
        {
            parts.Add($"{Queued} queued");
        }

        if (BytesPerSecond > 0)
        {
            parts.Add($"{Format(BytesPerSecond)}/s");
        }

        if (Eta is { } eta)
        {
            parts.Add($"{DescribeEta(eta)} left");
        }

        return string.Join(" - ", parts);
    }

    private static string DescribeEta(TimeSpan eta) => eta.TotalHours >= 1
        ? $"{(int)eta.TotalHours}h {eta.Minutes}m"
        : eta.TotalMinutes >= 1
            ? $"{(int)eta.TotalMinutes}m"
            : $"{Math.Max(1, (int)eta.TotalSeconds)}s";

    private static string Format(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;

        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:F0} B" : $"{bytes:F1} {units[unit]}";
    }
}

/// <summary>
/// Collects progress from the transfer workers and hands the UI only what changed.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the UI is never asked to keep up with the engine. Three concurrent uploads
/// reporting at one-mebibyte granularity generate thousands of updates a second; binding
/// straight to that makes the window unusable, however efficient the individual update is.
/// </para>
/// <para>
/// Workers call <see cref="Report"/> from any thread, which does nothing but record the latest
/// state per file. The UI calls <see cref="DrainChanged"/> on a timer -- a few times a second --
/// and gets one entry per file that actually moved. Intermediate updates for the same file
/// collapse into the newest, so the cost of drawing is bounded by the number of files on screen
/// rather than by transfer speed.
/// </para>
/// <para>
/// Deliberately UI-framework-free so this behaviour can be tested without a dispatcher.
/// </para>
/// </remarks>
public sealed class TransferProgressAggregator
{
    private readonly ConcurrentDictionary<string, TransferProgress> _latest =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths whose state has changed since the last drain.</summary>
    private readonly ConcurrentDictionary<string, byte> _dirty =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raised when a report arrives and nothing was pending.
    /// </summary>
    /// <remarks>
    /// Lets the UI keep its refresh timer stopped while idle and start it only when there is
    /// something to draw. On an instrument computer this matters: a timer ticking several times a
    /// second forever keeps the processor out of its deep idle states for no benefit, and this
    /// application spends almost all of its life with nothing to show.
    /// </remarks>
    public event Action? WorkAppeared;

    /// <summary>True when a drain would return something.</summary>
    public bool HasPendingChanges => !_dirty.IsEmpty;

    /// <summary>Records the newest state of one transfer. Safe to call from any thread.</summary>
    public void Report(TransferProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var wasQuiet = _dirty.IsEmpty;

        // Latest wins. An older update arriving late cannot resurrect a finished row, because
        // the engine reports strictly in order per file.
        _latest[progress.LocalPath] = progress;
        _dirty[progress.LocalPath] = 0;

        if (wasQuiet)
        {
            WorkAppeared?.Invoke();
        }
    }

    /// <summary>Everything currently known, in no particular order.</summary>
    public IReadOnlyCollection<TransferProgress> Snapshot() => _latest.Values.ToArray();

    /// <summary>How many files are being tracked.</summary>
    public int Count => _latest.Count;

    /// <summary>
    /// Returns one entry per file that changed since the previous call, and clears the change
    /// set.
    /// </summary>
    public IReadOnlyList<TransferProgress> DrainChanged()
    {
        if (_dirty.IsEmpty)
        {
            return [];
        }

        var changed = new List<TransferProgress>(_dirty.Count);

        foreach (var path in _dirty.Keys)
        {
            // Remove first: a worker reporting again between here and the lookup marks the file
            // dirty once more, so the update is picked up next drain rather than lost.
            _dirty.TryRemove(path, out _);

            if (_latest.TryGetValue(path, out var progress))
            {
                changed.Add(progress);
            }
        }

        return changed;
    }

    /// <summary>Aggregate figures over everything known.</summary>
    public TransferTotals Totals()
    {
        var active = 0;
        var queued = 0;
        var finished = 0;
        var attention = 0;
        long transferred = 0;
        long total = 0;
        double rate = 0;

        foreach (var progress in _latest.Values)
        {
            switch (progress.State)
            {
                case TransferState.Uploading:
                    active++;
                    transferred += progress.BytesTransferred;
                    total += progress.TotalBytes;
                    rate += progress.BytesPerSecond;
                    break;

                case TransferState.Discovered:
                case TransferState.Queued:
                case TransferState.LockedRetrying:
                    queued++;
                    total += progress.TotalBytes;
                    break;

                case TransferState.Verified:
                case TransferState.Skipped:
                case TransferState.Uploaded:

                // Settled by a person choosing the copy on the server. Nothing is going to
                // happen to it, so counting it as needing attention would leave the status bar
                // asking for a decision that has already been made.
                case TransferState.Declined:
                    finished++;
                    break;

                case TransferState.Failed:
                case TransferState.Conflict:
                case TransferState.Superseded:
                    attention++;
                    break;
            }
        }

        return new TransferTotals(active, queued, finished, attention, transferred, total, rate);
    }

    /// <summary>
    /// Forgets files that have finished cleanly, keeping anything that still needs a decision.
    /// </summary>
    /// <returns>The paths that were removed.</returns>
    public IReadOnlyList<string> ClearFinished()
    {
        var removed = new List<string>();

        foreach (var (path, progress) in _latest)
        {
            if (progress.State is TransferState.Verified or TransferState.Skipped
                    or TransferState.Declined
                && _latest.TryRemove(path, out _))
            {
                _dirty.TryRemove(path, out _);
                removed.Add(path);
            }
        }

        return removed;
    }

    /// <summary>Forgets everything.</summary>
    /// <summary>Drops one path, as though it had never been reported.</summary>
    /// <remarks>
    /// For a row that is going to be reported again from the beginning. Restating it as queued
    /// instead would leave the UI counting work that has not started, and nothing removes a
    /// queued row: the totals would keep the refresh timer awake for the life of the process,
    /// which on an instrument computer is the one thing this application must not do.
    /// </remarks>
    public bool Forget(string localPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        _dirty.TryRemove(localPath, out _);
        return _latest.TryRemove(localPath, out _);
    }

    public void Clear()
    {
        _latest.Clear();
        _dirty.Clear();
    }
}
