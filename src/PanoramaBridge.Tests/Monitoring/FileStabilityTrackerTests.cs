using PanoramaBridge.Core.Monitoring;

namespace PanoramaBridge.Tests.Monitoring;

/// <summary>
/// Uses real files and real Windows file handles throughout. The behaviour being relied on here
/// -- which share modes conflict, and when the directory entry's size is stale -- is a property
/// of the operating system, so a mock would only test the assumptions rather than the reality.
/// </summary>
public sealed class FileStabilityTrackerTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pb-stability-").FullName;
    private DateTimeOffset _now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private FileStabilityTracker NewTracker(int quietSeconds = 10) =>
        new(TimeSpan.FromSeconds(quietSeconds), () => _now);

    private void Advance(TimeSpan by) => _now += by;

    private string PathFor(string name) => Path.Combine(_directory, name);

    // -- An instrument holding a file open ----------------------------------------------------

    [Fact]
    public void A_file_held_open_by_another_process_is_never_ready()
    {
        // What an instrument does for the whole of an acquisition. Note the handle allows
        // readers, which is why a plain "can I open this for reading?" test would wrongly say
        // the file was available.
        var path = PathFor("acquiring.raw");
        File.WriteAllText(path, "partial acquisition data");

        using var instrument = new FileStream(
            path, FileMode.Open, FileAccess.Write, FileShare.Read);

        var tracker = NewTracker();

        tracker.Check(path);
        Advance(TimeSpan.FromMinutes(30));

        var readiness = tracker.Check(path);

        readiness.IsReady.ShouldBeFalse("an instrument still holds the file");
        readiness.Reason.ShouldBe(ReadinessReason.Locked);
        readiness.IsWorthRetrying.ShouldBeTrue();
        readiness.Detail.ShouldContain("open in another program");
    }

    [Fact]
    public void A_readable_but_still_open_file_is_not_mistaken_for_a_finished_one()
    {
        // Thermo and other vendors leave the acquisition file readable while writing it. This is
        // the case that makes an exclusive-open probe necessary rather than a read test.
        var path = PathFor("readable-while-writing.raw");
        File.WriteAllText(path, "data");

        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);

        // Prove the premise: reading it is perfectly possible right now.
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            reader.ReadByte().ShouldBeGreaterThan(-1);
        }

        var tracker = NewTracker();
        tracker.Check(path);
        Advance(TimeSpan.FromMinutes(1));

        tracker.Check(path).Reason.ShouldBe(ReadinessReason.Locked);
    }

    [Fact]
    public void Once_the_instrument_closes_the_file_it_becomes_ready()
    {
        var path = PathFor("finished.raw");
        File.WriteAllText(path, "complete acquisition");

        var tracker = NewTracker();

        using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            tracker.Check(path);
            Advance(TimeSpan.FromMinutes(30));
            tracker.Check(path).Reason.ShouldBe(ReadinessReason.Locked);
        }

        // Handle released. Size has not changed, and the quiet period has long since elapsed.
        tracker.Check(path).IsReady.ShouldBeTrue();
    }

    // -- A file being copied into the watched folder -------------------------------------------

    [Fact]
    public void A_copy_in_progress_is_not_uploaded_until_it_finishes()
    {
        // Simulates what Explorer does: create the destination, hold it open, write in blocks,
        // then close. The whole point is that no intermediate state is ever reported ready.
        var path = PathFor("being-copied.raw");
        var tracker = NewTracker(quietSeconds: 5);
        var block = new byte[64 * 1024];

        using (var destination = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            for (var written = 0; written < 10; written++)
            {
                destination.Write(block);
                destination.Flush();

                // Time passes between blocks, which is exactly when a naive quiet-period check
                // would decide the file had settled.
                Advance(TimeSpan.FromSeconds(30));

                tracker.Check(path).IsReady.ShouldBeFalse(
                    $"the copy is still running after {written + 1} blocks");
            }
        }

        // Copy complete and the handle closed.
        var afterClose = tracker.Check(path);
        Advance(TimeSpan.FromSeconds(6));

        tracker.Check(path).IsReady.ShouldBeTrue();
        new FileInfo(path).Length.ShouldBe(10L * block.Length);
    }

    [Fact]
    public void A_writer_that_closes_between_blocks_is_still_caught_by_size_change()
    {
        // The gap in the lock probe: a process that appends and closes for each block leaves
        // moments where nothing holds the file. Size stability is what covers this.
        var path = PathFor("append-and-close.raw");
        var tracker = NewTracker(quietSeconds: 5);

        for (var block = 0; block < 5; block++)
        {
            File.AppendAllText(path, new string('x', 4096));

            // Nothing holds the file at this instant, and enough time has passed to satisfy the
            // quiet period -- but the size moved, so it must not be released.
            Advance(TimeSpan.FromSeconds(30));

            var readiness = tracker.Check(path);

            if (block > 0)
            {
                readiness.Reason.ShouldBe(ReadinessReason.Growing);
            }

            readiness.IsReady.ShouldBeFalse($"block {block} only just landed");
        }

        // Writing has stopped. One check to record the settled size, then wait it out.
        tracker.Check(path);
        Advance(TimeSpan.FromSeconds(6));

        tracker.Check(path).IsReady.ShouldBeTrue();
    }

    [Fact]
    public void The_reported_size_reflects_the_real_end_of_file_not_the_directory_entry()
    {
        // Windows does not keep the directory entry up to date while a write handle is open, so
        // FileInfo.Length can report a stale size that does not change between samples. A
        // stability check built on it would conclude that an actively written file had settled.
        // The tracker opens the file and reads the length from the handle instead.
        var path = PathFor("stale-metadata.raw");

        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        writer.Write(new byte[100_000]);
        writer.Flush();

        var fromHandle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var trueLength = fromHandle.Length;
        fromHandle.Dispose();

        trueLength.ShouldBe(100_000);

        // Whatever the directory entry currently says, the tracker must agree with the handle.
        NewTracker().Check(path).Length.ShouldBe(trueLength);
    }

    // -- The quiet period ---------------------------------------------------------------------

    [Fact]
    public void A_first_sighting_is_never_ready_however_settled_the_file_looks()
    {
        // One observation cannot tell a finished file from one that happens to be between
        // writes, so the answer is always "wait and look again".
        var path = PathFor("first-look.raw");
        File.WriteAllText(path, "complete and closed");

        var readiness = NewTracker().Check(path);

        readiness.IsReady.ShouldBeFalse();
        readiness.Reason.ShouldBe(ReadinessReason.Settling);
    }

    [Fact]
    public void A_quiet_file_becomes_ready_only_after_the_full_quiet_period()
    {
        var path = PathFor("settling.raw");
        File.WriteAllText(path, "done");

        var tracker = NewTracker(quietSeconds: 10);

        tracker.Check(path).Reason.ShouldBe(ReadinessReason.Settling);

        Advance(TimeSpan.FromSeconds(9));
        tracker.Check(path).Reason.ShouldBe(ReadinessReason.Settling);

        Advance(TimeSpan.FromSeconds(2));
        tracker.Check(path).IsReady.ShouldBeTrue();
    }

    [Fact]
    public void A_late_change_restarts_the_clock_rather_than_shortening_it()
    {
        var path = PathFor("late-write.raw");
        File.WriteAllText(path, "first");

        var tracker = NewTracker(quietSeconds: 10);
        tracker.Check(path);

        Advance(TimeSpan.FromSeconds(9));
        File.AppendAllText(path, " and more");

        tracker.Check(path).Reason.ShouldBe(ReadinessReason.Growing);

        // The nine seconds already served count for nothing; the full period starts again.
        Advance(TimeSpan.FromSeconds(9));
        tracker.Check(path).Reason.ShouldBe(ReadinessReason.Settling);

        Advance(TimeSpan.FromSeconds(2));
        tracker.Check(path).IsReady.ShouldBeTrue();
    }

    [Fact]
    public void A_zero_length_file_is_handled_like_any_other()
    {
        // Instrument software often creates the file before writing anything to it.
        var path = PathFor("empty.raw");
        File.WriteAllBytes(path, []);

        var tracker = NewTracker(quietSeconds: 5);
        tracker.Check(path).Reason.ShouldBe(ReadinessReason.Settling);

        Advance(TimeSpan.FromSeconds(6));
        tracker.Check(path).IsReady.ShouldBeTrue();
    }

    // -- Files that go away --------------------------------------------------------------------

    [Fact]
    public void A_file_that_disappears_is_reported_missing_and_stops_being_tracked()
    {
        // A copy that is cancelled part-way leaves nothing behind, and retrying forever would
        // be pointless.
        var path = PathFor("vanishing.raw");
        File.WriteAllText(path, "here for now");

        var tracker = NewTracker();
        tracker.Check(path);
        tracker.Count.ShouldBe(1);

        File.Delete(path);

        var readiness = tracker.Check(path);

        readiness.Reason.ShouldBe(ReadinessReason.Missing);
        readiness.IsWorthRetrying.ShouldBeFalse();
        tracker.Count.ShouldBe(0);
    }

    [Fact]
    public void A_released_file_is_forgotten_so_tracking_does_not_grow_without_bound()
    {
        var tracker = NewTracker(quietSeconds: 1);

        for (var i = 0; i < 50; i++)
        {
            var path = PathFor($"batch{i}.raw");
            File.WriteAllText(path, $"file {i}");
            tracker.Check(path);
        }

        tracker.Count.ShouldBe(50);

        Advance(TimeSpan.FromSeconds(2));

        for (var i = 0; i < 50; i++)
        {
            tracker.Check(PathFor($"batch{i}.raw")).IsReady.ShouldBeTrue();
        }

        tracker.Count.ShouldBe(0, "a file that has been released is no longer tracked");
    }

    [Fact]
    public void Forgetting_a_file_explicitly_works()
    {
        var path = PathFor("forget-me.raw");
        File.WriteAllText(path, "x");

        var tracker = NewTracker();
        tracker.Check(path);
        tracker.Forget(path);

        tracker.Count.ShouldBe(0);
    }

    // -- Concurrency ---------------------------------------------------------------------------

    [Fact]
    public async Task Checking_the_same_file_from_several_threads_is_safe()
    {
        // The reconciliation sweep and the file-system watcher can both land on one file.
        var path = PathFor("contended.raw");
        File.WriteAllText(path, "shared");

        var tracker = NewTracker(quietSeconds: 0);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            FileReadiness last = default;
            for (var i = 0; i < 50; i++)
            {
                last = tracker.Check(path);
            }

            return last;
        })));

        results.ShouldAllBe(r => r.Reason != ReadinessReason.Unreadable);
    }

    // -- A realistic end-to-end sequence ------------------------------------------------------

    [Fact]
    public void An_acquisition_from_start_to_finish_is_released_exactly_once_at_the_end()
    {
        // The whole lifecycle: the instrument creates the file, appends to it over a long run
        // while holding it open, then closes it. Nothing before the close may be released.
        var path = PathFor("full-run.raw");
        var tracker = NewTracker(quietSeconds: 10);
        var readyCount = 0;

        using (var acquisition = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            for (var minute = 0; minute < 45; minute++)
            {
                // A real run does not grow smoothly; it writes in bursts with quiet stretches.
                if (minute % 5 != 0)
                {
                    acquisition.Write(new byte[8192]);
                    acquisition.Flush();
                }

                Advance(TimeSpan.FromMinutes(1));

                if (tracker.Check(path).IsReady)
                {
                    readyCount++;
                }
            }
        }

        readyCount.ShouldBe(0, "nothing during the run should ever have been released");

        // Run over, handle closed, and the file sits quiet.
        tracker.Check(path);
        Advance(TimeSpan.FromSeconds(11));

        tracker.Check(path).IsReady.ShouldBeTrue("the acquisition has finished");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
