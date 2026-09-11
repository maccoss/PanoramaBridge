using PanoramaBridge.App.ViewModels;
using PanoramaBridge.Core.Infrastructure;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;

// Aliased: unqualified Cli.Program binds to the test project's own Tests.Cli namespace.
using CliProgram = PanoramaBridge.Cli.Program;

namespace PanoramaBridge.Tests.App;

/// <summary>
/// Every surface that shows a byte count agrees with every other one.
/// </summary>
/// <remarks>
/// There were five private copies of the same unit loop before there was one, and they had begun
/// to drift: the throughput on a transfer row stopped at GB where the rest went to TB, and wrote
/// a sub-kilobyte value with a decimal the others did not use.
/// <para>
/// Drift is the failure this guards, not arithmetic. A number is wrong in a way nobody reports
/// when the audit table says one thing about a file and the transfer line says another, so these
/// assert agreement rather than any particular string.
/// </para>
/// </remarks>
public sealed class SizeDisplayAgreementTests
{
    private static UploadRowViewModel Row(long length) =>
        new(new UploadRecord(
            LocalPath: @"C:\data\run.raw",
            RemotePath: "/_webdav/uploads/run.raw",
            Length: length,
            LastWriteUnixMs: 0,
            Md5: null,
            Sha256: null,
            State: TransferState.Verified,
            VerifyMethod: VerifyMethod.ServerMd5,
            VerifiedUtc: null,
            Attempts: 1,
            LastError: null));

    [Theory]
    [InlineData(0)]
    [InlineData(512)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1048576)]
    [InlineData(7_323_298_011)]
    [InlineData(1024L * 1024 * 1024 * 1024)]
    public void The_uploads_table_agrees_with_every_other_display(long length)
    {
        // What the review caught: this column had its own copy, so a change to units or rounding
        // anywhere else would have left the audit view quietly disagreeing with the transfer and
        // console displays about the same file.
        Row(length).Size.ShouldBe(ByteSize.Describe(length));
    }

    [Fact]
    public void The_console_agrees_with_the_window()
    {
        // pbctl is a separate binary, so nothing but a shared implementation keeps these equal.
        foreach (var length in new long[] { 0, 512, 1024, 1536, 1048576, 7_323_298_011 })
        {
            CliProgram.FormatBytes(length).ShouldBe(ByteSize.Describe(length));
            Row(length).Size.ShouldBe(CliProgram.FormatBytes(length));
        }
    }
}
