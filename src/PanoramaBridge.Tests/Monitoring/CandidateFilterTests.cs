using PanoramaBridge.Core.Monitoring;
using PanoramaBridge.Core.Storage;

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

    // -- derived output written beside an acquisition -------------------------------------------

    [Theory]
    [InlineData("QC_2026_09_08.raw", true)]
    [InlineData("QC_2026_09_08.raw.skyd", false)]
    [InlineData("QC_2026_09_08.raw.skyd.tmp", false)]
    [InlineData("QC_2026_09_08.raw.tmp", false)]
    public void Skyline_output_beside_an_acquisition_is_not_data(string name, bool expected)
    {
        // AutoQC runs on the instrument computer, imports each acquisition into Skyline as it
        // appears, and leaves the chromatogram cache beside it as run.raw.skyd. The extension
        // walk reaches .raw and took it -- so a cache that can run to gigabytes, rewritten every
        // time AutoQC re-imports, was uploaded as though it were an acquisition.
        //
        // Constructed with no exclusion list, so this is the out-of-the-box behavior rather
        // than something a user has to know to configure.
        new CandidateFilter([".raw"]).Accepts(name).ShouldBe(expected);
    }

    [Fact]
    public void The_walk_stops_at_an_excluded_extension_however_deeply_buried()
    {
        // Checking only the last extension would let run.raw.skyd.gz through: .gz is not
        // excluded, and the walk would carry on past .skyd to .raw.
        new CandidateFilter([".raw"], [".skyd"])
            .Accepts("run.raw.skyd.gz")
            .ShouldBeFalse();
    }

    [Fact]
    public void An_exclusion_only_bites_where_it_is_an_extension()
    {
        // Excluding .skyd must not reject a file that merely contains the letters.
        var filter = new CandidateFilter([".raw"], [".skyd"]);

        filter.Accepts("skyd_calibration.raw").ShouldBeTrue();
        filter.Accepts("run.notskyd.raw").ShouldBeTrue();
    }

    [Fact]
    public void What_was_asked_for_is_sent_even_if_it_is_also_excluded()
    {
        // The exclusion list exists to stop the companion walk over-reaching, not to veto a
        // choice someone typed. Somebody who deliberately asks for .skyd gets .skyd; leaving it
        // in both boxes and then finding an empty queue would be the worse surprise.
        new CandidateFilter([".raw", ".skyd"], [".skyd"]).Accepts("run.raw.skyd").ShouldBeTrue();
    }

    [Fact]
    public void Exclusions_apply_when_the_extension_list_is_empty()
    {
        // "Everything" means every acquisition, not every byte in the folder -- and an
        // instrument computer running AutoQC has plenty of the latter.
        CandidateFilter.Everything.Accepts(@"C:\data\run.raw.skyd").ShouldBeFalse();
        CandidateFilter.Everything.Accepts(@"C:\data\run.raw").ShouldBeTrue();
    }

    [Fact]
    public void The_exclusion_list_is_the_users_to_extend()
    {
        // The defaults cover what is known to sit beside an acquisition today. The point of the
        // setting is that the next tool to do it does not need a new release.
        new CandidateFilter([".raw"], [".blib"]).Accepts("run.raw.blib").ShouldBeFalse();
        new CandidateFilter([".raw"]).Accepts("run.raw.blib").ShouldBeTrue();
    }

    [Fact]
    public void An_empty_exclusion_list_is_respected()
    {
        // Clearing the box means excluding nothing, not falling back to the defaults. A user who
        // wants the old behavior has to be able to get it.
        new CandidateFilter([".raw"], []).Accepts("run.raw.skyd").ShouldBeTrue();
    }

    [Fact]
    public void Emptying_the_exclusion_list_cannot_re_arm_uploading_a_partial_file()
    {
        // .tmp was briefly in the default exclusion list, which put the one rule this
        // application must never break somewhere a user can delete it: clearing the box to get
        // every companion back also re-armed uploading a half-written acquisition. It is one of
        // IsWorkingFile's own rules now, alongside the .md5 sidecar and SQLite's journals.
        new CandidateFilter([".raw"], []).Accepts("QC.raw.tmp").ShouldBeFalse();
        new CandidateFilter([".raw"]).Accepts("QC.raw.tmp").ShouldBeFalse();
        CandidateFilter.Everything.Accepts("QC.raw.tmp").ShouldBeFalse();
    }

    [Fact]
    public void A_finished_file_is_not_rejected_for_reading_like_a_temporary_one()
    {
        // The .tmp rule matches the end of the whole name, not one segment of the walk. An mzML
        // whose stem happens to end in .tmp is a finished mzML.
        new CandidateFilter([".mzml"]).Accepts("QC.tmp.mzML").ShouldBeTrue();
        CandidateFilter.Everything.Accepts("QC.tmp.mzML").ShouldBeTrue();
    }

    [Fact]
    public void An_empty_extension_list_judges_a_file_by_its_own_extension()
    {
        // With nothing to match against, the walk has no reason to stop, so it used to test
        // every segment of the name on the way down and reject a finished file whose stem merely
        // read like an excluded one. backup.tmp.zip is a zip; QC.skyd.mzML is an mzML.
        CandidateFilter.Everything.Accepts("backup.tmp.zip").ShouldBeTrue();
        CandidateFilter.Everything.Accepts("QC.skyd.mzML").ShouldBeTrue();

        // While the file whose actual extension is excluded is still refused.
        CandidateFilter.Everything.Accepts("QC.raw.skyd").ShouldBeFalse();
    }

    [Fact]
    public void The_filter_and_the_settings_screen_agree_on_the_defaults()
    {
        // Two defaults that can drift are worse than one in the wrong place: the screen would
        // show a list the filter does not use. AppSettings owns it; the filter falls back to it.
        new CandidateFilter([".raw"]).Exclusions
            .ShouldBe(MonitoringConfiguration.DefaultExcludedExtensions, ignoreOrder: true);
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
