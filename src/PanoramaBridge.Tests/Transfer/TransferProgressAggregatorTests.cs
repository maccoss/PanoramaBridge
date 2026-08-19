using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;

namespace PanoramaBridge.Tests.Transfer;

/// <summary>
/// The aggregator is what stops the UI having to keep up with the engine, so the property that
/// matters most is collapsing: many updates for one file must cost one redraw, not many.
/// </summary>
public sealed class TransferProgressAggregatorTests
{
    private static TransferProgress Progress(
        string path,
        TransferState state = TransferState.Uploading,
        long transferred = 0,
        long total = 100,
        double rate = 0) =>
        new(path, "/remote/" + Path.GetFileName(path), state, state.ToString(),
            transferred, total, rate);

    [Fact]
    public void Repeated_updates_for_one_file_collapse_into_a_single_change()
    {
        // Three concurrent uploads at one-mebibyte granularity produce thousands of updates a
        // second. Without collapsing, the UI is asked to redraw once per mebibyte transferred.
        var aggregator = new TransferProgressAggregator();

        for (var i = 1; i <= 5000; i++)
        {
            aggregator.Report(Progress(@"C:\Data\big.raw", transferred: i, total: 5000));
        }

        var changed = aggregator.DrainChanged();

        changed.Count.ShouldBe(1);
        changed[0].BytesTransferred.ShouldBe(5000, "the newest update wins");
    }

    [Fact]
    public void Each_changed_file_appears_once_per_drain()
    {
        var aggregator = new TransferProgressAggregator();

        for (var i = 0; i < 20; i++)
        {
            aggregator.Report(Progress($@"C:\Data\file{i}.raw", transferred: 1));
            aggregator.Report(Progress($@"C:\Data\file{i}.raw", transferred: 2));
        }

        aggregator.DrainChanged().Count.ShouldBe(20);
    }

    [Fact]
    public void A_drain_with_nothing_new_returns_nothing()
    {
        // The UI timer fires several times a second whether or not anything moved, so the quiet
        // case has to be free.
        var aggregator = new TransferProgressAggregator();
        aggregator.Report(Progress(@"C:\Data\a.raw"));

        aggregator.DrainChanged().Count.ShouldBe(1);
        aggregator.DrainChanged().ShouldBeEmpty();
        aggregator.DrainChanged().ShouldBeEmpty();
    }

    [Fact]
    public void An_update_arriving_during_a_drain_is_not_lost()
    {
        // The dirty marker is cleared before the value is read, so a worker reporting in that
        // window re-marks the file and the update surfaces on the next drain rather than
        // vanishing.
        var aggregator = new TransferProgressAggregator();
        aggregator.Report(Progress(@"C:\Data\a.raw", transferred: 1));

        aggregator.DrainChanged();
        aggregator.Report(Progress(@"C:\Data\a.raw", transferred: 2));

        aggregator.DrainChanged().ShouldHaveSingleItem().BytesTransferred.ShouldBe(2);
    }

    [Fact]
    public async Task Reporting_from_many_threads_loses_nothing()
    {
        var aggregator = new TransferProgressAggregator();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                aggregator.Report(Progress($@"C:\Data\worker{worker}-file{i % 25}.raw"));
            }
        })));

        // Eight workers over twenty-five file names each.
        aggregator.Count.ShouldBe(8 * 25);
        aggregator.DrainChanged().Count.ShouldBe(8 * 25);
    }

    [Fact]
    public void Totals_separate_what_is_moving_from_what_needs_a_person()
    {
        var aggregator = new TransferProgressAggregator();

        aggregator.Report(Progress(@"C:\a.raw", TransferState.Uploading, 50, 100, rate: 1_000_000));
        aggregator.Report(Progress(@"C:\b.raw", TransferState.Uploading, 25, 100, rate: 500_000));
        aggregator.Report(Progress(@"C:\c.raw", TransferState.Queued, 0, 200));
        aggregator.Report(Progress(@"C:\d.raw", TransferState.Verified, 100, 100));
        aggregator.Report(Progress(@"C:\e.raw", TransferState.Skipped, 10, 10));
        aggregator.Report(Progress(@"C:\f.raw", TransferState.Failed));
        aggregator.Report(Progress(@"C:\g.raw", TransferState.Conflict));

        var totals = aggregator.Totals();

        totals.Active.ShouldBe(2);
        totals.Queued.ShouldBe(1);
        totals.Finished.ShouldBe(2);
        totals.NeedsAttention.ShouldBe(2);
        totals.BytesTransferred.ShouldBe(75);
        totals.BytesTotal.ShouldBe(400);
        totals.BytesPerSecond.ShouldBe(1_500_000);
    }

    [Fact]
    public void Totals_describe_themselves_for_the_status_bar()
    {
        var aggregator = new TransferProgressAggregator();
        aggregator.Report(Progress(@"C:\a.raw", TransferState.Uploading, 50, 100, rate: 10_485_760));
        aggregator.Report(Progress(@"C:\b.raw", TransferState.Queued, 0, 100));

        var description = aggregator.Totals().Describe();

        description.ShouldContain("1 active");
        description.ShouldContain("1 queued");
        description.ShouldContain("10.0 MB/s");
    }

    [Fact]
    public void An_idle_engine_says_so_and_still_mentions_anything_unresolved()
    {
        new TransferProgressAggregator().Totals().Describe().ShouldBe("Idle");

        var withFailure = new TransferProgressAggregator();
        withFailure.Report(Progress(@"C:\a.raw", TransferState.Failed));

        withFailure.Totals().Describe().ShouldBe("Idle - 1 need attention");
    }

    [Fact]
    public void Overall_progress_covers_only_the_work_in_flight()
    {
        // This drives the taskbar indicator, so it has to reflect what is left to do rather than
        // everything ever transferred in the session.
        var aggregator = new TransferProgressAggregator();
        aggregator.Report(Progress(@"C:\a.raw", TransferState.Uploading, 30, 100));
        aggregator.Report(Progress(@"C:\done.raw", TransferState.Verified, 999, 999));

        aggregator.Totals().Fraction.ShouldNotBeNull().ShouldBe(0.3, 0.001);
    }

    [Fact]
    public void Clearing_finished_rows_keeps_anything_still_unresolved()
    {
        // "Clear Completed" must not quietly discard the failures a person still has to act on.
        var aggregator = new TransferProgressAggregator();
        aggregator.Report(Progress(@"C:\ok.raw", TransferState.Verified));
        aggregator.Report(Progress(@"C:\skip.raw", TransferState.Skipped));
        aggregator.Report(Progress(@"C:\bad.raw", TransferState.Failed));
        aggregator.Report(Progress(@"C:\clash.raw", TransferState.Conflict));
        aggregator.Report(Progress(@"C:\busy.raw", TransferState.Uploading));

        var removed = aggregator.ClearFinished();

        removed.Count.ShouldBe(2);
        aggregator.Count.ShouldBe(3);
        aggregator.Snapshot().Select(p => p.State)
            .ShouldBe([TransferState.Failed, TransferState.Conflict, TransferState.Uploading], ignoreOrder: true);
    }

    [Fact]
    public void Progress_describes_itself_with_rate_and_eta_while_uploading()
    {
        var progress = new TransferProgress(
            @"C:\Data\run.raw", "/remote/run.raw", TransferState.Uploading, "Uploading",
            BytesTransferred: 3L * 1024 * 1024 * 1024,
            TotalBytes: 6L * 1024 * 1024 * 1024,
            BytesPerSecond: 100L * 1024 * 1024);

        var description = progress.Describe();

        description.ShouldContain("50%");
        description.ShouldContain("6.0 GB");
        description.ShouldContain("100.0 MB/s");
        description.ShouldContain("left");
    }

    [Theory]
    [InlineData(VerifyMethod.ServerMd5, "Verified (server MD5)")]
    [InlineData(VerifyMethod.SizeOnly, "Uploaded - size only")]
    [InlineData(VerifyMethod.None, "Uploaded - not verified")]
    public void Verification_is_described_without_overstating_it(VerifyMethod method, string expected)
    {
        // A green tick that only means "the byte count matched" is how the previous
        // implementation lost trust. The wording has to say what was actually checked.
        new TransferProgress(@"C:\a.raw", "/r/a.raw", TransferState.Uploaded, "Uploaded", 1, 1,
                Verification: method)
            .DescribeVerification()
            .ShouldBe(expected);
    }
}
