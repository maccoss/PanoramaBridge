using System.Collections.Specialized;
using PanoramaBridge.App.ViewModels;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Tests.TestDoubles;

namespace PanoramaBridge.Tests.App;

/// <summary>
/// The Configurations tab: adding, copying, removing and switching between pairings.
/// </summary>
/// <remarks>
/// This list is how somebody tells at a glance which folders are covered, so the things worth
/// pinning are the ones that would quietly leave a folder uncovered or point an edit at the wrong
/// configuration.
/// </remarks>
public sealed class ConfigurationsViewModelTests
{
    private sealed class InMemorySettingsStore : ISettingsStore
    {
        public AppSettings Saved { get; private set; } = new();

        public int Saves { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Saved);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Saved = settings;
            Saves++;
            return Task.CompletedTask;
        }
    }

    private static MonitoringConfiguration Watching(string name, string local) => new()
    {
        Name = name,
        LocalDirectory = local,
        CreatedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Account = "config-" + name.ToLowerInvariant(),
    };

    private static (SettingsViewModel Settings, ConfigurationsViewModel List) New(
        params MonitoringConfiguration[] configurations)
    {
        var settings = new SettingsViewModel(
            new InMemorySettingsStore(),
            configurations.Length == 0
                ? new AppSettings()
                : new AppSettings { Configurations = configurations });

        return (settings, new ConfigurationsViewModel(settings));
    }

    [Fact]
    public void The_list_shows_what_is_there()
    {
        var (_, list) = New(
            Watching("Lumos", @"D:\Data\Lumos"),
            Watching("Exploris", @"D:\Data\Exploris"));

        list.Rows.Count.ShouldBe(2);
        list.Rows[0].Name.ShouldBe("Lumos");
        list.Rows[1].LocalDirectory.ShouldBe(@"D:\Data\Exploris");
        list.SelectedIndex.ShouldBe(0, "the window opens on the first one");
    }

    [Fact]
    public async Task Adding_a_configuration_selects_it_so_the_tabs_open_on_it()
    {
        // Adding one and leaving the editor pointed at the previous configuration is how somebody
        // fills in a folder for the wrong pairing without noticing.
        var (settings, list) = New(Watching("Lumos", @"D:\Data\Lumos"));

        await list.AddCommand.ExecuteAsync(null);

        list.Rows.Count.ShouldBe(2);
        list.SelectedIndex.ShouldBe(1);
        settings.ConfigurationIndex.ShouldBe(1);
        settings.LocalDirectory.ShouldBeEmpty("the new one has no folder yet");
    }

    [Fact]
    public async Task A_new_configuration_is_off_until_somebody_fills_it_in()
    {
        // It has no folder and no destination, so there is nothing it could do. Added switched
        // on, it is not merely useless: it is enabled and invalid, which makes the whole settings
        // record invalid and stops monitoring starting for the configurations that were working.
        var (settings, list) = New(Watching("Lumos", Path.GetTempPath()));

        await list.AddCommand.ExecuteAsync(null);

        settings.Configurations[1].Enabled.ShouldBeFalse();
        settings.ToSettings().Validate().ShouldBeEmpty(
            "adding one to set up later must not stop the others transferring");
    }

    [Fact]
    public async Task A_new_configuration_says_it_needs_setting_up_rather_than_just_off()
    {
        // Off on its own reads as a deliberate choice, which for a configuration nobody has
        // filled in yet is the wrong thing to say: the list is where somebody notices it is
        // waiting on them.
        var (_, list) = New(Watching("Lumos", Path.GetTempPath()));

        await list.AddCommand.ExecuteAsync(null);
        await list.StatusesChecked;

        list.Rows[1].Status.ShouldBe("Not set up");
        list.Rows[1].StatusDetail.ShouldNotBeNullOrWhiteSpace();

        list.Rows[0].Status.ShouldBe("Ready", "and the one beside it is unaffected");
    }

    [Fact]
    public async Task A_configuration_switched_off_on_purpose_still_reads_as_off()
    {
        // The distinction only helps if a complete configuration somebody turned off -- an
        // instrument away for service -- still says so rather than claiming to need setting up.
        var (_, list) = New(
            Watching("Away for service", Path.GetTempPath()) with { Enabled = false });

        await list.StatusesChecked;

        list.Rows[0].Status.ShouldBe("Off");
    }

    [Fact]
    public async Task Reloading_leaves_the_recent_destinations_alone_when_nothing_changed()
    {
        // The destination box is an editable ComboBox whose items are this collection and whose
        // text is two-way bound to RemotePath. Replacing the items raises a Reset, the ComboBox
        // throws away its text, and the binding writes the empty string back -- so the
        // destination of whichever configuration was on screen was blanked, and the next save
        // wrote the blank. Not touching the list when nothing about it changed is what stops it.
        var (settings, list) = New(Watching("Lumos", Path.GetTempPath()));

        var resets = 0;
        ((INotifyCollectionChanged)settings.RecentRemotePaths).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                resets++;
            }
        };

        await list.AddCommand.ExecuteAsync(null);
        await settings.EditConfigurationAsync(0);
        await settings.SaveAsync();

        resets.ShouldBe(0, "the recent destinations never changed, so the list was never replaced");
    }

    [Fact]
    public async Task Adding_a_configuration_does_not_blank_the_destination_of_the_one_before_it()
    {
        // What the blanking cost, stated at the level it was reported: add a configuration, go
        // back to the previous one, and its destination is gone.
        var (settings, list) = New(Watching("Lumos", Path.GetTempPath()));

        var before = settings.RemotePath;
        before.ShouldNotBeNullOrWhiteSpace();

        await list.AddCommand.ExecuteAsync(null);
        await settings.EditConfigurationAsync(0);

        settings.RemotePath.ShouldBe(before);
        settings.Configurations[0].RemotePath.ShouldBe(before);
    }

    [Fact]
    public async Task A_new_configuration_records_when_it_was_created()
    {
        var (settings, list) = New();

        await list.AddCommand.ExecuteAsync(null);

        settings.Configurations[^1].CreatedUtc.ShouldNotBe(
            default, "the list has a Created column and a new one can honestly fill it");
    }

    [Fact]
    public async Task A_copy_keeps_the_settings_and_takes_a_name_of_its_own()
    {
        // The reason the lab asked for a copy: a second instrument writing the same file types to
        // the same Panorama folder differs from the first by one directory.
        var (settings, list) = New(
            Watching("Lumos", @"D:\Data\Lumos") with
            {
                Extensions = [".wiff"],
                RemotePath = "/_webdav/MacCoss/shared/@files/",
            });

        await list.CopyCommand.ExecuteAsync(null);

        settings.Configurations.Count.ShouldBe(2);

        var copy = settings.Configurations[1];

        copy.Extensions.ShouldBe([".wiff"], "everything that made it worth copying is carried");
        copy.RemotePath.ShouldBe("/_webdav/MacCoss/shared/@files/");
        copy.Name.ShouldNotBe("Lumos", "two rows called the same thing would be unreadable");
        list.SelectedIndex.ShouldBe(1, "and the editor opens on the copy");
    }

    [Fact]
    public async Task A_copy_gets_its_own_credential_slot()
    {
        // Not shared with the configuration it came from. A copy is a new pairing, and giving it
        // its own slot is what lets it be signed in as somebody else later without signing the
        // original out.
        var (settings, list) = New(Watching("Lumos", @"D:\Data\Lumos"));

        await list.CopyCommand.ExecuteAsync(null);

        settings.Configurations[1].Account
            .ShouldNotBe(settings.Configurations[0].Account);
        settings.Configurations[1].Account.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Copying_twice_does_not_produce_two_rows_with_one_name()
    {
        var (settings, list) = New(Watching("Lumos", @"D:\Data\Lumos"));

        await list.CopyCommand.ExecuteAsync(null);
        list.SelectedIndex = 0;
        await list.CopyCommand.ExecuteAsync(null);

        settings.Configurations
            .Select(c => c.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count()
            .ShouldBe(3);
    }

    [Fact]
    public async Task Removing_a_configuration_asks_first()
    {
        var (settings, list) = New(
            Watching("Lumos", @"D:\Data\Lumos"),
            Watching("Exploris", @"D:\Data\Exploris"));

        var asked = 0;
        list.Confirm = _ => { asked++; return false; };

        await list.DeleteCommand.ExecuteAsync(null);

        asked.ShouldBe(1);
        settings.Configurations.Count.ShouldBe(2, "saying no has to mean no");
    }

    [Fact]
    public async Task Removing_a_configuration_leaves_the_selection_somewhere_valid()
    {
        // Deleting the last row would otherwise leave the editor pointed past the end of the
        // list, which is how the tabs end up showing an empty configuration nothing will save.
        var (settings, list) = New(
            Watching("Lumos", @"D:\Data\Lumos"),
            Watching("Exploris", @"D:\Data\Exploris"));

        list.Confirm = _ => true;
        list.SelectedIndex = 1;

        await list.DeleteCommand.ExecuteAsync(null);

        settings.Configurations.Count.ShouldBe(1);
        list.SelectedIndex.ShouldBe(0);
        settings.ConfigurationIndex.ShouldBe(0);
    }

    [Fact]
    public async Task Switching_the_selection_points_the_editor_tabs_at_that_configuration()
    {
        var (settings, list) = New(
            Watching("Lumos", @"D:\Data\Lumos"),
            Watching("Exploris", @"D:\Data\Exploris"));

        list.SelectedIndex = 1;

        // The switch saves first and is therefore asynchronous; the property set starts it.
        await settings.EditConfigurationAsync(1);

        settings.LocalDirectory.ShouldBe(@"D:\Data\Exploris");
        settings.EditingName.ShouldBe("Exploris");
    }

    [Fact]
    public async Task An_edit_in_progress_is_kept_when_the_selection_changes()
    {
        // Silently discarding half-typed edits on a click elsewhere is the worst of the options:
        // the boxes are simply different when you come back and nothing says why.
        var (settings, list) = New(
            Watching("Lumos", @"D:\Data\Lumos"),
            Watching("Exploris", @"D:\Data\Exploris"));

        settings.LocalDirectory = @"D:\Data\Lumos-moved";

        await settings.EditConfigurationAsync(1);
        await settings.EditConfigurationAsync(0);

        settings.LocalDirectory.ShouldBe(@"D:\Data\Lumos-moved");
        list.Rows[0].LocalDirectory.ShouldBe(@"D:\Data\Lumos-moved", "and the list shows it too");
    }

    [Fact]
    public async Task Ticking_a_row_turns_that_configuration_on_without_disturbing_the_others()
    {
        var (settings, list) = New(
            Watching("Lumos", @"D:\Data\Lumos") with { Enabled = false },
            Watching("Exploris", @"D:\Data\Exploris"));

        list.Rows[0].Enabled = true;

        // The tick writes through the settings, which is asynchronous.
        await Task.Yield();

        settings.Configurations[0].Enabled.ShouldBeTrue();
        settings.Configurations[1].Enabled.ShouldBeTrue("the one beside it is untouched");
    }

    [Fact]
    public async Task A_configuration_with_a_problem_says_so_rather_than_looking_ready()
    {
        // The column exists so that a folder nobody can read is visible in the list, rather than
        // only being discovered when monitoring refuses to start.
        var (_, list) = New(Watching("Lumos", @"X:\not\here"));

        await list.StatusesChecked;

        list.Rows[0].Status.ShouldBe("Needs attention");
        list.Rows[0].StatusDetail.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_status_column_does_not_touch_the_disk_while_the_list_is_built()
    {
        // Validate calls Directory.Exists, and on a share whose server is down that takes the SMB
        // timeout to answer -- once per row, on the UI thread, every time the list is rebuilt.
        // Until the off-thread pass comes back, a row says it is still being checked rather than
        // claiming to be ready, which nothing has established yet.
        var (_, list) = New(Watching("Lumos", @"X:\not\here"));

        list.Rows[0].Status.ShouldBe("Checking...");
        list.Rows[0].Problems.ShouldBeEmpty();
    }

    [Fact]
    public async Task Removing_the_last_configuration_leaves_a_fresh_one_rather_than_nothing()
    {
        // An empty list is a state the editor tabs cannot represent: they would go on showing the
        // configuration just deleted and silently re-add it on the next save, so the settings file
        // said none and the window said one.
        var (settings, list) = New(Watching("Lumos", @"D:\Data\Lumos"));

        list.Confirm = _ => true;

        await list.DeleteCommand.ExecuteAsync(null);

        settings.Configurations.ShouldHaveSingleItem();
        settings.Configurations[0].LocalDirectory.ShouldBeEmpty("and it is a fresh one");
        list.Rows.ShouldHaveSingleItem();
        list.SelectedIndex.ShouldBe(0);
    }

    [Fact]
    public async Task A_configuration_can_be_given_a_name()
    {
        // Without this nothing could set one, so two instruments whose folders are both called
        // Data were two rows with the same name and one entry between them in the status line.
        var (settings, list) = New(
            Watching("Lumos", @"D:\Data\Lumos"),
            Watching("Exploris", @"E:\Data\Exploris"));

        await settings.EditConfigurationAsync(1);

        settings.Name = "  Exploris 480  ";

        await settings.SaveAsync();

        settings.Configurations[1].Name.ShouldBe("Exploris 480", "trimmed on the way in");
        settings.EditingName.ShouldBe("Exploris 480");
        list.Rows[1].Name.ShouldBe("Exploris 480");
    }

    [Fact]
    public async Task Clearing_the_name_falls_back_to_the_folder()
    {
        var (settings, list) = New(Watching("Lumos", @"D:\Data\Lumos"));

        settings.Name = string.Empty;
        await settings.SaveAsync();

        list.Rows[0].Name.ShouldBe("Lumos", "which is what the folder is called");
    }

    [Fact]
    public void A_configuration_that_is_switched_off_reads_as_off_rather_than_as_broken()
    {
        // An instrument away for service. Nothing is wrong with it and nothing needs fixing, so
        // marking it red would train people to ignore the column.
        var (_, list) = New(
            Watching("Away for service", @"X:\not\here") with { Enabled = false });

        list.Rows[0].Status.ShouldBe("Off");
    }

    [Fact]
    public void An_api_key_configuration_says_so_in_the_user_column()
    {
        // An API key has no user name, so the column would otherwise be blank and look broken.
        var (_, list) = New(Watching("Lumos", @"D:\Data\Lumos"));

        list.Rows[0].User.ShouldBe("API key");
    }

    [Fact]
    public void A_configuration_carried_over_from_before_creation_dates_shows_none()
    {
        // The version 1 settings file never recorded when monitoring was set up, so there is
        // nothing honest to put here.
        var (_, list) = New(
            Watching("Lumos", @"D:\Data\Lumos") with { CreatedUtc = default });

        list.Rows[0].Created.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_summary_says_how_many_will_actually_run()
    {
        var (_, list) = New(
            Watching("Lumos", @"D:\Data\Lumos"),
            Watching("Exploris", @"D:\Data\Exploris") with { Enabled = false });

        list.Summary.ShouldBe("2 configurations, 1 on.");

        await list.AddCommand.ExecuteAsync(null);

        // Still one on: a configuration added is one nobody has filled in yet.
        list.Summary.ShouldBe("3 configurations, 1 on.");
    }
}
