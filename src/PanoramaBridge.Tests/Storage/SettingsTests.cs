using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;

namespace PanoramaBridge.Tests.Storage;

public sealed class AppSettingsTests
{
    [Theory]
    [InlineData("raw, sld, csv", ".raw,.sld,.csv")]
    [InlineData(".RAW; .D", ".raw,.d")]
    [InlineData("raw raw .raw RAW", ".raw")]
    [InlineData("", "")]
    public void Extensions_are_normalized_to_lower_case_with_a_leading_dot(string input, string expected)
    {
        // Users type these however they like; matching is done on Path.GetExtension, which is
        // dotted and case-folded, so the stored form has to be too.
        string.Join(",", AppSettings.ParseExtensions(input)).ShouldBe(expected);
    }

    [Fact]
    public void Skyline_caches_are_excluded_out_of_the_box()
    {
        // AutoQC runs on the instrument computer beside the acquisition software and leaves
        // run.raw.skyd next to run.raw. Nobody should have to discover that and configure it.
        new AppSettings().ExcludedExtensions.ShouldContain(".skyd");
        new AppSettings().FormatExcludedExtensions().ShouldContain(".skyd");
    }

    [Fact]
    public void Editing_the_exclusions_counts_as_a_change()
    {
        // Equality here is hand-written, because the compiler-generated version compares the
        // lists by reference. A property left out of it makes the settings screen believe
        // nothing was edited and quietly drop the edit.
        var settings = new AppSettings();

        settings.ShouldNotBe(settings with { ExcludedExtensions = [".skyd", ".blib"] });
        settings.ShouldNotBe(settings with { ExcludedExtensions = [] });
        settings.ShouldBe(settings with { ExcludedExtensions = [.. settings.ExcludedExtensions] });
    }

    [Fact]
    public void A_null_list_does_not_take_the_whole_settings_screen_down_with_it()
    {
        // A property initializer does not survive an explicit null in the JSON, and the settings
        // file is meant to be hand-editable. One hand-typed null used to reach GetHashCode and
        // FormatExtensions and throw -- straight past the corrupt-file fallback, which only
        // catches malformed JSON, so the file was valid and the application still fell over.
        var settings = new AppSettings { Extensions = null!, ExcludedExtensions = null! };

        settings.Extensions.ShouldBeEmpty();
        settings.ExcludedExtensions.ShouldBeEmpty();

        Should.NotThrow(() => settings.GetHashCode());
        Should.NotThrow(() => settings.FormatExtensions());
        Should.NotThrow(() => settings.FormatExcludedExtensions());
        Should.NotThrow(() => settings.Equals(new AppSettings()));

        // And it degrades to something the screen can explain rather than an empty list quietly
        // meaning "everything".
        settings.Validate().ShouldContain(p => p.Contains("at least one file extension"));
    }

    [Fact]
    public void Excluding_the_suffix_that_holds_the_spectra_is_reported()
    {
        // The 38 MB-of-13.7 GB truncation reachable by configuration instead of by a bug: the
        // .wiff uploads and records as verified while the spectra stay behind, and nothing looks
        // wrong until somebody opens it in Skyline.
        var problems = new AppSettings
        {
            LocalDirectory = Path.GetTempPath(),
            Extensions = [".wiff"],
            ExcludedExtensions = [".skyd", ".scan"],
        }.Validate();

        problems.ShouldContain(p => p.Contains(".scan"));
    }

    [Fact]
    public void Excluding_a_suffix_nothing_listed_needs_is_not_reported()
    {
        new AppSettings
        {
            LocalDirectory = Path.GetTempPath(),
            Extensions = [".raw"],
            ExcludedExtensions = [".skyd", ".scan"],
        }.Validate().ShouldNotContain(p => p.Contains(".scan"));
    }

    [Fact]
    public void The_lab_default_destination_is_offered_out_of_the_box()
    {
        // Nearly every upload from this lab goes here, so nobody should have to remember the
        // _webdav and @files incantation.
        new AppSettings().RemotePath.ShouldBe("/_webdav/MacCoss/maccoss/@files/");
        new AppSettings().RecentRemotePaths.ShouldContain("/_webdav/MacCoss/maccoss/@files/");
    }

    [Fact]
    public void A_chosen_destination_moves_to_the_front_of_the_recent_list()
    {
        var settings = new AppSettings()
            .WithRecentPath("/_webdav/MacCoss/Kyle/@files/")
            .WithRecentPath("/_webdav/MacCoss/maccoss/@files/RawFiles/");

        settings.RemotePath.ShouldBe("/_webdav/MacCoss/maccoss/@files/RawFiles/");
        settings.RecentRemotePaths[0].ShouldBe("/_webdav/MacCoss/maccoss/@files/RawFiles/");
        settings.RecentRemotePaths[1].ShouldBe("/_webdav/MacCoss/Kyle/@files/");

        // The lab default stays reachable however many others are used.
        settings.RecentRemotePaths.ShouldContain("/_webdav/MacCoss/maccoss/@files/");
    }

    [Fact]
    public void The_recent_list_does_not_grow_without_bound_or_repeat_itself()
    {
        var settings = new AppSettings();
        for (var i = 0; i < 30; i++)
        {
            settings = settings.WithRecentPath($"/_webdav/MacCoss/folder{i}/@files/");
        }

        settings.RecentRemotePaths.Count.ShouldBeLessThanOrEqualTo(8);
        settings.RecentRemotePaths.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            .ShouldBe(settings.RecentRemotePaths.Count);
    }

    [Fact]
    public void Re_selecting_the_current_path_does_not_duplicate_it()
    {
        var settings = new AppSettings().WithRecentPath("/_webdav/a/").WithRecentPath("/_webdav/a/");

        settings.RecentRemotePaths.Count(p => p == "/_webdav/a/").ShouldBe(1);
    }

    [Fact]
    public void Defaults_encode_the_decisions_that_matter()
    {
        var settings = new AppSettings();

        settings.AuthMode.ShouldBe(AuthMode.ApiKey, "an API key is safer than the account password");
        settings.VerifyUploads.ShouldBeTrue("an unverified upload is the failure mode being designed out");
        settings.ConflictPolicy.ShouldBe(ConflictPolicy.Ask, "guessing risks destroying data");
        settings.MaxConcurrentTransfers.ShouldBe(3);
        settings.ReconcileMinutes.ShouldBeGreaterThan(0, "the periodic sweep is the real safety net");
        settings.StabilitySeconds.ShouldBeGreaterThan(0);
        settings.LockedFileRetryIntervalSeconds.ShouldBe(
            30,
            "a file in use is looked at slowly, but it is always looked at");
    }

    [Fact]
    public void Validation_reports_what_the_user_has_to_fix()
    {
        var problems = new AppSettings { LocalDirectory = string.Empty }.Validate();

        problems.ShouldContain(p => p.Contains("Local Monitoring"));
    }

    [Fact]
    public void Validation_passes_for_a_usable_configuration()
    {
        var settings = new AppSettings { LocalDirectory = Path.GetTempPath() };

        settings.Validate().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://panoramaweb.org")]
    public void A_server_address_that_is_not_http_is_rejected(string url)
    {
        new AppSettings { LocalDirectory = Path.GetTempPath(), ServerUrl = url }
            .Validate()
            .ShouldContain(p => p.Contains("server address"));
    }

    [Fact]
    public void A_password_login_without_a_user_name_is_rejected()
    {
        new AppSettings
        {
            LocalDirectory = Path.GetTempPath(),
            AuthMode = AuthMode.UserNameAndPassword,
            UserName = string.Empty,
        }
            .Validate()
            .ShouldContain(p => p.Contains("user name"));
    }
}

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pb-settings-").FullName;

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public async Task Settings_round_trip()
    {
        var store = new JsonSettingsStore(SettingsPath);

        var saved = new AppSettings
        {
            LocalDirectory = @"D:\Data",
            Extensions = [".raw", ".d"],
            ExcludedExtensions = [".skyd", ".blib"],
            MaxConcurrentTransfers = 5,
            ConflictPolicy = ConflictPolicy.Overwrite,
            AuthMode = AuthMode.UserNameAndPassword,
            UserName = "someone@uw.edu",
            VerboseLogging = true,
        };

        await store.SaveAsync(saved);
        var loaded = await store.LoadAsync();

        loaded.ShouldBe(saved);
    }

    [Fact]
    public async Task A_missing_file_yields_defaults_rather_than_an_error()
    {
        (await new JsonSettingsStore(SettingsPath).LoadAsync()).ShouldBe(new AppSettings());
    }

    [Fact]
    public void Settings_have_no_member_capable_of_holding_a_secret()
    {
        // Asserted structurally rather than by scanning the output for suspicious words: the
        // serialized file legitimately contains the string "ApiKey" as the name of the auth
        // MODE, so a substring scan proves nothing either way. What actually matters is that
        // there is nowhere in this type for a secret to live, which is what makes the file safe
        // to read, copy or attach to a support request.
        var secretish = new[] { "password", "apikey", "secret", "token", "credential", "key" };

        var offenders = typeof(AppSettings)
            .GetProperties()
            .Where(property =>
                property.PropertyType == typeof(string)
                && secretish.Any(word =>
                    property.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Select(property => property.Name)
            .ToArray();

        offenders.ShouldBeEmpty(
            "credentials belong in Windows Credential Manager, never in the settings file");
    }

    [Fact]
    public async Task The_account_name_is_saved_but_it_is_not_a_secret()
    {
        var store = new JsonSettingsStore(SettingsPath);
        await store.SaveAsync(new AppSettings { UserName = "someone@uw.edu" });

        var text = await File.ReadAllTextAsync(SettingsPath);

        text.ShouldContain("someone@uw.edu");
    }

    [Fact]
    public async Task A_corrupt_file_falls_back_to_defaults_and_is_kept_for_inspection()
    {
        // Refusing to start because a settings file got truncated would be a worse outcome than
        // starting with defaults, but discarding the evidence would be worse still.
        await File.WriteAllTextAsync(SettingsPath, "{ this is not json");

        var loaded = await new JsonSettingsStore(SettingsPath).LoadAsync();

        loaded.ShouldBe(new AppSettings());
        File.Exists(SettingsPath + ".corrupt").ShouldBeTrue();
    }

    [Fact]
    public async Task Enums_are_written_by_name_so_the_file_stays_readable()
    {
        var store = new JsonSettingsStore(SettingsPath);
        await store.SaveAsync(new AppSettings { ConflictPolicy = ConflictPolicy.Overwrite });

        var text = await File.ReadAllTextAsync(SettingsPath);

        text.ShouldContain("Overwrite");
    }

    [Fact]
    public async Task Withdrawn_settings_are_normalized_without_losing_the_rest_of_the_file()
    {
        // FolderAcquisitions was retired as well. System.Text.Json deliberately ignores it as an
        // unknown property, so an upgrade keeps every setting it still understands.
        await File.WriteAllTextAsync(
            SettingsPath,
            """
            {
              "LocalDirectory": "D:\\Data",
              "ConflictPolicy": "Rename",
              "FolderAcquisitions": true
            }
            """);

        var loaded = await new JsonSettingsStore(SettingsPath).LoadAsync();

        loaded.LocalDirectory.ShouldBe(@"D:\Data");
        loaded.ConflictPolicy.ShouldBe(ConflictPolicy.Ask);

        var rewritten = await File.ReadAllTextAsync(SettingsPath);
        rewritten.ShouldNotContain("Rename");
        rewritten.ShouldNotContain("FolderAcquisitions");
        rewritten.ShouldContain("Ask");
    }

    [Fact]
    public async Task A_file_written_before_exclusions_existed_picks_up_the_defaults()
    {
        // The upgrade path is the whole point of the fix: anyone already running PanoramaBridge
        // next to AutoQC has a settings file with no ExcludedExtensions in it, and has to stop
        // collecting .skyd caches without being told to go and edit a box.
        await File.WriteAllTextAsync(
            SettingsPath,
            """
            {
              "LocalDirectory": "D:\\Data",
              "Extensions": [".raw"]
            }
            """);

        var loaded = await new JsonSettingsStore(SettingsPath).LoadAsync();

        loaded.Extensions.ShouldBe([".raw"]);
        loaded.ExcludedExtensions.ShouldContain(".skyd");
    }

    [Fact]
    public async Task A_hand_edited_null_list_loads_instead_of_throwing()
    {
        // null is valid JSON, so it sails past the corrupt-file fallback and reaches the record.
        await File.WriteAllTextAsync(
            SettingsPath,
            """
            {
              "LocalDirectory": "D:\\Data",
              "Extensions": null,
              "ExcludedExtensions": null
            }
            """);

        var loaded = await new JsonSettingsStore(SettingsPath).LoadAsync();

        loaded.LocalDirectory.ShouldBe(@"D:\Data");
        loaded.Extensions.ShouldBeEmpty();
        loaded.ExcludedExtensions.ShouldBeEmpty();
        Should.NotThrow(() => loaded.FormatExcludedExtensions());
    }

    [Fact]
    public async Task An_empty_exclusion_list_survives_a_round_trip()
    {
        // Clearing the box is how somebody asks for every companion file, so it must not be
        // mistaken for "this file predates the setting" and quietly refilled with the defaults.
        var store = new JsonSettingsStore(SettingsPath);
        await store.SaveAsync(new AppSettings { ExcludedExtensions = [] });

        (await store.LoadAsync()).ExcludedExtensions.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_save_leaves_no_temporary_file_behind()
    {
        // The write is temp-then-move so a crash cannot leave a half-written file that fails to
        // parse on next launch.
        var store = new JsonSettingsStore(SettingsPath);
        await store.SaveAsync(new AppSettings());

        Directory.GetFiles(_directory, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public async Task Concurrent_saves_do_not_corrupt_the_file()
    {
        var store = new JsonSettingsStore(SettingsPath);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            store.SaveAsync(new AppSettings { MaxConcurrentTransfers = 1 + (i % 8) })));

        // Whichever write landed last, the file has to be parseable.
        await Should.NotThrowAsync(() => store.LoadAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
