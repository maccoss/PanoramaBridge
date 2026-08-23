using System.Reflection;
using System.Text.RegularExpressions;
using PanoramaBridge.App.ViewModels;

namespace PanoramaBridge.Tests.App;

/// <summary>
/// Every setting the two settings tabs bind to exists on the view model behind them.
/// </summary>
/// <remarks>
/// <para>
/// XAML bindings fail silently. A control bound to a property that is not there shows an empty
/// box, accepts an edit, and drops it -- and the compiler says nothing, so nothing here would go
/// red. That is exactly the failure mode moving a control between tabs invites, and the reason
/// this exists: the tray and verbose-logging checkboxes moved out of Remote Settings, and a typo
/// during that move would have produced two settings that quietly stopped working.
/// </para>
/// <para>
/// Both tabs set <c>DataContext="{Binding Settings}"</c> in MainWindow.xaml, so the target is
/// always <see cref="SettingsViewModel"/>.
/// </para>
/// </remarks>
public sealed partial class SettingsBindingTests
{
    /// <summary>The two views whose DataContext is the settings view model.</summary>
    public static TheoryData<string> SettingsViews =>
    [
        "LocalMonitoringView.xaml",
        "RemoteSettingsView.xaml",
    ];

    [GeneratedRegex(@"\{Binding\s+(?:Path=)?([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex BindingPath();

    [Theory]
    [MemberData(nameof(SettingsViews))]
    public void Every_binding_on_a_settings_tab_resolves(string view)
    {
        var xaml = File.ReadAllText(Path.Combine(ViewsDirectory(), view));

        var names = BindingPath()
            .Matches(xaml)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        names.ShouldNotBeEmpty("the view should bind to something");

        var available = typeof(SettingsViewModel)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in names)
        {
            available.ShouldContain(
                name,
                $"{view} binds to '{name}', which SettingsViewModel does not have");
        }
    }

    [Fact]
    public void The_application_settings_live_on_the_local_tab_and_not_the_remote_one()
    {
        // Neither is a remote setting. They sat under Remote Settings / Advanced beside a
        // trusted-root certificate path only because there was nowhere else to put them, which
        // is a poor reason and a confusing place to look.
        var local = File.ReadAllText(Path.Combine(ViewsDirectory(), "LocalMonitoringView.xaml"));
        var remote = File.ReadAllText(Path.Combine(ViewsDirectory(), "RemoteSettingsView.xaml"));

        local.ShouldContain("MinimizeToTray");
        local.ShouldContain("VerboseLogging");

        remote.ShouldNotContain("MinimizeToTray");
        remote.ShouldNotContain("VerboseLogging");

        // The certificate stays: it is genuinely about reaching the server.
        remote.ShouldContain("TrustedRootCertificatePath");
    }

    /// <summary>Walks up to the repository so the XAML can be read as text.</summary>
    /// <remarks>
    /// Read from source rather than from a packed resource because the point is to check what a
    /// developer just edited, and because a binding that does not resolve is invisible either way.
    /// </remarks>
    private static string ViewsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "PanoramaBridge.App", "Views");

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find src/PanoramaBridge.App/Views above " + AppContext.BaseDirectory);
    }
}
