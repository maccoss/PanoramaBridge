using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PanoramaBridge.Core.Monitoring;

/// <summary>Why a full sweep of the monitored tree has been asked for.</summary>
/// <param name="Reason">A sentence fit to show the user.</param>
/// <param name="Urgent">
/// True when events were definitely lost, so the sweep should not wait for its normal turn.
/// </param>
public readonly record struct SweepRequest(string Reason, bool Urgent);

/// <summary>
/// Turns file system notifications into a stream of paths worth looking at.
/// </summary>
/// <remarks>
/// <para>
/// An accelerator, never the mechanism. Notifications are dropped when the kernel buffer
/// overflows, are not delivered at all by some SMB servers, and are duplicated by others -- the
/// share this was measured against sent three for every new file. Everything here is therefore
/// allowed to fail silently: <see cref="ReconciliationScanner"/> is what guarantees a file is
/// found, and this only makes it happen sooner.
/// </para>
/// <para>
/// There is no timer. Duplicate suppression is a comparison made when an event arrives, not a
/// window that has to be waited out, so an idle monitor costs exactly nothing -- which is the
/// point on a machine whose real job is running a mass spectrometer.
/// </para>
/// </remarks>
public sealed class DirectoryMonitor : IDisposable
{
    /// <summary>
    /// Kernel buffer for pending notifications.
    /// </summary>
    /// <remarks>
    /// The maximum that can be used with a network path, and sixteen times the default. It buys
    /// time under a burst rather than removing the overflow case: <see cref="Error"/> is
    /// subscribed because the buffer can always be beaten, and the Python version -- which
    /// subscribed to nothing -- simply lost events with no sign that it had.
    /// </remarks>
    private const int BufferBytes = 64 * 1024;

    /// <summary>Entries kept before stale ones are cleared out.</summary>
    private const int SuppressionCapacity = 4096;

    private readonly string _root;
    private readonly bool _includeSubdirectories;
    private readonly CandidateFilter _filter;
    private readonly TimeSpan _window;
    private readonly ILogger<DirectoryMonitor> _log;

    /// <summary>When each path was last passed on, in UTC ticks.</summary>
    private readonly Dictionary<string, long> _lastReported = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _suppression = new();

    private FileSystemWatcher? _watcher;
    private bool _disposed;

    /// <param name="root">Directory to watch.</param>
    /// <param name="includeSubdirectories">Whether to watch the tree below it.</param>
    /// <param name="filter">Which files are worth reporting.</param>
    /// <param name="window">
    /// How long the same path is ignored after being reported. One second by default, which
    /// collapses the burst a single file arrival produces without delaying anything.
    /// </param>
    /// <param name="log">Where to record overflows and watch failures.</param>
    public DirectoryMonitor(
        string root,
        bool includeSubdirectories,
        CandidateFilter filter,
        TimeSpan? window = null,
        ILogger<DirectoryMonitor>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        _root = root;
        _includeSubdirectories = includeSubdirectories;
        _filter = filter ?? throw new ArgumentNullException(nameof(filter));
        _window = window ?? TimeSpan.FromSeconds(1);
        _log = log ?? NullLogger<DirectoryMonitor>.Instance;
    }

    /// <summary>Raised for a file that has appeared or changed. Fires on a thread pool thread.</summary>
    public event Action<string>? Changed;

    /// <summary>Raised when only a full walk can establish what was missed.</summary>
    public event Action<SweepRequest>? SweepRequested;

    /// <summary>Whether notifications are currently being delivered.</summary>
    public bool IsWatching => _watcher is { EnableRaisingEvents: true };

    /// <summary>
    /// Starts watching, and reports whether it worked.
    /// </summary>
    /// <remarks>
    /// A failure here is not fatal and is deliberately not thrown. Some SMB servers refuse to
    /// register a watch at all; on those the sweep carries the whole job, more slowly but just as
    /// completely, and the user should see a note rather than an error.
    /// </remarks>
    public bool Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Stop();

        try
        {
            var watcher = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = _includeSubdirectories,
                InternalBufferSize = BufferBytes,

                // Size and last-write catch a file being appended to; file name catches one
                // arriving. Attribute and security changes are deliberately not watched: they
                // are noise here and they consume the same buffer.
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            };

            watcher.Created += OnChanged;
            watcher.Changed += OnChanged;

            // Renames matter as much as creations. Instrument and copy software routinely write
            // to a working name and rename on completion, which arrives as a rename and not as a
            // creation; without this, every such file waits for the next sweep.
            watcher.Renamed += OnRenamed;

            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;

            _watcher = watcher;

            _log.LogInformation(
                "Watching {Root} for changes{Recursive}.",
                _root,
                _includeSubdirectories ? " and everything below it" : string.Empty);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.LogWarning(
                ex,
                "Could not watch {Root} for changes. The periodic folder check will still find "
                + "new files.",
                _root);

            _watcher = null;
            return false;
        }
    }

    /// <summary>Stops watching. Safe to call when not started.</summary>
    public void Stop()
    {
        var watcher = Interlocked.Exchange(ref _watcher, null);

        if (watcher is null)
        {
            return;
        }

        watcher.Created -= OnChanged;
        watcher.Changed -= OnChanged;
        watcher.Renamed -= OnRenamed;
        watcher.Error -= OnError;

        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Consider(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e) => Consider(e.FullPath);

    private void OnError(object sender, ErrorEventArgs e)
    {
        var error = e.GetException();

        if (error is InternalBufferOverflowException)
        {
            // Events were lost, and there is no way to learn which. The only honest response is
            // to walk the whole tree.
            _log.LogWarning(
                "Change notifications for {Root} overflowed and some were lost. Re-checking the "
                + "whole folder.",
                _root);

            Raise(new SweepRequest(
                "Changes arrived faster than they could be read, so the whole folder is being "
                + "re-checked.",
                Urgent: true));
        }
        else
        {
            _log.LogWarning(
                error,
                "Watching {Root} stopped unexpectedly. Re-checking the whole folder.",
                _root);

            Raise(new SweepRequest(
                "The folder watch stopped unexpectedly and is being restarted.",
                Urgent: true));
        }

        // The watch is dead either way -- an overflow leaves it unable to say what it missed,
        // and any other error has already torn it down. Rebuilding is what restores it; a
        // failure to rebuild is survivable, because the sweep just requested does the work.
        if (!_disposed)
        {
            Start();
        }
    }

    private void Consider(string path)
    {
        try
        {
            if (!_filter.Accepts(path))
            {
                return;
            }

            // Directories raise these events too, and a folder whose name happens to match the
            // filter would otherwise be handed to the readiness gate, which would open it, fail,
            // and tell the user it could not read their file. Folder acquisitions are a separate
            // kind of transfer item and will not arrive through here.
            if (!File.Exists(path))
            {
                return;
            }

            if (!ShouldReport(path))
            {
                return;
            }

            // "It is not picking up my files" is the support question this whole component
            // invites, and the answer is always one of: the watch was refused, the filter
            // rejected the name, or the notification never arrived. Only the last of those is
            // invisible without this line.
            _log.LogDebug("Noticed a change to {Path}.", path);

            Changed?.Invoke(path);
        }
        catch (Exception ex)
        {
            // An exception thrown from a watcher callback tears down the process, and losing a
            // notification is not worth that. The sweep will find the file.
            _log.LogWarning(ex, "A change notification for {Path} could not be handled.", path);
        }
    }

    /// <summary>
    /// Suppresses repeats of the same path inside the window.
    /// </summary>
    /// <remarks>
    /// The measured SMB share delivered three notifications for every new file, and a local file
    /// being appended to produces one per write. Passing all of them on would put the same path
    /// through the readiness gate over and over. The first one is enough: the gate keeps watching
    /// a file until it settles, so nothing is lost by discarding the rest.
    /// </remarks>
    private bool ShouldReport(string path)
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;

        lock (_suppression)
        {
            if (_lastReported.TryGetValue(path, out var previous)
                && now - previous < _window.Ticks)
            {
                return false;
            }

            _lastReported[path] = now;

            if (_lastReported.Count > SuppressionCapacity)
            {
                Prune(now);
            }

            return true;
        }
    }

    /// <summary>Drops entries too old to suppress anything, so the map cannot grow without end.</summary>
    private void Prune(long now)
    {
        var cutoff = _window.Ticks * 4;

        foreach (var stale in _lastReported
            .Where(entry => now - entry.Value > cutoff)
            .Select(entry => entry.Key)
            .ToArray())
        {
            _lastReported.Remove(stale);
        }
    }

    private void Raise(SweepRequest request)
    {
        try
        {
            SweepRequested?.Invoke(request);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A sweep request could not be delivered.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
