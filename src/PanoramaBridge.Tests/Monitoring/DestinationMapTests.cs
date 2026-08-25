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
public sealed class DestinationMapTests : IDisposable
{
    private static readonly RemotePath Uploads = RemotePath.Parse("/_webdav/uploads/");

    private readonly string _root = Directory.CreateTempSubdirectory("pb-dest-").FullName;

    private DestinationMap Map => new(_root, Uploads);

    private UploadRecord Row(string relative, bool dataset = false, string? renamedTo = null) =>
        new(
            LocalPath: Path.Combine(_root, relative),
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
    public void The_sweep_and_the_engine_cannot_disagree()
    {
        // The property the whole type exists for. Two maps built from the same two values give
        // the same answer for the same row, whatever kind of row it is -- which is what the sweep
        // and the engine each hold.
        var sweep = new DestinationMap(_root, Uploads);
        var engine = new DestinationMap(_root, Uploads);

        UploadRecord[] rows =
        [
            Row("run.raw"),
            Row("run.raw", renamedTo: "run (2).raw"),
            Row("250314_HeLa.d", dataset: true),
            Row("250314_HeLa.d", dataset: true, renamedTo: "250314_HeLa (2).d.zip"),
        ];

        foreach (var row in rows)
        {
            sweep.For(row).ToEncodedString().ShouldBe(engine.For(row).ToEncodedString());
        }
    }

    [Fact]
    public void A_name_chosen_for_a_rename_is_used_as_given() =>
        Map.Under(Path.Combine(_root, "run.raw"), "run (3).raw").Name.ShouldBe("run (3).raw");

    [Fact]
    public void The_tree_shape_is_kept_and_only_the_leaf_changes()
    {
        var nested = Map.For(Row(Path.Combine("2026-08", "run.raw"), renamedTo: "run (2).raw"));

        nested.ToEncodedString().ShouldEndWith("/2026-08/run%20%282%29.raw");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
