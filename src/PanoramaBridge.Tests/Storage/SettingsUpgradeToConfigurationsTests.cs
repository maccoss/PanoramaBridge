using PanoramaBridge.Core.Security;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;
using PanoramaBridge.Tests.TestDoubles;

namespace PanoramaBridge.Tests.Storage;

/// <summary>
/// Reading a settings file written before configurations existed.
/// </summary>
/// <remarks>
/// <para>
/// Every installed copy has one of these. The whole of it -- monitored folder, destination,
/// server, sign-in, readiness timings -- moved out of the top level of the file and into a
/// configuration, and the type that used to hold those properties no longer has them. A version 1
/// file read as the current shape therefore does not fail: it loads, silently missing everything
/// that moved, and the first anyone knows of it is that an instrument stopped transferring.
/// </para>
/// <para>
/// That is what these are for. Unlike the ledger rebuild in phase 1, nothing here can destroy
/// data -- the old file is only rewritten once the new shape exists, through a temporary file --
/// so the risk is not loss but silence.
/// </para>
/// </remarks>
public sealed class SettingsUpgradeToConfigurationsTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("pb-settings-upgrade-").FullName;

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    /// <summary>
    /// A settings file as v26.8.1 wrote it, with every property moved off its default.
    /// </summary>
    /// <remarks>
    /// Written out as text rather than built from the record it came from, because the record is
    /// exactly what changed. Serializing the current type could only ever produce the current
    /// shape, which would make the test agree with itself and prove nothing.
    /// </remarks>
    private const string Version1Settings =
        """
        {
          "LocalDirectory": "D:\\Data\\QE-HF",
          "IncludeSubdirectories": false,
          "Extensions": [".raw", ".mzml"],
          "ExcludedExtensions": [".skyd", ".blib"],
          "StabilitySeconds": 42,
          "ReconcileMinutes": 7,
          "LockedFileRetryIntervalSeconds": 11,
          "LockedFileMaxRetries": 3,
          "MaxConcurrentTransfers": 6,
          "ConflictPolicy": "Overwrite",
          "VerifyUploads": false,
          "WriteChecksumSidecars": false,
          "YieldToInstrumentSoftware": false,
          "RecordSha256": true,
          "ServerUrl": "https://labkey.partner.edu",
          "AuthMode": "UserNameAndPassword",
          "UserName": "someone@uw.edu",
          "SaveCredentials": false,
          "RemotePath": "/_webdav/Partner/@files/RawFiles/",
          "RecentRemotePaths": ["/_webdav/Partner/@files/RawFiles/", "/_webdav/MacCoss/maccoss/@files/"],
          "TrustedRootCertificatePath": "C:\\certs\\extra.cer",
          "VerboseLogging": true,
          "MinimizeToTray": false,
          "$version": 1
        }
        """;

    [Fact]
    public async Task Everything_a_version_1_file_says_about_its_pairing_becomes_configuration_one()
    {
        // The property-by-property assertion, because a field dropped in the move is a setting
        // that silently reverts to its default on the first launch after updating -- and several
        // of these defaults are the opposite of what this file asks for.
        await File.WriteAllTextAsync(SettingsPath, Version1Settings);

        var configuration = (await new JsonSettingsStore(SettingsPath).LoadAsync())
            .OnlyConfiguration();

        configuration.LocalDirectory.ShouldBe(@"D:\Data\QE-HF");
        configuration.IncludeSubdirectories.ShouldBeFalse();
        configuration.Extensions.ShouldBe([".raw", ".mzml"]);
        configuration.ExcludedExtensions.ShouldBe([".skyd", ".blib"]);
        configuration.StabilitySeconds.ShouldBe(42);
        configuration.ReconcileMinutes.ShouldBe(7);
        configuration.LockedFileRetryIntervalSeconds.ShouldBe(11);
        configuration.LockedFileMaxRetries.ShouldBe(3);
        configuration.ConflictPolicy.ShouldBe(ConflictPolicy.Overwrite);
        configuration.VerifyUploads.ShouldBeFalse();
        configuration.WriteChecksumSidecars.ShouldBeFalse();
        configuration.ServerUrl.ShouldBe("https://labkey.partner.edu");
        configuration.AuthMode.ShouldBe(AuthMode.UserNameAndPassword);
        configuration.UserName.ShouldBe("someone@uw.edu");
        configuration.SaveCredentials.ShouldBeFalse();
        configuration.RemotePath.ShouldBe("/_webdav/Partner/@files/RawFiles/");
    }

    [Fact]
    public async Task The_settings_that_describe_the_machine_stay_where_they_were()
    {
        // The other half of the split. These never belonged to a pairing -- a TLS-inspecting
        // proxy intercepts everything leaving the machine, and the disk is as slow for one
        // configuration as for five -- so they must not have been carried into the configuration
        // and lost from the application.
        await File.WriteAllTextAsync(SettingsPath, Version1Settings);

        var settings = await new JsonSettingsStore(SettingsPath).LoadAsync();

        settings.MaxConcurrentTransfers.ShouldBe(6);
        settings.YieldToInstrumentSoftware.ShouldBeFalse();
        settings.RecordSha256.ShouldBeTrue();
        settings.TrustedRootCertificatePath.ShouldBe(@"C:\certs\extra.cer");
        settings.VerboseLogging.ShouldBeTrue();
        settings.MinimizeToTray.ShouldBeFalse();
        settings.RecentRemotePaths.ShouldBe(
            ["/_webdav/Partner/@files/RawFiles/", "/_webdav/MacCoss/maccoss/@files/"]);
    }

    [Fact]
    public async Task The_upgraded_configuration_is_enabled_and_named_for_its_folder()
    {
        // Enabled because this is what the machine was already doing: an update that quietly
        // switched monitoring off would look exactly like the application breaking. Named
        // because the Configurations tab has a name column, and "" in it is no use to anybody.
        await File.WriteAllTextAsync(SettingsPath, Version1Settings);

        var configuration = (await new JsonSettingsStore(SettingsPath).LoadAsync())
            .OnlyConfiguration();

        configuration.Enabled.ShouldBeTrue();
        configuration.Name.ShouldBe("QE-HF");

        // No creation time, because the file never recorded one. Blank is honest; the moment of
        // its own upgrade is not.
        configuration.CreatedUtc.ShouldBe(default);
    }

    [Fact]
    public async Task The_upgraded_configuration_reads_the_credential_already_on_the_machine()
    {
        // The whole of the credential migration, and the reason phase 2 needed none: an empty
        // account resolves to exactly the target name used before accounts existed, so the
        // credential this installation has been signing in with is found where it has always
        // been. Get this wrong and an update signs the instrument out, silently, until somebody
        // notices transfers have stopped.
        await File.WriteAllTextAsync(SettingsPath, Version1Settings);

        var configuration = (await new JsonSettingsStore(SettingsPath).LoadAsync())
            .OnlyConfiguration();

        configuration.Account.ShouldBeEmpty();

        WindowsCredentialStore.TargetFor(configuration.ServerUrl, configuration.Account)
            .ShouldBe(WindowsCredentialStore.TargetFor(configuration.ServerUrl));
    }

    [Fact]
    public async Task A_file_with_no_version_at_all_is_read_as_version_1()
    {
        // $version was added after the first releases, so the oldest files in the field say
        // nothing about their own format. Absent has to mean 1; treating it as current would
        // read a flat file as the new shape and drop every setting that moved.
        await File.WriteAllTextAsync(
            SettingsPath,
            """
            {
              "LocalDirectory": "D:\\Data",
              "RemotePath": "/_webdav/MacCoss/maccoss/@files/older/"
            }
            """);

        var configuration = (await new JsonSettingsStore(SettingsPath).LoadAsync())
            .OnlyConfiguration();

        configuration.LocalDirectory.ShouldBe(@"D:\Data");
        configuration.RemotePath.ShouldBe("/_webdav/MacCoss/maccoss/@files/older/");
    }

    [Fact]
    public async Task The_upgrade_is_written_back_once_and_then_stops_happening()
    {
        // Rewriting on load is how the file stops being a version 1 file. Doing it on every
        // launch would be harmless but would also mean the upgrade never actually took, which is
        // the thing worth knowing.
        await File.WriteAllTextAsync(SettingsPath, Version1Settings);

        var store = new JsonSettingsStore(SettingsPath);

        var first = await store.LoadAsync();
        var rewritten = await File.ReadAllTextAsync(SettingsPath);

        rewritten.ShouldContain("\"$version\": 2");
        rewritten.ShouldContain("Configurations");

        // Loading the rewritten file takes the ordinary path and produces the same thing. An
        // upgrade that is not a fixed point quietly changes settings on every launch.
        var second = await store.LoadAsync();

        second.ShouldBe(first);
        (await File.ReadAllTextAsync(SettingsPath)).ShouldBe(rewritten);
    }

    [Fact]
    public async Task A_read_only_settings_directory_upgrades_in_memory_rather_than_failing()
    {
        // The rewrite is a convenience, not the upgrade. A machine whose settings directory is
        // locked down by policy still has to monitor the folder its file names, so a failed
        // write must not make the settings look corrupt or fall back to defaults.
        await File.WriteAllTextAsync(SettingsPath, Version1Settings);

        // Readable but not replaceable, which is what a locked-down settings directory amounts
        // to. FileShare.Read rather than None on purpose: the load has to get as far as reading
        // the file and only then fail to write it back. Simpler and more faithful than changing
        // an ACL, and it undoes itself when the test ends.
        await using var held = new FileStream(
            SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var settings = await new JsonSettingsStore(SettingsPath).LoadAsync();

        settings.OnlyConfiguration().LocalDirectory.ShouldBe(@"D:\Data\QE-HF");
        settings.MaxConcurrentTransfers.ShouldBe(6);
    }

    [Fact]
    public async Task An_emptied_exclusion_list_is_not_mistaken_for_one_written_before_the_setting()
    {
        // Clearing the box is how somebody asks for every companion file, and it survived a
        // round trip before configurations existed. The upgrade has to keep telling the two
        // apart: refilling the defaults here would re-arm collecting the .skyd caches that the
        // person editing this file had gone out of their way to allow.
        await File.WriteAllTextAsync(
            SettingsPath,
            """
            {
              "LocalDirectory": "D:\\Data",
              "ExcludedExtensions": []
            }
            """);

        (await new JsonSettingsStore(SettingsPath).LoadAsync())
            .OnlyConfiguration()
            .ExcludedExtensions
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task A_hand_typed_null_stays_a_reportable_mistake_rather_than_becoming_the_defaults()
    {
        // null is valid JSON and reaches the record as null, the same as an absent property --
        // but they mean opposite things. Absent means the file predates the setting, so the
        // defaults apply. null is a mistake in a hand-edited file, and turning it into the
        // defaults would transfer the file types the person editing had just tried to stop.
        await File.WriteAllTextAsync(
            SettingsPath,
            """
            {
              "LocalDirectory": "D:\\Data",
              "Extensions": null,
              "ExcludedExtensions": null
            }
            """);

        var settings = await new JsonSettingsStore(SettingsPath).LoadAsync();

        settings.OnlyConfiguration().Extensions.ShouldBeEmpty();
        settings.OnlyConfiguration().ExcludedExtensions.ShouldBeEmpty();
        settings.Validate().ShouldContain(p => p.Contains("at least one file extension"));
    }

    [Fact]
    public async Task A_file_written_by_a_newer_build_is_left_alone_rather_than_stamped_down()
    {
        // What a rollback looks like. This build cannot represent whatever version 3 added, so
        // writing its own version number onto the part it did understand would hand the newer
        // build back a file quietly missing settings -- and that build has no way to tell.
        var newer =
            """
            {
              "$version": 3,
              "MaxConcurrentTransfers": 5,
              "SomethingAddedLater": { "unreadable": true },
              "Configurations": [ { "Name": "Lumos", "LocalDirectory": "D:\\Data" } ]
            }
            """;

        await File.WriteAllTextAsync(SettingsPath, newer);

        var settings = await new JsonSettingsStore(SettingsPath).LoadAsync();

        // Read as far as it can be, so the application still runs.
        settings.MaxConcurrentTransfers.ShouldBe(5);
        settings.OnlyConfiguration().Name.ShouldBe("Lumos");

        // And the file on disk is untouched.
        (await File.ReadAllTextAsync(SettingsPath)).ShouldBe(newer);
    }

    [Fact]
    public async Task A_version_1_file_is_still_readable_by_the_build_that_wrote_it_until_the_rewrite_lands()
    {
        // The rewrite goes through a temporary file and a move, so there is no moment at which
        // the settings are half of each format. Asserted as the property that matters: at every
        // point, what is on disk parses -- either as version 1 or as version 2, never as neither.
        await File.WriteAllTextAsync(SettingsPath, Version1Settings);

        var before = await File.ReadAllTextAsync(SettingsPath);
        Should.NotThrow(() => System.Text.Json.JsonDocument.Parse(before));

        await new JsonSettingsStore(SettingsPath).LoadAsync();

        var after = await File.ReadAllTextAsync(SettingsPath);
        Should.NotThrow(() => System.Text.Json.JsonDocument.Parse(after));

        Directory.GetFiles(_directory, "*.tmp").ShouldBeEmpty();
        File.Exists(SettingsPath + ".corrupt").ShouldBeFalse("nothing here is corrupt");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
