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
/// The <c>Local\</c> namespace scopes both handles to the signed-in session, which matches a
/// per-user install: two people using the same machine each get their own instance, as they each
/// have their own settings and their own ledger.
/// </para>
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _wakeExisting;
    private readonly ManualResetEventSlim _stopping = new(false);

    private Thread? _listener;
    private bool _disposed;

    private SingleInstance(bool isFirst, Mutex? mutex, EventWaitHandle? wakeExisting)
    {
        IsFirst = isFirst;
        _mutex = mutex;
        _wakeExisting = wakeExisting;
    }

    /// <summary>True when this process is the only one running.</summary>
    public bool IsFirst { get; }

    /// <summary>
    /// Claims the instance for this process.
    /// </summary>
    /// <param name="name">Distinguishes the application, and lets tests use their own.</param>
    /// <remarks>
    /// A failure to create either handle reports <see cref="IsFirst"/> true. Refusing to start
    /// because the check itself could not be made would turn a locked-down machine into one that
    /// cannot transfer at all, which is far worse than the duplicate this guards against.
    /// </remarks>
    public static SingleInstance Acquire(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            var mutex = new Mutex(initiallyOwned: true, $@"Local\{name}.instance", out var isFirst);
            var wake = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.wake");

            return new SingleInstance(isFirst, mutex, wake);
        }
        catch (Exception)
        {
            // Includes UnauthorizedAccessException, seen when a handle of the same name exists
            // with an ACL this account cannot open.
            return new SingleInstance(isFirst: true, mutex: null, wakeExisting: null);
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

        if (IsFirst)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned, which is only reachable if Dispose runs on another thread. Nothing
                // to do about it, and nothing that depends on it.
            }
        }

        _mutex?.Dispose();
        _wakeExisting?.Dispose();
        _stopping.Dispose();
    }
}
