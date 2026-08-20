using PanoramaBridge.Core.Hashing;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;
using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.Tests.Transfer;

/// <summary>
/// The checksum file written beside each upload.
/// </summary>
/// <remarks>
/// Its whole purpose is to be readable by someone who has the data and nothing else -- no
/// PanoramaBridge, no ledger, possibly no idea what wrote it. So the format is the one every
/// checksum tool already understands, and the tests are about that rather than about anything
/// this application does with it.
/// </remarks>
public sealed class ChecksumSidecarTests
{
    private static readonly ContentHashes Hashes =
        new("d41d8cd98f00b204e9800998ecf8427e", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");

    private static readonly DateTimeOffset Acquired =
        new(2025, 5, 19, 14, 32, 10, TimeSpan.Zero);

    private static readonly DateTimeOffset Uploaded =
        new(2026, 8, 20, 7, 30, 40, TimeSpan.Zero);

    private static string Render(ContentHashes? hashes = null) =>
        ChecksumSidecar.Render(
            "run_013.raw",
            hashes ?? Hashes,
            7_323_298_011,
            Acquired,
            Uploaded,
            "PanoramaBridge/26.1.0");

    [Fact]
    public void The_first_line_is_exactly_what_md5sum_writes()
    {
        // So that "md5sum -c run_013.raw.md5" works with no explanation and no tooling.
        var first = Render().Split('\n')[0];

        first.ShouldBe("d41d8cd98f00b204e9800998ecf8427e  run_013.raw");
        first.ShouldContain("  ", Case.Sensitive);
    }

    [Fact]
    public void Everything_else_is_a_comment_so_the_format_still_parses()
    {
        var lines = Render().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Skip(1).ShouldAllBe(line => line.StartsWith('#'));
    }

    [Fact]
    public void The_acquisition_date_is_recorded_because_the_server_cannot_keep_it()
    {
        // Panorama stamps an uploaded file with the time it arrived and refuses PROPPATCH, so
        // this file is the only place the date the instrument wrote the data survives.
        var text = Render();

        text.ShouldContain("# acquired  2025-05-19T14:32:10Z");
        text.ShouldContain("# uploaded  2026-08-20T07:30:40Z");
    }

    [Fact]
    public void The_stronger_hash_is_included_only_when_there_is_one()
    {
        Render().ShouldContain("# sha256    e3b0c442");

        Render(new ContentHashes(Hashes.Md5, null))
            .ShouldNotContain("sha256", Case.Insensitive);
    }

    [Fact]
    public void The_sidecar_sits_next_to_the_file_it_describes()
    {
        var uploaded = RemotePath.Parse("/_webdav/MacCoss/maccoss/@files/uploads/run_013.raw");

        ChecksumSidecar.PathFor(uploaded).ToEncodedString()
            .ShouldBe("/_webdav/MacCoss/maccoss/@files/uploads/run_013.raw.md5");

        ChecksumSidecar.IsSidecar("run_013.raw.md5").ShouldBeTrue();
        ChecksumSidecar.IsSidecar("run_013.raw").ShouldBeFalse();
    }

    [Fact]
    public void The_acquisition_time_comes_from_the_stamp_the_ledger_already_keeps()
    {
        var stamp = new LocalFileStamp(@"C:\data\run_013.raw", 10, Acquired.ToUnixTimeMilliseconds());

        ChecksumSidecar.AcquiredFrom(stamp).ShouldBe(Acquired);
    }
}
