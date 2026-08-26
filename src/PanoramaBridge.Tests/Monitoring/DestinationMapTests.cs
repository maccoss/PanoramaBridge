using PanoramaBridge.Core.Monitoring;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.Tests.Monitoring;

/// <summary>
/// Where a file goes on the server, answered once.
/// </summary>
public sealed class DestinationMapTests
{
    private static readonly RemotePath Uploads = RemotePath.Parse("/_webdav/uploads/");

    // A constant, because nothing here reads the disk: the type does string work and RemotePath
    // parsing. Creating and deleting a real directory was ceremony, and its teardown caught only
    // IOException while Windows can refuse a delete with UnauthorizedAccessException.
    private const string Root = @"C:\data";

    private static readonly DestinationMap Map = new(Root, Uploads);

    private static UploadRecord Row(string relative) =>
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
            RawCheck: null);

    [Fact]
    public void An_ordinary_file_keeps_its_own_name() =>
        Map.For(Row("run.raw")).Name.ShouldBe("run.raw");

}
