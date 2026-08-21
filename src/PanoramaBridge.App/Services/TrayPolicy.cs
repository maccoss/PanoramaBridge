namespace PanoramaBridge.App.Services;

/// <summary>
/// The rules behind the notification-area icon, with none of the plumbing.
/// </summary>
/// <remarks>
/// Separate from <see cref="TrayIcon"/>, and deliberately free of any reference to WinForms, so
/// these can be tested without a message loop, an STA thread or a real notification area. The
/// plumbing around them is thin; these two are the parts that can strand a user or throw.
/// </remarks>
public static class TrayPolicy
{
    /// <summary>
    /// The shell's tooltip limit.
    /// </summary>
    /// <remarks>
    /// <c>NOTIFYICONDATA.szTip</c> is 128 characters wide, but <c>NotifyIcon.Text</c> has always
    /// enforced 63 and throws above it. The lower number is the one that matters.
    /// </remarks>
    public const int MaxTooltipLength = 63;

    /// <summary>
    /// Whether a close request should hide the window instead of closing it.
    /// </summary>
    /// <param name="keepRunningInTray">The user's setting.</param>
    /// <param name="trayAvailable">Whether an icon was actually created.</param>
    /// <param name="exiting">True when the user asked to exit outright.</param>
    /// <remarks>
    /// <paramref name="trayAvailable"/> is the one that earns its place. Hiding the window when
    /// no icon exists leaves a running process with no user interface and no way back to it
    /// short of Task Manager, on a machine that may be part-way through an acquisition. Exiting
    /// when the user wanted the tray is wrong in a way they can correct in a second; hiding with
    /// nothing to click is not.
    /// </remarks>
    public static bool ShouldHideInsteadOfClosing(
        bool keepRunningInTray,
        bool trayAvailable,
        bool exiting) =>
        keepRunningInTray && trayAvailable && !exiting;

    /// <summary>
    /// Truncates hover text to what the shell accepts, with an ellipsis when something was cut.
    /// </summary>
    /// <remarks>
    /// The tooltip carries the status line, and the status line carries folder names: "Monitoring
    /// \\fileserver\instruments\QE\data" passes 63 characters without trying. Letting
    /// that reach <c>NotifyIcon.Text</c> unchecked turns a long path into an exception on a
    /// machine nobody is watching.
    /// </remarks>
    public static string TruncateTooltip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length <= MaxTooltipLength)
        {
            return text;
        }

        var keep = MaxTooltipLength - 1;

        // Cutting at a fixed index can land between the halves of a surrogate pair and emit a
        // lone surrogate, which the shell draws as a replacement box. One astral-plane character
        // in a folder name is enough; stepping back one unit costs nothing.
        if (char.IsHighSurrogate(text[keep - 1]) && char.IsLowSurrogate(text[keep]))
        {
            keep--;
        }

        return string.Concat(text.AsSpan(0, keep), "…");
    }
}
