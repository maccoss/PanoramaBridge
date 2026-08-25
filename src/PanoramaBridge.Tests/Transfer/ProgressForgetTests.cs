using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;

namespace PanoramaBridge.Tests.Transfer;

/// <summary>
/// Dropping one path from the progress view.
/// </summary>
/// <remarks>
/// Added because the resolve buttons drop a path per file, and the same dictionary answers
/// "is a transfer in flight?" for the update and tray-exit guards. Getting this wrong lets the
/// application restart in the middle of an upload.
/// </remarks>
public sealed class ProgressForgetTests
{
    private static TransferProgress At(string path, TransferState state) =>
        new(path, "/_webdav/uploads/" + path, state, "phase", 0, 100);

    [Fact]
    public void A_held_file_is_dropped_and_announced()
    {
        var aggregator = new TransferProgressAggregator();
        aggregator.Report(At("a.raw", TransferState.Conflict));

        var dropped = new List<string>();
        aggregator.Dropped += dropped.Add;

        aggregator.Forget("a.raw").ShouldBeTrue();

        dropped.ShouldBe(["a.raw"]);
        aggregator.Snapshot().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(TransferState.Uploading)]
    [InlineData(TransferState.Uploaded)]
    [InlineData(TransferState.Queued)]
    public void A_transfer_that_has_started_is_never_dropped(TransferState state)
    {
        // The window is narrow -- between the ledger being written and the drop being invoked,
        // the sweep can pick the file up -- but the consequence is not. HasTransferInFlight reads
        // this dictionary, so dropping a live entry tells the update prompt and the tray Exit that
        // nothing is moving, and the process restarts mid-upload.
        var aggregator = new TransferProgressAggregator();
        aggregator.Report(At("a.raw", state));

        var dropped = new List<string>();
        aggregator.Dropped += dropped.Add;

        aggregator.Forget("a.raw").ShouldBeFalse();

        dropped.ShouldBeEmpty();
        aggregator.Snapshot().Count.ShouldBe(1);
    }

    [Fact]
    public void A_report_that_arrives_first_is_kept_rather_than_discarded()
    {
        // Compare-and-remove: the entry read is the entry removed. Removing by key alone threw
        // away whatever had been written in between, and left the path queued for redraw with
        // nothing to redraw from.
        var aggregator = new TransferProgressAggregator();
        aggregator.Report(At("a.raw", TransferState.Conflict));

        aggregator.Report(At("a.raw", TransferState.Uploading));

        aggregator.Forget("a.raw").ShouldBeFalse();
        aggregator.Snapshot().Single().State.ShouldBe(TransferState.Uploading);
    }

    [Fact]
    public void Forgetting_something_that_was_never_reported_says_so()
    {
        var aggregator = new TransferProgressAggregator();

        var dropped = new List<string>();
        aggregator.Dropped += dropped.Add;

        aggregator.Forget("absent.raw").ShouldBeFalse();
        dropped.ShouldBeEmpty();
    }
}
