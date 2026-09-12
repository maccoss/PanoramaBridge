using PanoramaBridge.Core.Storage;

namespace PanoramaBridge.Tests.Storage;

/// <summary>
/// Working out which configuration a ledger row belongs to.
/// </summary>
/// <remarks>
/// The Uploads table shows one list across every configuration, per the lab's own request, so
/// each row has to say which one put it there. Derived from the paths rather than stored, because
/// the ledger already records where a file came from and where it went, and a stored name would
/// go stale the moment somebody renamed a configuration.
/// </remarks>
public sealed class ConfigurationLookupTests
{
    private static MonitoringConfiguration Watching(
        string name,
        string local,
        string remote) => new()
        {
            Name = name,
            LocalDirectory = local,
            RemotePath = remote,
        };

    [Fact]
    public void A_file_is_matched_to_the_configuration_watching_its_folder()
    {
        var configurations = new[]
        {
            Watching("Lumos", @"D:\Data\Lumos", "/_webdav/MacCoss/Lumos/@files/"),
            Watching("Exploris", @"D:\Data\Exploris", "/_webdav/MacCoss/Exploris/@files/"),
        };

        ConfigurationLookup
            .NameFor(configurations, @"D:\Data\Exploris\run.raw", "/_webdav/MacCoss/Exploris/@files/run.raw")
            .ShouldBe("Exploris");
    }

    [Fact]
    public void Two_configurations_watching_one_folder_are_told_apart_by_the_destination()
    {
        // The case the lab asked for and the reason the ledger key was widened. The local path
        // is identical, so only the destination can answer.
        var configurations = new[]
        {
            Watching("To QC", @"D:\Data", "/_webdav/MacCoss/QC/@files/"),
            Watching("To the project", @"D:\Data", "/_webdav/MacCoss/Project/@files/"),
        };

        ConfigurationLookup
            .NameFor(configurations, @"D:\Data\run.raw", "/_webdav/MacCoss/Project/@files/run.raw")
            .ShouldBe("To the project");
    }

    [Fact]
    public void The_most_specific_watched_folder_wins()
    {
        // One configuration watching a tree and another watching a folder inside it both contain
        // the file. The answer a person would give is the one that named the folder it is in.
        var configurations = new[]
        {
            Watching("Everything", @"D:\Data", "/_webdav/MacCoss/all/@files/"),
            Watching("Just QC", @"D:\Data\QC", "/_webdav/MacCoss/all/@files/"),
        };

        ConfigurationLookup
            .NameFor(configurations, @"D:\Data\QC\run.raw", "/_webdav/MacCoss/all/@files/QC/run.raw")
            .ShouldBe("Just QC");
    }

    [Fact]
    public void A_folder_that_merely_starts_with_the_same_letters_is_not_a_match()
    {
        // A plain prefix test would put everything in D:\DataArchive inside a configuration
        // watching D:\Data, and the column would name the wrong instrument.
        var configurations = new[]
        {
            Watching("Lumos", @"D:\Data", "/_webdav/MacCoss/Lumos/@files/"),
        };

        ConfigurationLookup
            .NameFor(configurations, @"D:\DataArchive\run.raw", "/_webdav/MacCoss/Lumos/@files/run.raw")
            .ShouldBeEmpty();
    }

    [Fact]
    public void A_remote_folder_that_merely_starts_with_the_same_letters_is_not_a_match()
    {
        var configurations = new[]
        {
            Watching("Lumos", @"D:\Data", "/_webdav/MacCoss/QC/@files/"),
        };

        ConfigurationLookup
            .NameFor(configurations, @"D:\Data\run.raw", "/_webdav/MacCoss/QCArchive/@files/run.raw")
            .ShouldBeEmpty();
    }

    [Fact]
    public void The_local_half_is_matched_without_regard_to_case_and_the_remote_half_exactly()
    {
        // Windows is case-insensitive and Panorama is not, which is the same split the ledger
        // itself makes: local_path is NOCASE and remote_path is not.
        var configurations = new[]
        {
            Watching("Lumos", @"D:\Data", "/_webdav/MacCoss/QC/@files/"),
        };

        ConfigurationLookup
            .NameFor(configurations, @"d:\DATA\run.raw", "/_webdav/MacCoss/QC/@files/run.raw")
            .ShouldBe("Lumos");

        ConfigurationLookup
            .NameFor(configurations, @"D:\Data\run.raw", "/_webdav/maccoss/qc/@files/run.raw")
            .ShouldBeEmpty("a server that distinguishes case means these are different folders");
    }

    [Fact]
    public void A_trailing_separator_on_the_watched_folder_makes_no_difference()
    {
        // Which it has depends on whether somebody browsed to it or typed it.
        var configurations = new[]
        {
            Watching("Lumos", @"D:\Data\", "/_webdav/MacCoss/QC/@files/"),
        };

        ConfigurationLookup
            .NameFor(configurations, @"D:\Data\run.raw", "/_webdav/MacCoss/QC/@files/run.raw")
            .ShouldBe("Lumos");
    }

    [Fact]
    public void A_failure_recorded_before_a_destination_was_known_still_finds_its_configuration()
    {
        // Those rows carry an empty destination, which is a real key rather than an error. The
        // local half is all there is to go on, and it is enough.
        var configurations = new[]
        {
            Watching("Lumos", @"D:\Data", "/_webdav/MacCoss/QC/@files/"),
        };

        ConfigurationLookup
            .NameFor(configurations, @"D:\Data\run.raw", string.Empty)
            .ShouldBe("Lumos");
    }

    [Fact]
    public void A_row_no_configuration_claims_gets_no_name_rather_than_a_wrong_one()
    {
        // A file transferred by a configuration since deleted is still a true record of an
        // upload. A blank says so; borrowing a neighboring configuration's name would not.
        ConfigurationLookup
            .NameFor([], @"D:\Data\run.raw", "/_webdav/MacCoss/QC/@files/run.raw")
            .ShouldBeEmpty();
    }

    [Fact]
    public void A_configuration_with_no_folder_yet_claims_nothing()
    {
        // A freshly added configuration, before anyone has chosen a directory. An empty prefix
        // matches every path, so without this it would claim every row in the table.
        var configurations = new[]
        {
            Watching("Brand new", string.Empty, AppSettings.MacCossFilesPath),
        };

        ConfigurationLookup
            .NameFor(configurations, @"D:\Data\run.raw", AppSettings.MacCossFilesPath + "run.raw")
            .ShouldBeEmpty();
    }
}
