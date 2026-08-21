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
/// Every failure here is non-fatal. An icon that cannot be created is reported through
/// <see cref="IsAvailable"/>, which keeps the window closing normally, because an application
/// that cannot show its window is worse than one that does not sit in the tray.
/// </para>
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon? _icon;
    private readonly ILogger<TrayIcon> _log;

    private bool _disposed;
    private bool _announced;

    public TrayIcon(ILogger<TrayIcon> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));

        try
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();

            var open = menu.Items.Add("Open PanoramaBridge");
            open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

            // Bold, so a double-click and the menu's default item visibly agree.
            open.Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold);

            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            var exit = menu.Items.Add("Exit");
            exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

            _icon = new System.Windows.Forms.NotifyIcon
            {
                Icon = LoadIcon(),
                Text = Core.Infrastructure.AppInfo.ProductName,
                ContextMenuStrip = menu,
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
            // Seen when the shell is not running the notification area at all, which happens on
            // stripped-down server images. Not a reason to fail startup.
            _log.LogWarning(ex, "The notification area icon could not be created.");
            _icon = null;
        }
    }

    /// <summary>Raised when the user asks for the window back.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>Raised when the user asks to exit from the tray menu.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>
    /// Whether an icon exists. False means the window must never be hidden.
    /// </summary>
    public bool IsAvailable => _icon is not null;

    /// <summary>Whether the icon is currently shown.</summary>
    public bool Visible
    {
        get => _icon?.Visible == true;
        set
        {
            if (_icon is not null && !_disposed)
            {
                _icon.Visible = value;
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
    /// <remarks>
    /// Windows puts a new icon in the hidden overflow by default, so without this the window
    /// simply vanishes and the obvious conclusion is that it exited -- at which point somebody
    /// starts it again and wonders why nothing is transferring. Shown once per run: repeating it
    /// on every close would be worse than not showing it at all.
    /// </remarks>
    public void AnnounceStillRunning()
    {
        if (_icon is null || _disposed || _announced)
        {
            return;
        }

        _announced = true;

        try
        {
            _icon.ShowBalloonTip(
                5000,
                Core.Infrastructure.AppInfo.ProductName,
                "Still running, and still watching for new files. "
                + "Double-click this icon to reopen it.",
                System.Windows.Forms.ToolTipIcon.Info);
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
    /// Safe to call more than once: the window disposes this when it closes so the icon goes
    /// immediately rather than lingering until the pointer next passes over it, and the service
    /// container disposes it again on the way out. The same double-dispose that once turned
    /// closing the application into a "could not start" dialog.
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
            _icon.ContextMenuStrip?.Dispose();
            _icon.Dispose();
        }
    }

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
