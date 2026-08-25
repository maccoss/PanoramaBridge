using PanoramaBridge.Core.Monitoring;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.Tests.Monitoring;

/// <summary>
/// Where a file goes on the server, answered once.
/// </summary>
/// <remarks>
/// Six call sites worked this out for themselves, and four separate defects came from two of them
/// working it out differently: a renamed file re-sent for ever, every finished acquisition
/// re-measured for ever, Replace destroying the copy somebody chose to preserve, and a failed
/// rename setting that destruction up again. These are those four, asked of the one type that
/// answers now.
/// </remarks>
public sealed class DestinationMapTests
{
    private static readonly RemotePath Uploads = RemotePath.Parse("/_webdav/uploads/");

    // A constant, because nothing here reads the disk: the type does string work and RemotePath
    // parsing. Creating and deleting a real directory was ceremony, and its teardown caught only
    // IOException while Windows can refuse a delete with UnauthorizedAccessException.
    private const string Root = @"C:\data";

    private static readonly DestinationMap Map = new(Root, Uploads);

    private static UploadRecord Row(string relative, bool dataset = false, string? renamedTo = null) =>
        new(
            LocalPath: Path.Combine(Root, relative),
            RemotePath: string.Empty,
            Length: 1,
            LastWriteUnixMs: 1,
            Md5: null,
            Sha256: null,
            State: TransferState.Verified,
            VerifyMethod: VerifyMethod.ServerMd5,
            VerifiedUtc: null,
            Attempts: 1,
            LastError: null,
            IsDataset: dataset,
            RawCheck: null,
            Resolution: ConflictResolution.None,
            RenameTo: renamedTo);

    [Fact]
    public void An_ordinary_file_keeps_its_own_name() =>
        Map.For(Row("run.raw")).Name.ShouldBe("run.raw");

    [Fact]
    public void A_renamed_file_keeps_the_name_it_was_sent_under()
    {
        // The sweep resolving this to run.raw while the ledger held run (2).raw is what made a
        // renamed acquisition be sent again on every pass, for ever.
        Map.For(Row("run.raw", renamedTo: "run (2).raw")).Name.ShouldBe("run (2).raw");
    }

    [Fact]
    public void An_acquisition_folder_goes_to_its_archive()
    {
        // The same mismatch: .d.zip in the ledger, .d from the sweep, so every finished Bruker,
        // Waters and Agilent acquisition was re-measured on every pass.
        Map.For(Row("250314_HeLa.d", dataset: true)).Name.ShouldBe("250314_HeLa.d.zip");
    }

    [Fact]
    public void A_renamed_acquisition_folder_keeps_the_renamed_archive()
    {
        // A rename wins over the acquisition rule, or a .d sent alongside would resolve back to
        // the archive name its folder produces and overwrite the copy already there.
        Map.For(Row("250314_HeLa.d", dataset: true, renamedTo: "250314_HeLa (2).d.zip"))
            .Name.ShouldBe("250314_HeLa (2).d.zip");
    }

    [Fact]
    public void A_blank_rename_is_no_rename_rather_than_an_empty_name()
    {
        // ResolveDestination treats whitespace as "no leaf given", so testing only for null here
        // let a blank rename short-circuit the acquisition rule and then get ignored downstream:
        // a .d resolving to its folder name, which is not where its archive lives.
        Map.For(Row("250314_HeLa.d", dataset: true, renamedTo: "")).Name
            .ShouldBe("250314_HeLa.d.zip");

        Map.For(Row("250314_HeLa.d", dataset: true, renamedTo: "   ")).Name
            .ShouldBe("250314_HeLa.d.zip");
    }

    [Fact]
    public void What_is_on_disk_decides_whether_it_is_an_acquisition()
    {
        // IsDataset is never cleared once set. A .d folder replaced by a plain .d file would
        // otherwise be sent to the archive name and land on top of the acquisition, so the engine
        // passes what it is actually holding rather than what the row remembers.
        var stale = Row("250314_HeLa.d", dataset: true);

        Map.For(stale.LocalPath, isDataset: false, stale).Name.ShouldBe("250314_HeLa.d");
    }

    [Fact]
    public void The_path_in_hand_wins_over_the_one_the_ledger_recorded()
    {
        // The ledger is NOCASE, so a row can hold a different casing from the file on disk.
        // Resolving from the row meant renaming a file's case left every later upload going to
        // the old-cased remote name.
        var row = Row("Run.RAW");

        Map.For(Path.Combine(Root, "run.raw"), isDataset: false, row).Name.ShouldBe("run.raw");
    }

    [Fact]
    public void A_name_chosen_for_a_rename_is_used_as_given() =>
        Map.Under(Path.Combine(Root, "run.raw"), "run (3).raw").Name.ShouldBe("run (3).raw");

    [Fact]
    public void The_tree_shape_is_kept_and_only_the_leaf_changes()
    {
        var nested = Map.For(Row(Path.Combine("2026-08", "run.raw"), renamedTo: "run (2).raw"));

        nested.ToEncodedString().ShouldEndWith("/2026-08/run%20%282%29.raw");
    }

}
