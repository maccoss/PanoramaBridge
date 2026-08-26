using PanoramaBridge.Core.Monitoring;

namespace PanoramaBridge.Tests.Monitoring;

/// <summary>
/// Which files the monitor considers data.
/// </summary>
/// <remarks>
/// One filter serves both the sweep and the change watcher. If they could disagree, a file would
/// arrive or not depending on whether Windows happened to deliver a notification -- a difference
/// nobody could reproduce from a bug report.
/// </remarks>
public sealed class CandidateFilterTests
{
    private static readonly CandidateFilter Instrument = new([".raw", ".wiff"]);

    [Theory]
    [InlineData(@"C:\data\run1.raw")]
    [InlineData(@"C:\data\RUN1.RAW")]
    [InlineData(@"C:\data\sample.wiff")]
    public void Files_with_a_listed_extension_are_accepted(string path) =>
        Instrument.Accepts(path).ShouldBeTrue();

    [Theory]
    [InlineData(@"C:\data\notes.txt")]
    [InlineData(@"C:\data\archive.notraw")]
    public void Anything_else_is_left_alone(string path) =>
        Instrument.Accepts(path).ShouldBeFalse();

    // -- companion files, from a real Sciex ZenoTOF 8600 acquisition ---------------------------

    [Theory]
    [InlineData("250814_ZTScan_100spd_A_1_A1.wiff", true)]
    [InlineData("250814_ZTScan_100spd_A_1_A1.wiff.scan", true)]
    [InlineData("250814_ZTScan_100spd_A_1_A1.wiff.dia", true)]
    [InlineData("250814_ZTScan_100spd_A_1_A1.wiff.dia.quant", true)]
    [InlineData("250814_ZTScan_100spd_A_1_A1.timeseries.data", false)]
    public void A_sciex_acquisition_travels_with_its_companions(string name, bool expected)
    {
        // The .wiff is metadata; the spectra are in the .wiff.scan, which is two hundred times
        // larger. Path.GetExtension sees ".scan" and used to leave it behind, so asking for
        // .wiff transferred 38 MB of a 13.7 GB acquisition -- and recorded it as verified,
        // because the one file that was sent did arrive intact.
        new CandidateFilter([".wiff"]).Accepts(name).ShouldBe(expected);
    }

    [Fact]
    public void The_sqlite_journal_beside_a_sciex_acquisition_is_not_data()
    {
        // Sciex leaves a .wiff2-journal next to every run. The extension walk reaches .wiff2 and
        // would take it; it is SQLite's working file and belongs to whatever has the database
        // open.
        new CandidateFilter([".wiff2"])
            .Accepts("250814_ZTScan_100spd_A_1_A1.wiff2-journal")
            .ShouldBeFalse();
    }

    [Fact]
    public void Our_own_checksum_sidecar_is_never_mistaken_for_data()
    {
        // run.raw.md5 reaches run.raw by the same walk. Uploading our own bookkeeping as though
        // it were an acquisition would be a loop with a straight face.
        new CandidateFilter([".raw"]).Accepts("run.raw.md5").ShouldBeFalse();
    }

    [Fact]
    public void A_companion_of_something_not_asked_for_is_still_not_taken()
    {
        // The walk must not turn into "accept anything that shares a stem".
        new CandidateFilter([".raw"]).Accepts("run.wiff.scan").ShouldBeFalse();
    }

    [Fact]
    public void An_ordinary_double_extension_is_unaffected()
    {
        new CandidateFilter([".gz"]).Accepts("results.tar.gz").ShouldBeTrue();
        new CandidateFilter([".tar"]).Accepts("results.tar.gz").ShouldBeTrue();
        new CandidateFilter([".zip"]).Accepts("results.tar.gz").ShouldBeFalse();
    }

    [Fact]
    public void A_suffix_match_is_not_good_enough()
    {
        // Path.GetExtension rather than EndsWith, so a filter of .raw does not also drag in a
        // file someone named archive.notraw.
        Instrument.Accepts(@"C:\data\archive.notraw").ShouldBeFalse();
        Instrument.Accepts(@"C:\data\archive.not.raw").ShouldBeTrue();
    }

    [Theory]
    [InlineData(@"C:\data\.hidden.raw")]
    [InlineData(@"C:\data\~partial.raw")]
    [InlineData(@"C:\data\~partial.d.zip")]
    public void Working_files_are_never_data(string path)
    {
        // Instrument software and Windows both leave these behind, and a copy in progress is
        // frequently one of them. Uploading a half-written working file is exactly what this
        // application must not do.
        Instrument.Accepts(path).ShouldBeFalse();
        CandidateFilter.Everything.Accepts(path).ShouldBeFalse();
    }

    [Fact]
    public void An_empty_extension_list_means_everything()
    {
        // What the settings screen means by leaving the box empty.
        CandidateFilter.Everything.Accepts(@"C:\data\notes.txt").ShouldBeTrue();
        CandidateFilter.Everything.Accepts(@"C:\data\run1.raw").ShouldBeTrue();
    }

    [Theory]
    [InlineData(@"C:\data\run1.raw.md5")]
    [InlineData(@"C:\data\run1.wiff2-journal")]
    [InlineData(@"C:\data\run1.wiff2-wal")]
    [InlineData(@"C:\data\run1.wiff2-shm")]
    public void An_empty_extension_list_still_leaves_the_working_files(string path)
    {
        // "Everything" means every acquisition, not every byte in the folder. This path skipped
        // the working-file rules entirely, so a user who left the box empty would have had
        // PanoramaBridge upload its own .md5 sidecars back to the server as though they were
        // data, and the SQLite journals sitting beside a run still being written with them.
        CandidateFilter.Everything.Accepts(path).ShouldBeFalse();
    }
}
