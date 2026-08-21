namespace PanoramaBridge.Core.Infrastructure;

/// <summary>
/// Ensures one PanoramaBridge per signed-in user, and lets a second launch reach the first.
/// </summary>
/// <remarks>
/// <para>
/// This became necessary the moment closing the window stopped exiting. Windows files a new
/// notification icon under hidden icons, so a user who closes the window sees nothing at all and
/// reasonably concludes it is not running -- and starts it again. Before that, a stray second
/// copy announced itself with a second window.
/// </para>
/// <para>
/// Two copies is not a cosmetic problem. They open the same SQLite ledger, walk the same folder,
/// and race PUTs of the same file to the same remote path, each unaware the other is uploading
/// it. That is the one thing this application is not allowed to get wrong.
/// </para>
/// <para>
/// Exclusion is a locked file in the per-user data directory, not a named mutex. The kernel's
/// <c>Local\</c> namespace is scoped to the terminal <em>session</em>, while the ledger under
/// <c>%LOCALAPPDATA%</c> is shared by the account across all of them -- so a mutex there would
/// have let the same user run two copies from two sessions, over one ledger, which is the exact
/// collision being prevented. A file lock matches the thing being protected, because it lives
/// beside it. It is also released by the operating system when the process dies, so a crash
/// cannot leave a stale lock that blocks every future start.
/// </para>
/// <para>
/// Waking the running copy stays session-scoped, and correctly so: a window belonging to another
/// session cannot be shown to whoever is looking at this one. Across sessions the second launch
/// simply exits.
/// </para>
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private readonly FileStream? _lock;
    private readonly EventWaitHandle? _wakeExisting;
    private readonly ManualResetEventSlim _stopping = new(false);

    private Thread? _listener;
    private bool _disposed;

    private SingleInstance(bool isFirst, FileStream? heldLock, EventWaitHandle? wakeExisting)
    {
        IsFirst = isFirst;
        _lock = heldLock;
        _wakeExisting = wakeExisting;
    }

    /// <summary>True when this process is the only one running.</summary>
    public bool IsFirst { get; }

    /// <summary>
    /// Claims the instance for this process.
    /// </summary>
    /// <param name="name">Names the wake handle, and lets tests use their own.</param>
    /// <param name="lockFile">
    /// A path in the per-user data directory. Held open for the life of the process.
    /// </param>
    /// <remarks>
    /// A failure to create either handle reports <see cref="IsFirst"/> true. Refusing to start
    /// because the check itself could not be made would turn a locked-down machine into one that
    /// cannot transfer at all, which is far worse than the duplicate this guards against.
    /// </remarks>
    public static SingleInstance Acquire(string name, string lockFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFile);

        EventWaitHandle? wake = null;

        try
        {
            wake = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.wake");
        }
        catch (Exception)
        {
            // Only costs the ability to raise the running window; exclusion does not depend on it.
        }

        try
        {
            var held = new FileStream(
                lockFile,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);

            return new SingleInstance(isFirst: true, held, wake);
        }
        catch (IOException)
        {
            // Another copy holds it. The one case this exists for.
            return new SingleInstance(isFirst: false, heldLock: null, wake);
        }
        catch (Exception)
        {
            // Anything else -- an unwritable directory, a policy denying the open -- reports
            // first. Refusing to start because the check could not be made would turn a
            // locked-down machine into one that cannot transfer at all, which is far worse than
            // the duplicate this guards against.
            return new SingleInstance(isFirst: true, heldLock: null, wake);
        }
    }

    /// <summary>
    /// Runs <paramref name="show"/> when another launch asks for the window. First instance only.
    /// </summary>
    /// <remarks>
    /// A background thread rather than a timer: it spends its life blocked on a wait handle and
    /// costs nothing, which is the standard this application is held to everywhere else.
    /// </remarks>
    public void ListenForSecondLaunch(Action show)
    {
        ArgumentNullException.ThrowIfNull(show);

        if (!IsFirst || _wakeExisting is null || _listener is not null || _disposed)
        {
            return;
        }

        _listener = new Thread(() =>
        {
            // Two handles, so shutdown is a signal rather than an abort: waking the thread to
            // find _stopping set is how it ends without anything being killed mid-call.
            var handles = new[] { _wakeExisting, _stopping.WaitHandle };

            while (!_disposed)
            {
                if (WaitHandle.WaitAny(handles) != 0)
                {
                    return;
                }

                show();
            }
        })
        {
            IsBackground = true,
            Name = "PanoramaBridge second-launch listener",
        };

        _listener.Start();
    }

    /// <summary>
    /// Asks the running instance to show itself.
    /// </summary>
    /// <returns>True when there was one to ask.</returns>
    public bool SignalExisting()
    {
        if (IsFirst || _wakeExisting is null)
        {
            return false;
        }

        try
        {
            return _wakeExisting.Set();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping.Set();

        // Joined briefly rather than not at all, so the handles below are not closed out from
        // under a wait. It is blocked on an event that has just been set, so this returns at once.
        _listener?.Join(TimeSpan.FromSeconds(1));

        try
        {
            // DeleteOnClose removes the file as the handle closes, so nothing is left behind.
            _lock?.Dispose();
        }
        catch (IOException)
        {
            // The lock is released by the handle closing either way.
        }

        _wakeExisting?.Dispose();
        _stopping.Dispose();
    }
}
