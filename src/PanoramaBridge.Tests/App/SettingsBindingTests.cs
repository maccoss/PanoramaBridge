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
    /// <summary>The views whose DataContext is the settings view model.</summary>
    public static TheoryData<string> SettingsViews =>
    [
        "LocalMonitoringView.xaml",
        "RemoteSettingsView.xaml",
        "ApplicationView.xaml",
    ];

    /// <summary>The tabs that edit one configuration rather than the machine.</summary>
    public static TheoryData<string> PerConfigurationViews =>
    [
        "LocalMonitoringView.xaml",
        "RemoteSettingsView.xaml",
    ];

    /// <summary>
    /// Settings that describe this computer rather than any one pairing.
    /// </summary>
    /// <remarks>
    /// Taken from <see cref="AppSettings"/> rather than listed by hand, so a setting added there
    /// is covered without anybody remembering to come back here.
    /// </remarks>
    private static string[] ApplicationLevelSettings() =>
        typeof(PanoramaBridge.Core.Storage.AppSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Where(name => name is not ("Configurations" or "EnabledConfigurations" or "Version"))

            // RecentRemotePaths is the one real exception, and it is an exception because it is
            // not edited anywhere: it is the list of destinations offered in the drop-down beside
            // a configuration's own remote path, gathered from every configuration because a path
            // one of them uses is exactly what the next one wants to start from. Nothing about it
            // is set on that tab, so nothing about it can be misread as belonging to the
            // configuration being edited.
            .Where(name => name is not "RecentRemotePaths")
            .ToArray();

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

        // Properties only. WPF resolves a binding path to a property, so accepting methods and
        // events would let a binding pass here and still fail silently at run time -- which is
        // the entire failure this test exists to catch.
        var available = typeof(SettingsViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in names)
        {
            available.ShouldContain(
                name,
                $"{view} binds to '{name}', which SettingsViewModel does not have");
        }
    }

    [Theory]
    [MemberData(nameof(PerConfigurationViews))]
    public void No_machine_wide_setting_appears_on_a_per_configuration_tab(string view)
    {
        // Local Monitoring and Remote Settings edit whichever configuration is selected. A
        // machine-wide setting shown beside them reads as belonging to that configuration, so
        // somebody changing it for one instrument would reasonably believe the others were
        // untouched. The screen would be saying something untrue, and silently.
        //
        // Stated as the rule rather than as a list of where things sit today, because the next
        // setting to be added is the one that gets put on the nearest tab.
        var xaml = File.ReadAllText(Path.Combine(ViewsDirectory(), view));

        var bound = BindingPath()
            .Matches(xaml)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in ApplicationLevelSettings())
        {
            bound.ShouldNotContain(
                name,
                $"{view} edits one configuration, and {name} describes the whole machine. "
                + "It belongs on the Application tab.");
        }
    }

    [Fact]
    public void The_machine_wide_settings_that_have_a_control_are_on_the_application_tab()
    {
        // The other half: having moved them off the per-configuration tabs, they have to have
        // somewhere to be. Not every application setting has a control -- RecordSha256 and
        // YieldToInstrumentSoftware are deliberately settings-file-only -- so this names the ones
        // that do rather than requiring all of them.
        var application = File.ReadAllText(Path.Combine(ViewsDirectory(), "ApplicationView.xaml"));

        application.ShouldContain("MinimizeToTray");
        application.ShouldContain("VerboseLogging");
        application.ShouldContain("MaxConcurrentTransfers");
        application.ShouldContain("TrustedRootCertificatePath");
    }

    [Fact]
    public void Every_binding_on_the_configurations_tab_resolves()
    {
        // The same silent failure, against a different view model. This one matters more than
        // most: the list is how somebody tells which folders are covered, and a column bound to
        // a property that is not there is simply blank rather than wrong-looking.
        var xaml = File.ReadAllText(Path.Combine(ViewsDirectory(), "ConfigurationsView.xaml"));

        var names = BindingPath()
            .Matches(xaml)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        names.ShouldNotBeEmpty("the view should bind to something");

        // Both, because the grid's rows bind to the row view model while everything around them
        // binds to the tab's own.
        var available = typeof(ConfigurationsViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Concat(typeof(ConfigurationRowViewModel)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in names)
        {
            available.ShouldContain(
                name,
                $"ConfigurationsView binds to '{name}', which neither view model has");
        }
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
