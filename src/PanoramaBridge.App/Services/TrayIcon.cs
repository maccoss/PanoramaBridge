using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace PanoramaBridge.App.Services;

/// <summary>
/// The notification-area icon, and the menu on it.
/// </summary>
/// <remarks>
/// <para>
/// <c>System.Windows.Forms.NotifyIcon</c> rather than <c>Shell_NotifyIcon</c> by hand. The
/// deciding factor is that Explorer restarting destroys every tray icon, and each application is
/// responsible for adding its own back when it does. NotifyIcon handles that; hand-rolled
/// interop usually does not, and the symptom -- a window that can no longer be reopened -- would
/// appear days into a run and be near-impossible to attribute.
/// </para>
/// <para>
/// Every failure here is non-fatal. An icon that cannot be shown is reported through
/// <see cref="IsAvailable"/>, which keeps the window closing normally, because an application
/// that cannot show its window is worse than one that does not sit in the tray.
/// </para>
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon? _icon;
    private readonly System.Windows.Forms.ContextMenuStrip? _menu;
    private readonly System.Drawing.Font? _defaultItemFont;
    private readonly ILogger<TrayIcon> _log;

    private bool _disposed;
    private bool _announced;

    public TrayIcon(ILogger<TrayIcon> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));

        try
        {
            _menu = new System.Windows.Forms.ContextMenuStrip();

            var open = _menu.Items.Add("Open PanoramaBridge");
            open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

            // Bold, so a double-click and the menu's default item visibly agree. Held in a field
            // because ToolStripItem does not own a font assigned to it, so nothing else would
            // ever release the handle.
            _defaultItemFont = new System.Drawing.Font(_menu.Font, System.Drawing.FontStyle.Bold);
            open.Font = _defaultItemFont;

            _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            var exit = _menu.Items.Add("Exit");
            exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

            _icon = new System.Windows.Forms.NotifyIcon
            {
                Icon = LoadIcon(),
                Text = Core.Infrastructure.AppInfo.ProductName,
                ContextMenuStrip = _menu,
                Visible = false,
            };

            // Both, because both are things people do to a tray icon and neither is discoverable.
            // A single left click doing nothing reads as a dead icon; the double click is the
            // documented gesture. Restoring twice is harmless -- showing a shown window is a
            // no-op -- so there is no need to suppress the click that precedes a double click.
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left)
                {
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                }
            };

            _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "The notification area icon could not be created.");

            // Nothing else holds these once the constructor gives up, so they would leak.
            _defaultItemFont?.Dispose();
            _menu?.Dispose();

            _icon = null;
            _menu = null;
            _defaultItemFont = null;
        }
    }

    /// <summary>Raised when the user asks for the window back.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>Raised when the user asks to exit from the tray menu.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>
    /// Whether there is an icon the user could actually click. False means never hide the window.
    /// </summary>
    /// <remarks>
    /// This deliberately asks the shell rather than reporting whether the constructor threw.
    /// Constructing a <c>NotifyIcon</c> touches nothing outside the process: the shell is not
    /// called until <see cref="Visible"/> is set, and <c>NotifyIcon</c> discards the result of
    /// <c>Shell_NotifyIcon</c> and cannot report having been refused. Reporting availability from
    /// the constructor therefore answered "did allocating an object succeed", which is always
    /// yes -- so the guard that stops the window being hidden with nothing to click could never
    /// once have fired. Asking whether a taskbar exists is the cheapest question that is actually
    /// about the notification area.
    /// </remarks>
    public bool IsAvailable => _icon is not null && !_disposed && NotificationAreaExists();

    /// <summary>Whether the icon is currently shown.</summary>
    public bool Visible
    {
        get => _icon?.Visible == true;
        set
        {
            if (_icon is null || _disposed)
            {
                return;
            }

            try
            {
                _icon.Visible = value;
            }
            catch (Exception ex)
            {
                // Setting this is the point at which the shell is involved at all, so it is the
                // first place a shell problem can surface.
                _log.LogWarning(ex, "The notification area icon could not be shown.");
            }
        }
    }

    /// <summary>Sets the hover text, truncated to what the shell accepts.</summary>
    public void SetTooltip(string text)
    {
        if (_icon is null || _disposed)
        {
            return;
        }

        _icon.Text = TrayPolicy.TruncateTooltip(text);
    }

    /// <summary>
    /// Says the application is still running, the first time the window is hidden.
    /// </summary>
    /// <param name="monitoring">Whether the folder is actually being watched.</param>
    /// <remarks>
    /// Windows puts a new icon in the hidden overflow by default, so without this the window
    /// simply vanishes and the obvious conclusion is that it exited -- at which point somebody
    /// starts it again and wonders why nothing is transferring. Shown once per run: repeating it
    /// on every close would be worse than not showing it at all.
    /// </remarks>
    public void AnnounceStillRunning(bool monitoring)
    {
        if (_announced)
        {
            return;
        }

        _announced = true;

        // "Still watching for new files" is true only when it is. Monitoring does not start on
        // its own by default, and closing the window is among the first things anyone does, so
        // saying it unconditionally would reassure exactly the person who most needs telling
        // that nothing is running.
        Notify(
            Core.Infrastructure.AppInfo.ProductName,
            monitoring
                ? "Still running and still watching for new files. Click this icon to reopen it."
                : "Still running, but not monitoring. Click this icon to reopen it and start.",
            warning: false);
    }

    /// <summary>
    /// Raises a notification from the icon.
    /// </summary>
    /// <remarks>
    /// The only way a hidden window can say anything at all. A failure reported solely into the
    /// status line is invisible for as long as the window stays closed, which on an instrument
    /// computer can be weeks.
    /// </remarks>
    public void Notify(string title, string message, bool warning)
    {
        if (_icon is null || _disposed || !_icon.Visible)
        {
            return;
        }

        try
        {
            _icon.ShowBalloonTip(
                warning ? 15000 : 5000,
                title,
                message,
                warning
                    ? System.Windows.Forms.ToolTipIcon.Warning
                    : System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            // Balloons can be suppressed by policy; that is not a failure worth surfacing.
            _log.LogDebug(ex, "The notification balloon could not be shown.");
        }
    }

    /// <summary>
    /// Removes the icon.
    /// </summary>
    /// <remarks>
    /// Safe to call more than once, and called from three places: the window when it closes, so
    /// the icon goes at once rather than lingering until the pointer next crosses it; the service
    /// container on the way out; and the process-exit handler, which is the only one of the three
    /// that runs when Velopack restarts the application for an update. The same double-dispose
    /// that once turned closing the application into a "could not start" dialog.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.Dispose();
        }

        _menu?.Dispose();
        _defaultItemFont?.Dispose();
    }

    /// <summary>
    /// Whether the shell is running a taskbar to put an icon in.
    /// </summary>
    /// <remarks>
    /// Absent on a Server Core installation, and briefly absent while Explorer restarts. The
    /// transient case is harmless: it makes closing the window close the application for as long
    /// as it lasts, which is the safe direction to be wrong in.
    /// </remarks>
    private static bool NotificationAreaExists() =>
        FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    /// <summary>
    /// Loads the application icon at the size the notification area actually draws.
    /// </summary>
    /// <remarks>
    /// The .ico carries several frames; asking for the small-icon size picks the one drawn for
    /// this display rather than downscaling the 256-pixel frame, which is what makes the
    /// difference between a crisp tray icon and a smudged one.
    /// </remarks>
    private System.Drawing.Icon LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/panoramabridge.ico");
            using var stream = Application.GetResourceStream(uri)?.Stream;

            if (stream is not null)
            {
                return new System.Drawing.Icon(
                    stream,
                    System.Windows.Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Falling back from the packed icon resource.");
        }

        // Reached when there is no WPF resource stream to read -- under a test host, for one.
        return System.Drawing.SystemIcons.Application;
    }
}
