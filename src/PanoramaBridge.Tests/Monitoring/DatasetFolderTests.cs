using PanoramaBridge.Core.Monitoring;

namespace PanoramaBridge.Tests.Monitoring;

/// <summary>
/// Recognising a directory acquisition, measuring it, and deciding when it has finished.
/// </summary>
/// <remarks>
/// The rule these exist to protect is the same one that governs files: nothing partial may be
/// transferred. It is harder to satisfy for a folder, because a folder can look finished between
/// one file being closed and the next being created, and because an empty one looks finished
/// from the outside while containing nothing at all.
/// </remarks>
public sealed class DatasetFolderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pb-dataset-").FullName;

    private static readonly CandidateFilter Bruker = new([".d"]);

    private string NewFolder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Write(string folder, string name, string content) =>
        File.WriteAllText(Path.Combine(folder, name), content);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A held handle on a Windows agent is not a test failure.
        }
    }

    // -- recognising one ---------------------------------------------------------------------

    [Fact]
    public void A_directory_with_a_matching_extension_is_an_acquisition()
    {
        var folder = NewFolder("run_001.d");

        DatasetFolder.Is(folder, Bruker).ShouldBeTrue();
    }

    [Fact]
    public void A_file_with_the_same_extension_is_not()
    {
        // Waters writes .raw as a folder and Thermo writes it as a file. They share an extension
        // and are nothing alike, so being a directory is the whole distinction.
        var file = Path.Combine(_root, "notafolder.d");
        File.WriteAllText(file, "this is a file");

        DatasetFolder.Is(file, Bruker).ShouldBeFalse();
    }

    [Fact]
    public void A_directory_the_user_did_not_ask_for_is_not()
    {
        var folder = NewFolder("results.qc");

        DatasetFolder.Is(folder, Bruker).ShouldBeFalse();
    }

    [Fact]
    public void The_archive_keeps_the_whole_folder_name()
    {
        // run.d.zip, not run.zip: the vendor and the original name both survive, and it matches
        // what is already stored on Panorama.
        DatasetFolder.ArchiveNameFor(@"C:\data\250314_HeLa_DIA_01.d")
            .ShouldBe("250314_HeLa_DIA_01.d.zip");
    }

    [Fact]
    public void A_trailing_separator_does_not_change_the_archive_name()
    {
        DatasetFolder.ArchiveNameFor(@"C:\data\run.d\").ShouldBe("run.d.zip");
    }

    // -- measuring one -----------------------------------------------------------------------

    [Fact]
    public void Measuring_counts_every_file_at_any_depth()
    {
        var folder = NewFolder("deep.d");
        Write(folder, "analysis.tdf", "aaaa");
        Directory.CreateDirectory(Path.Combine(folder, "inner"));
        Write(Path.Combine(folder, "inner"), "more.bin", "bbbbbb");

        var stamp = DatasetFolder.Measure(folder);

        stamp.ShouldNotBeNull();
        stamp!.Value.FileCount.ShouldBe(2);
        stamp.Value.TotalBytes.ShouldBe(10);
        stamp.Value.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void Measuring_something_that_is_not_there_says_so()
    {
        DatasetFolder.Measure(Path.Combine(_root, "absent.d")).ShouldBeNull();
    }

    [Fact]
    public void An_empty_folder_measures_as_empty_rather_than_missing()
    {
        var stamp = DatasetFolder.Measure(NewFolder("empty.d"));

        stamp.ShouldNotBeNull();
        stamp!.Value.IsEmpty.ShouldBeTrue();
    }

    // -- deciding it has finished -------------------------------------------------------------

    [Fact]
    public void One_look_is_never_enough()
    {
        // The same rule as for a file. A single observation cannot tell a finished acquisition
        // from a pause between two of its files.
        var folder = NewFolder("settling.d");
        Write(folder, "analysis.tdf", "data");

        var tracker = new DatasetStabilityTracker(TimeSpan.Zero);

        tracker.Check(folder).IsReady.ShouldBeFalse();
    }

    [Fact]
    public void A_folder_that_has_stopped_changing_is_ready()
    {
        var folder = NewFolder("finished.d");
        Write(folder, "analysis.tdf", "data");

        var now = DateTimeOffset.UtcNow;
        var tracker = new DatasetStabilityTracker(TimeSpan.FromSeconds(10), () => now);

        tracker.Check(folder).IsReady.ShouldBeFalse("first sighting");

        now = now.AddSeconds(11);

        var readiness = tracker.Check(folder);
        readiness.IsReady.ShouldBeTrue();

        // What it settled at travels in the answer, which describes the folder that was asked
        // about. "data" is four bytes.
        readiness.Length.ShouldBe(4);
    }

    [Fact]
    public void A_file_appearing_restarts_the_clock()
    {
        // The case a single total would miss: Bruker finishes the files in a .d at different
        // moments, so the folder can be quiet and still be growing.
        var folder = NewFolder("growing.d");
        Write(folder, "analysis.tdf", "data");

        var now = DateTimeOffset.UtcNow;
        var tracker = new DatasetStabilityTracker(TimeSpan.FromSeconds(10), () => now);

        tracker.Check(folder);
        now = now.AddSeconds(11);

        Write(folder, "analysis.tdf_bin", "more");
        tracker.Check(folder).IsReady.ShouldBeFalse("something was added, so it is not finished");

        now = now.AddSeconds(11);
        tracker.Check(folder).IsReady.ShouldBeTrue("and now it has settled again");
    }

    [Fact]
    public void An_empty_folder_is_never_ready()
    {
        // A .d that has been created but not written to is quiet, unlocked, and would produce a
        // perfectly valid archive of nothing at all.
        var folder = NewFolder("nothing.d");

        var now = DateTimeOffset.UtcNow;
        var tracker = new DatasetStabilityTracker(TimeSpan.FromSeconds(10), () => now);

        tracker.Check(folder);
        now = now.AddSeconds(600);

        tracker.Check(folder).IsReady.ShouldBeFalse("there is nothing in it to transfer");
    }

    [Fact]
    public void A_folder_with_a_file_still_open_for_writing_is_not_ready()
    {
        // What an instrument mid-acquisition looks like: quiet for long enough, and still being
        // written to. Size alone would have released this one.
        var folder = NewFolder("acquiring.d");
        var file = Path.Combine(folder, "analysis.tdf");
        File.WriteAllText(file, "data so far");

        var now = DateTimeOffset.UtcNow;
        var tracker = new DatasetStabilityTracker(TimeSpan.FromSeconds(10), () => now);

        tracker.Check(folder);
        now = now.AddSeconds(11);

        using (new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            tracker.Check(folder).IsReady.ShouldBeFalse("an instrument still holds it");
        }

        tracker.Check(folder).IsReady.ShouldBeTrue("and once released, it is finished");
    }

    [Fact]
    public void A_folder_that_disappears_stops_being_tracked()
    {
        var folder = NewFolder("vanishing.d");
        Write(folder, "analysis.tdf", "data");

        var tracker = new DatasetStabilityTracker(TimeSpan.Zero);
        tracker.Check(folder);
        tracker.Count.ShouldBe(1);

        Directory.Delete(folder, recursive: true);

        tracker.Check(folder).Reason.ShouldBe(ReadinessReason.Missing);
        tracker.Count.ShouldBe(0, "nothing improves by asking again");
    }
}
