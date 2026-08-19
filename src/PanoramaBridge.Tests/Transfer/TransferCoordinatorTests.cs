using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;
using PanoramaBridge.Core.WebDav;
using PanoramaBridge.Tests.TestDoubles;

namespace PanoramaBridge.Tests.Transfer;

public sealed class TransferCoordinatorTests : IAsyncDisposable
{
    private static readonly RemotePath Destination =
        RemotePath.Parse("/_webdav/MacCoss/maccoss/@files/uploads/");

    private readonly SqliteStateStore _store = SqliteStateStore.InMemory();
    private readonly FakeWebDavClient _server = new();
    private readonly string _local = Directory.CreateTempSubdirectory("pb-engine-").FullName;
    private readonly List<TransferProgress> _reported = [];

    private TransferCoordinator NewCoordinator(
        int concurrency = 3,
        ConflictPolicy policy = ConflictPolicy.Ask,
        bool verify = true)
    {
        var coordinator = new TransferCoordinator(
            _server,
            _store,
            new TransferEngineOptions
            {
                LocalBaseDirectory = _local,
                DestinationRoot = Destination,
                MaxConcurrentTransfers = concurrency,
                ConflictPolicy = policy,
                VerifyUploads = verify,
            });

        coordinator.Progress += p =>
        {
            lock (_reported)
            {
                _reported.Add(p);
            }
        };

        return coordinator;
    }

    private async Task<string> WriteAsync(string relativePath, string content)
    {
        var path = Path.Combine(_local, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    private async Task<TransferSummary> RunWithAsync(
        TransferCoordinator coordinator,
        params string[] files)
    {
        foreach (var file in files)
        {
            await coordinator.EnqueueAsync(file);
        }

        coordinator.CompleteAdding();
        return await coordinator.RunAsync();
    }

    // -- The happy path -----------------------------------------------------------------------

    [Fact]
    public async Task A_new_file_is_uploaded_and_verified_against_the_servers_own_hash()
    {
        var file = await WriteAsync("sample.raw", "acquisition data");

        var summary = await RunWithAsync(NewCoordinator(), file);

        summary.Uploaded.ShouldBe(1);
        summary.Failed.ShouldBe(0);

        var record = await _store.GetAsync(file);
        record.ShouldNotBeNull();
        record!.State.ShouldBe(TransferState.Verified);
        record.VerifyMethod.ShouldBe(VerifyMethod.ServerMd5);
        record.VerifiedUtc.ShouldNotBeNull();

        System.Text.Encoding.UTF8
            .GetString(_server.Content(Destination.Append("sample.raw"))!)
            .ShouldBe("acquisition data");
    }

    [Fact]
    public async Task The_local_directory_structure_is_mirrored_remotely()
    {
        var file = await WriteAsync(Path.Combine("2026", "batch 7", "run.raw"), "nested");

        await RunWithAsync(NewCoordinator(), file);

        _server.Content(Destination.Append("2026", true).Append("batch 7", true).Append("run.raw"))
            .ShouldNotBeNull();
    }

    [Fact]
    public async Task Progress_moves_through_uploading_verifying_and_verified()
    {
        var file = await WriteAsync("phases.raw", "some bytes here");

        await RunWithAsync(NewCoordinator(concurrency: 1), file);

        var phases = _reported.Select(p => p.Phase).ToList();
        phases.ShouldContain("Uploading");
        phases.ShouldContain("Verifying");
        phases[^1].ShouldBe("Verified");

        // The gap between the last byte written and the server's answer is stated as its own
        // phase rather than papered over by holding the bar below 100%.
        _reported[^1].Verification.ShouldBe(VerifyMethod.ServerMd5);
        _reported[^1].DescribeVerification().ShouldBe("Verified (server MD5)");
    }

    // -- Re-running ---------------------------------------------------------------------------

    [Fact]
    public async Task A_second_run_over_the_same_files_uploads_nothing_and_never_hashes()
    {
        // The measurement that matters for a monitored folder: after the first pass, repeated
        // scans have to be nearly free.
        var files = new List<string>();
        for (var i = 0; i < 12; i++)
        {
            files.Add(await WriteAsync($"run{i}.raw", $"content {i}"));
        }

        var first = await RunWithAsync(NewCoordinator(), [.. files]);
        first.Uploaded.ShouldBe(12);

        _server.Reset();
        _reported.Clear();

        var second = await RunWithAsync(NewCoordinator(), [.. files]);

        second.Skipped.ShouldBe(12);
        second.Uploaded.ShouldBe(0);
        _server.UploadCalls.ShouldBe(0);
        _server.TotalCalls.ShouldBe(0, "every file should be answered from the ledger alone");

        _reported.ShouldAllBe(p => p.State == TransferState.Skipped);
    }

    [Fact]
    public async Task A_file_edited_between_runs_is_uploaded_again()
    {
        var file = await WriteAsync("edited.raw", "first version");
        await RunWithAsync(NewCoordinator(), file);

        await File.WriteAllTextAsync(file, "second version, longer than before");

        var summary = await RunWithAsync(NewCoordinator(), file);

        summary.Uploaded.ShouldBe(1);
        System.Text.Encoding.UTF8
            .GetString(_server.Content(Destination.Append("edited.raw"))!)
            .ShouldBe("second version, longer than before");
    }

    [Fact]
    public async Task A_file_already_on_the_server_is_skipped_without_being_sent()
    {
        // Covers a fresh install, or a rebuilt machine, where the ledger is empty but the data
        // is already up there.
        const string Content = "uploaded by a previous installation";
        var file = await WriteAsync("existing.raw", Content);
        _server.Seed(Destination.Append("existing.raw"), System.Text.Encoding.UTF8.GetBytes(Content));

        var summary = await RunWithAsync(NewCoordinator(), file);

        summary.Skipped.ShouldBe(1);
        _server.UploadCalls.ShouldBe(0);

        var record = await _store.GetAsync(file);
        record!.VerifyMethod.ShouldBe(VerifyMethod.ServerMd5);
    }

    // -- Deduplication and concurrency --------------------------------------------------------

    [Fact]
    public async Task The_same_file_offered_repeatedly_is_only_queued_once()
    {
        // A watcher can easily raise several events for one file copy.
        var file = await WriteAsync("noisy.raw", "one copy");
        var coordinator = NewCoordinator();

        (await coordinator.EnqueueAsync(file)).ShouldBeTrue();
        (await coordinator.EnqueueAsync(file)).ShouldBeFalse();
        (await coordinator.EnqueueAsync(file.ToUpperInvariant())).ShouldBeFalse();

        coordinator.CompleteAdding();
        var summary = await coordinator.RunAsync();

        summary.Total.ShouldBe(1);
        _server.UploadCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_workers_all_make_progress_and_the_totals_add_up()
    {
        var files = new List<string>();
        for (var i = 0; i < 40; i++)
        {
            files.Add(await WriteAsync($"parallel{i}.raw", new string((char)('a' + i % 26), 500 + i)));
        }

        var summary = await RunWithAsync(NewCoordinator(concurrency: 4), [.. files]);

        summary.Uploaded.ShouldBe(40);
        summary.Failed.ShouldBe(0);
        summary.BytesUploaded.ShouldBe(files.Sum(f => new FileInfo(f).Length));

        // Counters are incremented from several workers, so a torn count would show up here.
        summary.Total.ShouldBe(40);
    }

    // -- Failure handling ---------------------------------------------------------------------

    [Fact]
    public async Task One_failing_file_does_not_stop_the_others()
    {
        var good1 = await WriteAsync("good1.raw", "fine");
        var bad = await WriteAsync("bad.raw", "will fail");
        var good2 = await WriteAsync("good2.raw", "also fine");

        // More failures than the fake will forgive, so this file never succeeds.
        _server.FailUploadsBeforeSucceeding = 1;

        var summary = await RunWithAsync(NewCoordinator(concurrency: 1), good1, bad, good2);

        summary.Total.ShouldBe(3);
        summary.Failed.ShouldBe(1);
        summary.Uploaded.ShouldBe(2);
    }

    [Fact]
    public async Task A_server_that_stores_different_bytes_is_reported_as_a_failure()
    {
        // The case the Python version could not detect: the transfer "succeeded" but the copy
        // is wrong. Comparing against the server's own hash is what catches it.
        var file = await WriteAsync("corrupt.raw", "sent this");
        _server.OverrideReportedHash = "00000000000000000000000000000000";

        var summary = await RunWithAsync(NewCoordinator(), file);

        summary.Failed.ShouldBe(1);
        summary.Uploaded.ShouldBe(0);

        var record = await _store.GetAsync(file);
        record!.State.ShouldBe(TransferState.Failed);
        record.LastError.ShouldNotBeNull().ShouldContain("different content");
    }

    [Fact]
    public async Task A_server_that_reports_no_hash_is_not_recorded_as_verified()
    {
        var file = await WriteAsync("nohash.raw", "content");
        _server.WithholdHashes = true;

        // The fake reports no per-file hash either once hashes are withheld.
        var summary = await RunWithAsync(NewCoordinator(), file);

        var record = await _store.GetAsync(file);
        record!.VerifyMethod.ShouldNotBe(VerifyMethod.ServerMd5);
        summary.Uploaded.ShouldBe(0);
    }

    [Fact]
    public async Task Verification_can_be_turned_off_and_then_makes_no_claim()
    {
        var file = await WriteAsync("unverified.raw", "content");

        var summary = await RunWithAsync(NewCoordinator(verify: false), file);

        summary.Uploaded.ShouldBe(1);
        _server.FileHashCalls.ShouldBe(0);

        var record = await _store.GetAsync(file);
        record!.State.ShouldBe(TransferState.Uploaded);
        record.VerifyMethod.ShouldBe(VerifyMethod.None);

        _reported[^1].DescribeVerification().ShouldBe("Uploaded - not verified");
    }

    // -- Conflicts ----------------------------------------------------------------------------

    [Fact]
    public async Task A_conflict_is_recorded_rather_than_guessed_at()
    {
        var file = await WriteAsync("clash.raw", "local content!!");
        _server.Seed(Destination.Append("clash.raw"), "server content"u8.ToArray());

        var summary = await RunWithAsync(NewCoordinator(policy: ConflictPolicy.Ask), file);

        summary.Conflicts.ShouldBe(1);
        _server.UploadCalls.ShouldBe(0);

        var record = await _store.GetAsync(file);
        record!.State.ShouldBe(TransferState.Conflict);
    }

    [Fact]
    public async Task Editing_a_file_we_uploaded_is_a_new_version_not_a_conflict()
    {
        // The remote copy is the one this application put there, so a local edit is simply a
        // newer version to send. Calling that a conflict would stop routine re-processing dead
        // and demand a pointless decision from the user.
        var file = await WriteAsync("versioned.raw", "first version");
        await RunWithAsync(NewCoordinator(), file);

        await File.WriteAllTextAsync(file, "second version, materially longer");

        var summary = await RunWithAsync(NewCoordinator(policy: ConflictPolicy.Ask), file);

        summary.Uploaded.ShouldBe(1);
        summary.Conflicts.ShouldBe(0);
    }

    [Fact]
    public async Task A_remote_copy_we_did_not_write_is_a_conflict_even_at_the_same_size()
    {
        // The mirror image: the ledger says we put content X there, but the server holds
        // something else, so somebody changed it behind our back. That is a real conflict and
        // overwriting it silently could destroy another person's work.
        var file = await WriteAsync("touched.raw", "our own content..");
        await RunWithAsync(NewCoordinator(), file);

        // Someone edits the remote copy, keeping the length identical.
        _server.Seed(Destination.Append("touched.raw"), "someone else's!!!"u8.ToArray());

        // And the local file changes too, so the ledger's fast path cannot short-circuit.
        await File.WriteAllTextAsync(file, "our newer content");

        var summary = await RunWithAsync(NewCoordinator(policy: ConflictPolicy.Ask), file);

        summary.Conflicts.ShouldBe(1);
        summary.Uploaded.ShouldBe(0);
    }

    [Fact]
    public async Task Overwrite_policy_replaces_the_remote_copy()
    {
        var file = await WriteAsync("clash.raw", "local content!!");
        _server.Seed(Destination.Append("clash.raw"), "server content"u8.ToArray());

        var summary = await RunWithAsync(NewCoordinator(policy: ConflictPolicy.Overwrite), file);

        summary.Uploaded.ShouldBe(1);
        System.Text.Encoding.UTF8
            .GetString(_server.Content(Destination.Append("clash.raw"))!)
            .ShouldBe("local content!!");
    }

    // -- Crash recovery -----------------------------------------------------------------------

    [Fact]
    public async Task Rows_left_mid_upload_by_a_crash_are_requeued()
    {
        // Every transition is written before the action it describes, so a row still marked
        // Uploading means the process died during that upload.
        var file = await WriteAsync("interrupted.raw", "half sent");

        await _store.SaveAsync(
            UploadRecord.ForNewFile(LocalFileStamp.FromFile(file), Destination.Append("interrupted.raw").ToEncodedString())
                with { State = TransferState.Uploading });

        var coordinator = NewCoordinator();
        var requeued = await coordinator.RecoverInterruptedAsync();

        coordinator.CompleteAdding();
        var summary = await coordinator.RunAsync();

        requeued.ShouldBe(1);
        summary.Uploaded.ShouldBe(1);
        (await _store.GetAsync(file))!.State.ShouldBe(TransferState.Verified);
    }

    [Fact]
    public async Task An_interrupted_row_whose_file_is_gone_is_failed_not_requeued()
    {
        var missing = Path.Combine(_local, "vanished.raw");

        await _store.SaveAsync(
            new UploadRecord(missing, Destination.Append("vanished.raw").ToEncodedString(),
                10, 0, null, null, TransferState.Uploading, VerifyMethod.None, null, 1, null, false));

        var coordinator = NewCoordinator();

        (await coordinator.RecoverInterruptedAsync()).ShouldBe(0);
        (await _store.GetAsync(missing))!.State.ShouldBe(TransferState.Failed);
    }

    [Fact]
    public async Task An_interrupted_upload_that_actually_completed_is_recognised_and_skipped()
    {
        // The bytes may well have arrived before the process died. The decision ladder finds
        // the identical remote copy and does not send it a second time.
        const string Content = "this actually made it";
        var file = await WriteAsync("landed.raw", Content);
        _server.Seed(Destination.Append("landed.raw"), System.Text.Encoding.UTF8.GetBytes(Content));

        await _store.SaveAsync(
            UploadRecord.ForNewFile(LocalFileStamp.FromFile(file), Destination.Append("landed.raw").ToEncodedString())
                with { State = TransferState.Uploading });

        var coordinator = NewCoordinator();
        await coordinator.RecoverInterruptedAsync();
        coordinator.CompleteAdding();

        var summary = await coordinator.RunAsync();

        summary.Skipped.ShouldBe(1);
        _server.UploadCalls.ShouldBe(0);
    }

    // -- Cancellation -------------------------------------------------------------------------

    [Fact]
    public async Task Cancelling_stops_the_run()
    {
        for (var i = 0; i < 50; i++)
        {
            await WriteAsync($"many{i}.raw", new string('x', 200));
        }

        var coordinator = NewCoordinator(concurrency: 2);
        foreach (var file in Directory.GetFiles(_local))
        {
            await coordinator.EnqueueAsync(file);
        }

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => coordinator.RunAsync(cts.Token));
    }

    // -- Safety -------------------------------------------------------------------------------

    [Fact]
    public async Task A_semicolon_in_a_file_name_fails_that_file_rather_than_uploading_it()
    {
        // Panorama truncates the name at the semicolon and could silently overwrite another
        // acquisition, so the only safe outcome is to refuse.
        var file = await WriteAsync("run;rep1.raw", "would be truncated");

        var summary = await RunWithAsync(NewCoordinator(), file);

        summary.Failed.ShouldBe(1);
        _server.UploadCalls.ShouldBe(0);
        (await _store.GetAsync(file))?.State.ShouldBe(TransferState.Failed);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        if (Directory.Exists(_local))
        {
            Directory.Delete(_local, recursive: true);
        }
    }
}
