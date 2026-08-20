using PanoramaBridge.App.ViewModels;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;

namespace PanoramaBridge.Tests.App;

/// <summary>
/// The transfer grid: which block each row belongs in, and the bookkeeping that keeps them
/// in order.
/// </summary>
/// <remarks>
/// The ordering is maintained incrementally -- a row that changes state is moved rather than the
/// grid re-sorted -- because a re-sort five times a second over thousands of rows would be felt,
/// and because replacing rows resets the user's selection and scroll position. Incremental means
/// index arithmetic against a running count per block, which is exactly the kind of code that
/// goes subtly wrong and stays wrong quietly.
/// </remarks>
public sealed class TransferStatusViewModelTests : IDisposable
{
    private readonly TransferProgressAggregator _aggregator = new();
    private readonly TransferStatusViewModel _view;

    public TransferStatusViewModelTests() => _view = new TransferStatusViewModel(_aggregator);

    private void Report(string name, TransferState state, string? phase = null)
    {
        _aggregator.Report(new TransferProgress(
            @"C:\data\" + name,
            "/_webdav/uploads/" + name,
            state,
            phase ?? state.ToString(),
            0,
            1024));

        _view.Refresh();
    }

    private IReadOnlyList<string> Names() => _view.Rows.Select(r => r.FileName).ToArray();

    private IReadOnlyList<TransferBand> Bands() => _view.Rows.Select(r => r.Band).ToArray();

    [Fact]
    public void Rows_are_grouped_by_what_they_are_doing()
    {
        // Reported in an order that has nothing to do with the order they should appear in.
        Report("waiting.raw", TransferState.Discovered);
        Report("done.raw", TransferState.Verified);
        Report("moving.raw", TransferState.Uploading);
        Report("broken.raw", TransferState.Failed);

        Bands().ShouldBe(
        [
            TransferBand.Active,
            TransferBand.NeedsAttention,
            TransferBand.Finished,
            TransferBand.Waiting,
        ]);

        Names().ShouldBe(["moving.raw", "broken.raw", "done.raw", "waiting.raw"]);
    }

    [Fact]
    public void A_file_moves_down_the_grid_as_it_finishes()
    {
        // The behaviour the ordering exists for: watch the top of the list and you see what is
        // happening now; a file leaves that block at the moment it is verified.
        Report("first.raw", TransferState.Uploading);
        Report("second.raw", TransferState.Uploading);

        Names().ShouldBe(["second.raw", "first.raw"]);

        Report("first.raw", TransferState.Verified);

        Names().ShouldBe(["second.raw", "first.raw"]);
        Bands().ShouldBe([TransferBand.Active, TransferBand.Finished]);

        Report("second.raw", TransferState.Verified);

        // Most recently finished at the top of its block, under whatever is still moving.
        Names().ShouldBe(["second.raw", "first.raw"]);
    }

    [Fact]
    public void The_newest_row_in_a_block_is_at_the_top_of_it()
    {
        Report("a.raw", TransferState.Discovered);
        Report("b.raw", TransferState.Discovered);
        Report("c.raw", TransferState.Discovered);

        Names().ShouldBe(["c.raw", "b.raw", "a.raw"]);
    }

    [Fact]
    public void A_row_being_checked_before_any_bytes_move_counts_as_active()
    {
        // Queued covers the checks that run before an upload starts, and one of them can take
        // half a minute against a large destination folder. A file that is being worked on
        // belongs with the work, not with the files nothing has touched yet.
        Report("checking.raw", TransferState.Queued, phase: "Checking server");

        _view.Rows[0].Band.ShouldBe(TransferBand.Active);
        _view.Rows[0].Status.ShouldBe("Checking server", "the phase is what is happening now");
    }

    [Fact]
    public void Clearing_finished_rows_leaves_the_rest_in_order()
    {
        Report("moving.raw", TransferState.Uploading);
        Report("done.raw", TransferState.Verified);
        Report("skipped.raw", TransferState.Skipped);
        Report("broken.raw", TransferState.Failed);
        Report("waiting.raw", TransferState.Discovered);

        _view.ClearCompletedCommand.Execute(null);

        Names().ShouldBe(["moving.raw", "broken.raw", "waiting.raw"]);
        Bands().ShouldBe([TransferBand.Active, TransferBand.NeedsAttention, TransferBand.Waiting]);
    }

    [Fact]
    public void The_summary_counts_what_is_in_flight()
    {
        Report("moving.raw", TransferState.Uploading);
        Report("broken.raw", TransferState.Failed);

        _view.AttentionCount.ShouldBe(1);
        _view.HasAttentionItems.ShouldBeTrue();
        _view.Summary.ShouldContain("1 active");
    }

    [Fact]
    public void The_order_survives_any_sequence_of_state_changes()
    {
        // The bookkeeping is a running count per block and a move to the front of a block. Every
        // add, move and removal has to keep those counts agreeing with the collection, and no
        // single hand-written sequence proves that. So: a long deterministic shuffle, checking
        // the invariant after every step.
        var random = new Random(20260820);

        TransferState[] states =
        [
            TransferState.Discovered, TransferState.Queued, TransferState.Uploading,
            TransferState.Uploaded, TransferState.Verified, TransferState.Skipped,
            TransferState.Conflict, TransferState.LockedRetrying, TransferState.Superseded,
            TransferState.Failed,
        ];

        var files = Enumerable.Range(0, 40).Select(i => $"run{i:D2}.raw").ToArray();

        for (var step = 0; step < 600; step++)
        {
            Report(files[random.Next(files.Length)], states[random.Next(states.Length)]);

            var bands = Bands();
            bands.ShouldBe(bands.OrderBy(b => b).ToArray(), $"rows fell out of order at step {step}");

            _view.Rows.Select(r => r.FileName).Distinct().Count()
                .ShouldBe(_view.Rows.Count, $"a file appeared twice at step {step}");
        }

        _view.Rows.Count.ShouldBe(files.Length);
    }

    [Fact]
    public void Trimming_an_overlong_grid_never_discards_a_problem()
    {
        // The cap exists because a monitor that runs for weeks would otherwise grow a row per
        // file for ever. Dropping a failure to stay under it would be worse than the memory.
        Report("broken.raw", TransferState.Failed);

        for (var i = 0; i < 5200; i++)
        {
            Report($"bulk{i:D5}.raw", TransferState.Verified);
        }

        _view.Rows.Count.ShouldBeLessThanOrEqualTo(5000);
        Names().ShouldContain("broken.raw");
        _view.Rows[0].FileName.ShouldBe("broken.raw", "and it stays where it can be seen");
    }

    /// <inheritdoc />
    public void Dispose() => _view.Dispose();
}
