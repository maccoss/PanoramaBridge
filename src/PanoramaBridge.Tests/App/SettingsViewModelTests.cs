using PanoramaBridge.App.ViewModels;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;

namespace PanoramaBridge.Tests.App;

/// <summary>
/// The settings screen: what it shows, and what it hands back.
/// </summary>
/// <remarks>
/// The view model copies every setting out to properties and back again by hand. A setting
/// missed in either direction is silent -- the box shows the right value, editing it appears to
/// work, and the value is lost or never loaded. That has happened twice while this was being
/// built, so the round trip is asserted over the whole record rather than field by field.
/// </remarks>
public sealed class SettingsViewModelTests
{
    /// <summary>Holds settings in memory, so no test touches the real settings file.</summary>
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

    /// <summary>Settings with every field moved off its default, so a dropped one shows up.</summary>
    private static AppSettings Distinctive() => new()
    {
        LocalDirectory = @"\\fileserver\instruments\QE",
        IncludeSubdirectories = false,
        Extensions = [".wiff", ".d"],
        StabilitySeconds = 42,
        ReconcileMinutes = 7,
        LockedFileRetryIntervalSeconds = 11,
        LockedFileMaxRetries = 3,
        MaxConcurrentTransfers = 6,
        ConflictPolicy = ConflictPolicy.Overwrite,
        VerifyUploads = false,
        WriteChecksumSidecars = false,
        ServerUrl = "https://example.org",
        AuthMode = AuthMode.UserNameAndPassword,
        UserName = "someone",
        SaveCredentials = false,
        RemotePath = "/_webdav/Somewhere/@files/",
        TrustedRootCertificatePath = @"C:\certs\extra.cer",
        VerboseLogging = true,
        MinimizeToTray = false,
    };

    [Fact]
    public async Task Every_setting_survives_a_trip_through_the_screen()
    {
        // Loaded into the view model, saved straight back out, and compared as a whole. A field
        // the view model forgets to carry reverts to its default here and fails the comparison,
        // which is the only way to catch one without listing them all again.
        var store = new InMemorySettingsStore();
        var original = Distinctive();

        var view = new SettingsViewModel(store, original);
        var saved = await view.SaveAsync();

        saved.ShouldBe(original with { RecentRemotePaths = saved.RecentRemotePaths });
        store.Saved.ShouldBe(saved);
    }

    [Fact]
    public void Nothing_is_unsaved_before_anything_is_edited()
    {
        var view = new SettingsViewModel(new InMemorySettingsStore(), Distinctive());

        view.HasUnsavedChanges.ShouldBeFalse(
            "a screen that claims unsaved edits the moment it opens teaches people to ignore it");
    }

    [Fact]
    public void A_withdrawn_conflict_policy_is_shown_as_ask_me()
    {
        var view = new SettingsViewModel(
            new InMemorySettingsStore(),
            Distinctive() with { ConflictPolicy = ConflictPolicy.Rename });

        view.ConflictPolicy.ShouldBe(ConflictPolicy.Ask);
        view.HasUnsavedChanges.ShouldBeFalse();
    }

    [Fact]
    public void Editing_a_field_is_noticed()
    {
        var view = new SettingsViewModel(new InMemorySettingsStore(), Distinctive());

        view.StabilitySeconds = 99;

        view.HasUnsavedChanges.ShouldBeTrue();
    }

    [Fact]
    public async Task Saving_promotes_the_destination_to_the_top_of_the_recent_list()
    {
        var store = new InMemorySettingsStore();
        var view = new SettingsViewModel(store, new AppSettings());

        view.RemotePath = "/_webdav/MacCoss/maccoss/@files/new-project/";
        var saved = await view.SaveAsync();

        saved.RecentRemotePaths[0].ShouldBe("/_webdav/MacCoss/maccoss/@files/new-project/");
        saved.RecentRemotePaths.ShouldContain(AppSettings.MacCossFilesPath, "the lab default stays available");
    }

    // SkippableFact, not Fact: Skip.If throws a SkipException that only [SkippableFact] turns
    // into a skip. Under a plain [Fact] it is a failure, and one that hides on any machine that
    // happens to have a network drive mapped -- which is every machine this was written on, and
    // no build agent.
    [SkippableFact]
    public async Task A_mapped_drive_is_recorded_as_the_share_it_stands_for()
    {
        // A drive letter belongs to one Windows sign-in, so storing one makes the monitored
        // folder invisible to a service or scheduled task. Resolving happens on save as well as
        // on browse, because the box can be typed into.
        var mapped = DriveInfo.GetDrives()
            .Select(d => d.Name)
            .FirstOrDefault(PanoramaBridge.Core.Infrastructure.NetworkPaths.IsMappedDrive);

        Skip.If(mapped is null, "No mapped network drive on this machine.");

        var store = new InMemorySettingsStore();
        var view = new SettingsViewModel(store, new AppSettings());

        view.LocalDirectory = mapped!;
        var saved = await view.SaveAsync();

        saved.LocalDirectory.ShouldStartWith(@"\\");
        view.LocalDirectory.ShouldBe(saved.LocalDirectory, "and the box shows what was stored");
    }

    [Fact]
    public void Extensions_are_shown_as_text_and_read_back_as_a_list()
    {
        var view = new SettingsViewModel(new InMemorySettingsStore(), Distinctive());

        view.ExtensionsText.ShouldBe(".wiff, .d");

        view.ExtensionsText = "RAW; mzML  .d";
        view.ToSettings().Extensions.ShouldBe([".raw", ".mzml", ".d"]);
    }

    [Fact]
    public void Problems_are_reported_before_a_transfer_is_attempted()
    {
        var view = new SettingsViewModel(new InMemorySettingsStore(), new AppSettings());

        view.Problems.ShouldContain(p => p.Contains("Local Monitoring"));

        view.LocalDirectory = Path.GetTempPath();
        view.Problems.ShouldBeEmpty();
    }
}
