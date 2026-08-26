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

    private static UploadRecord Row(string relative, bool dataset = false) =>
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
                RawCheck: null);

    [Fact]
    public void An_ordinary_file_keeps_its_own_name() =>
        Map.For(Row("run.raw")).Name.ShouldBe("run.raw");

    [Fact]
    public void An_acquisition_folder_goes_to_its_archive()
    {
        // The same mismatch: .d.zip in the ledger, .d from the sweep, so every finished Bruker,
        // Waters and Agilent acquisition was re-measured on every pass.
        Map.For(Row("250314_HeLa.d", dataset: true)).Name.ShouldBe("250314_HeLa.d.zip");
    }

    [Fact]
    public void A_name_chosen_for_a_rename_is_used_as_given() =>
        Map.Under(Path.Combine(Root, "run.raw"), "run (3).raw").Name.ShouldBe("run (3).raw");

}
