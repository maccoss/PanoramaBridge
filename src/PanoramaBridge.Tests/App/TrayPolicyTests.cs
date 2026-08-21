using PanoramaBridge.App.Services;

namespace PanoramaBridge.Tests.App;

/// <summary>
/// The two rules behind the notification-area icon.
/// </summary>
/// <remarks>
/// These are tested and the icon itself is not, on purpose. Creating a real
/// <c>NotifyIcon</c> needs an STA thread and a message loop, and a test that stood one up would
/// assert almost nothing beyond "WinForms works". What can actually go wrong is deciding to hide
/// a window with nothing left to click, and handing the shell a tooltip it refuses -- both of
/// which are plain functions, and both of which are here.
/// </remarks>
public sealed class TrayPolicyTests
{
    [Fact]
    public void Closing_hides_the_window_when_that_is_what_the_user_asked_for()
    {
        TrayPolicy.ShouldHideInsteadOfClosing(
            keepRunningInTray: true,
            trayAvailable: true,
            exiting: false).ShouldBeTrue();
    }

    [Fact]
    public void Closing_really_closes_when_the_setting_is_off()
    {
        TrayPolicy.ShouldHideInsteadOfClosing(
            keepRunningInTray: false,
            trayAvailable: true,
            exiting: false).ShouldBeFalse();
    }

    [Fact]
    public void The_window_is_never_hidden_when_there_is_no_icon_to_bring_it_back()
    {
        // The rule that earns its place. An icon can fail to be created -- a shell without a
        // notification area, a policy that blocks it -- and hiding the window anyway would leave
        // a process running with no interface and no way to reach it except Task Manager, on a
        // machine that may be part-way through an acquisition.
        TrayPolicy.ShouldHideInsteadOfClosing(
            keepRunningInTray: true,
            trayAvailable: false,
            exiting: false).ShouldBeFalse(
            "hiding with nothing to click is a state the user cannot get out of");
    }

    [Fact]
    public void Exit_from_the_tray_menu_is_not_intercepted()
    {
        // Otherwise Exit would hide the window it was asked to close, and the application could
        // only ever be stopped by killing it.
        TrayPolicy.ShouldHideInsteadOfClosing(
            keepRunningInTray: true,
            trayAvailable: true,
            exiting: true).ShouldBeFalse();
    }

    [Fact]
    public void An_icon_with_nowhere_to_sit_is_not_available()
    {
        // The bug that shipped in 26.1.1, stated as a test. IsAvailable reported whether
        // constructing a NotifyIcon threw -- which it does not, because constructing one touches
        // nothing outside the process -- so it answered true on every machine, and the guard
        // that stops the window being hidden with nothing to click could never fire.
        TrayPolicy.IsIconUsable(
            iconCreated: true,
            notificationAreaPresent: false,
            disposed: false).ShouldBeFalse(
            "an icon object exists, but there is no notification area drawing it");
    }

    [Fact]
    public void An_icon_that_could_not_be_created_is_not_available()
    {
        TrayPolicy.IsIconUsable(
            iconCreated: false,
            notificationAreaPresent: true,
            disposed: false).ShouldBeFalse();
    }

    [Fact]
    public void A_taken_down_icon_is_not_available()
    {
        // Reachable during shutdown: the window disposes the icon and the container disposes it
        // again. Anything asking afterwards must not be told there is something to click.
        TrayPolicy.IsIconUsable(
            iconCreated: true,
            notificationAreaPresent: true,
            disposed: true).ShouldBeFalse();
    }

    [Fact]
    public void An_icon_in_a_running_shell_is_available()
    {
        TrayPolicy.IsIconUsable(
            iconCreated: true,
            notificationAreaPresent: true,
            disposed: false).ShouldBeTrue();
    }

    [Fact]
    public void Availability_is_what_decides_whether_the_window_may_hide()
    {
        // The two rules meeting, which is the pairing that actually protects the user: an icon
        // that cannot be shown must leave the window closing normally.
        var available = TrayPolicy.IsIconUsable(
            iconCreated: true, notificationAreaPresent: false, disposed: false);

        TrayPolicy.ShouldHideInsteadOfClosing(
            keepRunningInTray: true,
            trayAvailable: available,
            exiting: false).ShouldBeFalse();
    }

    [Fact]
    public void A_tooltip_that_fits_is_left_exactly_as_it_is()
    {
        const string text = "PanoramaBridge - monitoring";

        TrayPolicy.TruncateTooltip(text).ShouldBe(text);
    }

    [Fact]
    public void A_tooltip_at_the_limit_is_still_left_alone()
    {
        var text = new string('x', TrayPolicy.MaxTooltipLength);

        TrayPolicy.TruncateTooltip(text).ShouldBe(text);
    }

    [Fact]
    public void A_long_tooltip_is_cut_rather_than_thrown_at_the_shell()
    {
        // NotifyIcon.Text throws above 63 characters. A status line carrying a deep UNC path
        // reaches that easily, and it would throw on a machine nobody is watching.
        var text = @"PanoramaBridge - monitoring \\fileserver\instruments\QE-Exactive-HF\data\2026";

        var result = TrayPolicy.TruncateTooltip(text);

        result.Length.ShouldBe(TrayPolicy.MaxTooltipLength);
        result.ShouldEndWith("…");   // so it is visible that something was cut
        result.ShouldStartWith("PanoramaBridge - monitoring");
    }

    [Fact]
    public void Truncating_nothing_is_a_programming_error_rather_than_an_empty_tooltip()
    {
        Should.Throw<ArgumentNullException>(() => TrayPolicy.TruncateTooltip(null!));
    }

    [Fact]
    public void A_cut_never_splits_a_character_in_half()
    {
        // Cutting at a fixed index can land between the halves of a surrogate pair, and a lone
        // surrogate draws as a replacement box. One emoji in a folder name is enough to reach it.
        // Built so a pair straddles the cut: 62 filler characters, then the pair at 62 and 63.
        var text = new string('x', TrayPolicy.MaxTooltipLength - 1) + "😀" + "tail";

        var result = TrayPolicy.TruncateTooltip(text);

        char.IsLowSurrogate(result[^2]).ShouldBeFalse("the ellipsis must not follow half a pair");

        foreach (var (c, i) in result.Select((c, i) => (c, i)))
        {
            if (char.IsHighSurrogate(c))
            {
                (i + 1 < result.Length && char.IsLowSurrogate(result[i + 1])).ShouldBeTrue(
                    "every high surrogate must keep its partner");
            }

            if (char.IsLowSurrogate(c))
            {
                (i > 0 && char.IsHighSurrogate(result[i - 1])).ShouldBeTrue(
                    "every low surrogate must keep its partner");
            }
        }
    }
}
