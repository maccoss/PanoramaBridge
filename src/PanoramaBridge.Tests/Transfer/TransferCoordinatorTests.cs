using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Monitoring;
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
        bool verify = true,
        int queueCapacity = 5000)
    {
        var coordinator = new TransferCoordinator(
            _server,
            _store,
            new TransferEngineOptions
            {
                LocalBaseDirectory = _local,
                DestinationRoot = Destination,
                MaxConcurrentTransfers = concurrency,
                QueueCapacity = queueCapacity,
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

    // -- The date the instrument wrote the file ---------------------------------------------------

    /// <summary>
    /// Writes a file that opens with a valid Thermo RAW header and nothing coherent after it.
    /// </summary>
    /// <remarks>
    /// A header and no body is what a copy that died early looks like, and it is the case the
    /// ordinary readiness signals cannot see: nothing holds it and its size is perfectly stable.
    /// </remarks>
    private async Task<string> WriteRawHeaderAsync(string name, int formatVersion, int padding)
    {
        var bytes = new byte[1356 + padding];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes, 0xA101);
        System.Text.Encoding.Unicode.GetBytes("Finnigan").CopyTo(bytes, 2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(0x24), (uint)formatVersion);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(0x28), 133_000_000_000_000_000UL);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(0x98), 133_000_000_100_000_000UL);

        var path = Path.Combine(_local, name);
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    [Fact]
    public async Task Checking_a_raw_file_never_modifies_it()
    {
        // An acquisition is not ours to touch. A validator that could alter what it validates
        // would be worse than no validator at all.
        var file = await WriteRawHeaderAsync("untouched.raw", formatVersion: 66, padding: 4096);

        var before = await File.ReadAllBytesAsync(file);
        var writtenAt = File.GetLastWriteTimeUtc(file);

        await RunWithAsync(NewCoordinator(), file);

        File.ReadAllBytes(file).ShouldBe(before, "not one byte may change");
        File.GetLastWriteTimeUtc(file).ShouldBe(writtenAt, "and neither may the timestamp");
    }

    [Fact]
    public async Task A_raw_file_still_held_open_for_writing_is_not_examined()
    {
        // What an instrument mid-acquisition looks like. The check must decline rather than read
        // a file that is still growing, and declining must not stop the transfer machinery.
        var file = await WriteRawHeaderAsync("acquiring.raw", formatVersion: 66, padding: 0);

        using (var held = new FileStream(
                   file, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            await RunWithAsync(NewCoordinator(), file);

            var during = await _store.GetAsync(file);
            during?.RawCheck.ShouldBeNull(
                "a file held open for writing must not be given a verdict");
        }

        // The same file, once released, is examined and found short.
        await RunWithAsync(NewCoordinator(), file);

        var after = await _store.GetAsync(file);
        after!.RawCheck.ShouldNotBeNullOrEmpty();
        after.State.ShouldBe(TransferState.Conflict);
    }

    [Fact]
    public async Task A_directory_passed_directly_to_the_coordinator_is_not_transferred()
    {
        // Product discovery only queues files. Retaining this guard keeps an accidental direct
        // caller from turning a directory into an unsupported transfer shape.
        var directory = Path.Combine(_local, "unsupported.d");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "analysis.tdf"), "data");

        var summary = await RunWithAsync(NewCoordinator(), directory);

        summary.Total.ShouldBe(0);
        _server.UploadCalls.ShouldBe(0);
        (await _store.GetAsync(directory)).ShouldBeNull();
    }

    [Fact]
    public async Task Cancelling_a_run_does_not_leave_a_file_reporting_that_it_is_uploading()
    {
        // The reported bug, at the level it actually happens. Stop a run while bytes are moving
        // and the file's last progress report is "Uploading"; the aggregator keeps the latest per
        // file, so anything asking "is a transfer in flight?" gets yes for the rest of the
        // session -- which is what silently disabled Restart now.
        var file = await WriteAsync("interrupted.raw", "an acquisition being sent");

        using var hold = new SemaphoreSlim(0);
        using var started = new SemaphoreSlim(0);
        _server.HoldUpload = hold;
        _server.UploadStarted = started;

        await using var coordinator = NewCoordinator(concurrency: 1);
        using var stopping = new CancellationTokenSource();

        var run = coordinator.RunAsync(stopping.Token);
        await coordinator.EnqueueAsync(file);

        // Wait until it is genuinely mid-upload before pulling the rug.
        (await started.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();

        _reported.Any(r => r.State == TransferState.Uploading).ShouldBeTrue("it is uploading now");

        await stopping.CancelAsync();
        hold.Release(4);

        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
            // How a cancelled run is supposed to end.
        }

        // Latest report per file, which is what HasTransferInFlight reads.
        var latest = _reported
            .GroupBy(r => r.LocalPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last());

        latest.ShouldNotContain(
            r => r.State == TransferState.Uploading,
            "a stranded Uploading row blocks the updater and Exit for the whole session");
    }

    [Fact]
    public async Task A_truncated_thermo_raw_file_is_not_uploaded()
    {
        // The reason the check exists. Uploading a short acquisition is worse than uploading
        // nothing, because the copy looks complete and verifies against its own truncated
        // content -- and the two ordinary readiness signals cannot tell this from a finished
        // file, since nothing holds it and its size has stopped changing.
        var file = await WriteRawHeaderAsync("cut-short.raw", formatVersion: 66, padding: 0);

        await RunWithAsync(NewCoordinator(), file);

        _server.Content(Destination.Append("cut-short.raw")).ShouldBeNull(
            "a file proven short must never reach the server");

        var record = await _store.GetAsync(file);
        record.ShouldNotBeNull();
        record!.State.ShouldBe(TransferState.Conflict, "and it must be visible, not silently dropped");
        record.RawCheck.ShouldNotBeNullOrEmpty();
        record.LastError!.ShouldContain("Truncated");
    }

    [Fact]
    public async Task A_raw_revision_we_do_not_understand_is_still_uploaded()
    {
        // The property that keeps a checker from becoming an outage. Thermo ships new format
        // revisions; refusing an unfamiliar one would turn a firmware update into an instrument
        // that has silently stopped transferring.
        var file = await WriteRawHeaderAsync("future.raw", formatVersion: 70, padding: 4096);

        await RunWithAsync(NewCoordinator(), file);

        _server.Content(Destination.Append("future.raw")).ShouldNotBeNull(
            "an unrecognised revision is not a reason to hold a file back");

        var record = await _store.GetAsync(file);
        record!.RawCheck!.ShouldContain("70", customMessage:
            "and the record must name the revision, so the gap can be closed");
    }

    [Fact]
    public async Task A_file_that_is_not_a_raw_file_is_left_alone_by_the_check()
    {
        var file = await WriteAsync("notes.txt", "nothing to do with Thermo");

        await RunWithAsync(NewCoordinator(), file);

        _server.Content(Destination.Append("notes.txt")).ShouldNotBeNull();

        var record = await _store.GetAsync(file);
        record!.RawCheck.ShouldBeNull("nothing was asked of it, so nothing should be claimed");
    }

    [Fact]
    public async Task An_upload_keeps_the_date_the_instrument_wrote_it()
    {
        // Moving a file is not the same as collecting it. Left alone the server stamps an upload
        // with the moment it arrived, which turns every acquisition in an archive into today's
        // date. LabKey takes the real one through X-LABKEY-Last-Modified, and the transport
        // sends it on every PUT.
        var acquired = new DateTime(2024, 7, 2, 14, 33, 42, DateTimeKind.Utc);

        var file = await WriteAsync("acquired.raw", "acquisition data");
        File.SetLastWriteTimeUtc(file, acquired);

        await RunWithAsync(NewCoordinator(), file);

        _server.StampOf(Destination.Append("acquired.raw"))
            .ShouldBe(new DateTimeOffset(acquired));
    }

    [Fact]
    public async Task The_checksum_file_carries_the_same_date_as_the_file_it_describes()
    {
        // So the two stay adjacent however a directory listing is sorted.
        var acquired = new DateTime(2024, 7, 2, 14, 33, 42, DateTimeKind.Utc);

        var file = await WriteAsync("acquired.raw", "acquisition data");
        File.SetLastWriteTimeUtc(file, acquired);

        await RunWithAsync(NewCoordinator(), file);

        _server.StampOf(Destination.Append("acquired.raw.md5"))
            .ShouldBe(new DateTimeOffset(acquired));
    }

    // -- The checksum sidecar --------------------------------------------------------------------

    [Fact]
    public async Task A_verified_upload_leaves_its_checksum_beside_it_on_the_server()
    {
        var file = await WriteAsync("sample.raw", "acquisition data");
        File.SetLastWriteTimeUtc(file, new DateTime(2025, 5, 19, 14, 32, 10, DateTimeKind.Utc));

        await RunWithAsync(NewCoordinator(), file);

        var sidecar = _server.Text(Destination.Append("sample.raw.md5")).ShouldNotBeNull();

        var record = (await _store.GetAsync(file)).ShouldNotBeNull();

        sidecar.ShouldStartWith($"{record.Md5}  sample.raw");
        sidecar.ShouldContain("# acquired  2025-05-19T14:32:10Z");
        sidecar.ShouldContain("# bytes     16");
    }

    [Fact]
    public async Task A_file_that_was_not_sent_gets_no_sidecar()
    {
        // Only a verified upload earns one. Writing a checksum for a file this run did not put
        // there would be claiming something that was never checked.
        var file = await WriteAsync("sample.raw", "acquisition data");
        _server.Seed(Destination.Append("sample.raw"), "acquisition data"u8.ToArray());

        var summary = await RunWithAsync(NewCoordinator(), file);

        summary.Skipped.ShouldBe(1);
        _server.Content(Destination.Append("sample.raw.md5")).ShouldBeNull();
    }

    [Fact]
    public async Task A_sidecar_that_cannot_be_written_does_not_fail_the_transfer()
    {
        // The data is on the server and proven. Reporting that as a failure because a note
        // beside it could not be written would be worse than the missing note.
        var file = await WriteAsync("sample.raw", "acquisition data");

        _server.FailTextUploads = true;

        var summary = await RunWithAsync(NewCoordinator(), file);

        summary.Uploaded.ShouldBe(1);
        summary.Failed.ShouldBe(0);

        var record = (await _store.GetAsync(file)).ShouldNotBeNull();
        record.State.ShouldBe(TransferState.Verified);
    }

    [Fact]
    public async Task Sidecars_can_be_turned_off()
    {
        var file = await WriteAsync("sample.raw", "acquisition data");

        await using var coordinator = new TransferCoordinator(
            _server,
            _store,
            new TransferEngineOptions
            {
                LocalBaseDirectory = _local,
                DestinationRoot = Destination,
                WriteChecksumSidecars = false,
            });

        await RunWithAsync(coordinator, file);

        _server.Content(Destination.Append("sample.raw")).ShouldNotBeNull();
        _server.Content(Destination.Append("sample.raw.md5")).ShouldBeNull();
    }

    // -- What a batch costs the server ---------------------------------------------------------

    [Fact]
    public async Task A_batch_into_one_folder_asks_the_server_for_its_contents_once()
    {
        // The collection hash is computed on demand: the server reads every byte in the folder to
        // answer it, which against a folder holding 19 GB was measured at half a minute. Asking
        // once per uploaded file therefore makes a batch quadratic in the size of the
        // destination, and it grows as the batch proceeds. Everything needed to keep the cached
        // listing current is already in hand after each upload, so it is updated rather than
        // discarded.
        // A destination that already holds data, which is the case that costs: the hash is over
        // everything in the folder, so it gets more expensive the more has been archived there.
        _server.Seed(Destination.Append("archived.raw"), "an earlier acquisition"u8.ToArray());

        var files = new List<string>();

        for (var i = 0; i < 12; i++)
        {
            files.Add(await WriteAsync($"batch{i:D2}.raw", $"acquisition {i}"));
        }

        var summary = await RunWithAsync(NewCoordinator(concurrency: 1), [.. files]);

        summary.Uploaded.ShouldBe(12);

        _server.ListCalls.ShouldBe(1, "one listing of the destination, however many files follow");

        _server.CollectionHashCalls.ShouldBe(
            0,
            "and no collection hash at all: none of these names is on the server, so nothing "
            + "read a hash, and asking for one would have made the server read every byte in "
            + "the destination to answer a question already answered by the listing");

        _server.UploadCalls.ShouldBe(12);
        _server.FileHashCalls.ShouldBe(12, "verification is per file and is meant to be");
    }

    [Fact]
    public async Task A_batch_that_does_need_hashes_still_only_asks_once()
    {
        // The other half of the same property. When the names do match, the folder's hashes are
        // worth fetching -- and they arrive together, so twelve files cost one request rather
        // than twelve. Making the fetch lazy must not turn one round trip into a dozen.
        var files = new List<string>();

        for (var i = 0; i < 12; i++)
        {
            var content = $"acquisition {i}";
            files.Add(await WriteAsync($"already{i:D2}.raw", content));
            _server.Seed(
                Destination.Append($"already{i:D2}.raw"),
                System.Text.Encoding.UTF8.GetBytes(content));
        }

        _server.Reset();

        var summary = await RunWithAsync(NewCoordinator(concurrency: 1), [.. files]);

        summary.Skipped.ShouldBe(12, "every one is already there, byte for byte");
        _server.ListCalls.ShouldBe(1);
        _server.CollectionHashCalls.ShouldBe(1, "fetched once for the folder, not once per file");
        _server.UploadCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_file_uploaded_a_moment_ago_is_recognised_without_asking_again()
    {
        // Folding the upload into the cached listing has to leave it correct, not merely cheap.
        // A second offer of the same file must reach the same conclusion the server would.
        var file = await WriteAsync("sample.raw", "acquisition data");

        await RunWithAsync(NewCoordinator(), file);

        var snapshots = new RemoteSnapshotCache(_server);
        _server.Reset();

        await using var second = new TransferCoordinator(
            _server,
            _store,
            new TransferEngineOptions
            {
                LocalBaseDirectory = _local,
                DestinationRoot = Destination,
                MaxConcurrentTransfers = 1,
            },
            snapshots);

        // Asked twice through one cache, as two files in the same folder would be.
        await second.EnqueueAsync(file);
        second.CompleteAdding();
        var summary = await second.RunAsync();

        summary.Skipped.ShouldBe(1);
        _server.UploadCalls.ShouldBe(0, "it is already there, unchanged");
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
    public void Progress_is_reported_inline_so_ordering_is_preserved()
    {
        // The framework's Progress<T> POSTS each report rather than invoking it, so a report can
        // be delivered after the code that follows it. Because the aggregator is latest-wins,
        // that would let a late "uploading" overwrite "verified" and leave a finished row stuck
        // showing progress. This caught exactly that: the phase sequence passed locally and
        // failed on CI, where the posted callback arrived after the run had finished.
        var order = new List<string>();

        IProgress<long> progress = new InlineProgress<long>(_ => order.Add("reported"));

        progress.Report(1);
        order.Add("after");

        order.ShouldBe(["reported", "after"]);
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

        // Ordering, not just presence: a row must never appear to go backwards.
        phases.IndexOf("Uploading").ShouldBeLessThan(phases.IndexOf("Verifying"));

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

        // Stamped explicitly rather than relying on the write having reached the directory entry.
        // Windows updates that entry lazily, so a length and time read immediately after a write
        // can still be the previous ones -- and the fast path, seeing a file it believes
        // unchanged, would skip it. In the application a file has been quiet for ten seconds
        // before it is ever offered, so the window cannot arise; here it is pure timing, and it
        // made this test fail about one run in six.
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));

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
                10, 0, null, null, TransferState.Uploading, VerifyMethod.None, null, 1, null));

        var coordinator = NewCoordinator();

        (await coordinator.RecoverInterruptedAsync()).ShouldBe(0);
        (await _store.GetAsync(missing))!.State.ShouldBe(TransferState.Failed);
    }

    [Fact]
    public async Task Nothing_is_written_off_while_the_monitored_folder_is_unreachable()
    {
        // Recovery runs at startup, which on a monitored network share is routinely before the
        // share is mounted -- and an unreachable path answers exactly as a deleted one does.
        // Writing rows off then claims data no longer exists while it sits untouched on the
        // share. Not being able to look is not evidence that nothing is there.
        var missing = Path.Combine(UnmountedDrive(), "data", "run.raw");

        await _store.SaveAsync(
            new UploadRecord(missing, Destination.Append("run.raw").ToEncodedString(),
                10, 0, null, null, TransferState.Uploading, VerifyMethod.None, null, 1, null));

        // A coordinator whose monitored folder is on that drive, as it would be for a share
        // that has not come back yet.
        await using var coordinator = new TransferCoordinator(
            _server,
            _store,
            new TransferEngineOptions
            {
                LocalBaseDirectory = Path.Combine(UnmountedDrive(), "data"),
                DestinationRoot = Destination,
            });

        (await coordinator.RecoverInterruptedAsync()).ShouldBe(0);

        var row = await _store.GetAsync(missing);
        row!.State.ShouldBe(TransferState.Uploading, "waiting for the share, not gone");
        row.LastError.ShouldBeNull();
    }

    /// <summary>A drive letter this machine does not have, standing in for a share that is not
    /// mounted yet. Probing one answers immediately, where an invented UNC host would sit in an
    /// SMB connect timeout and make the suite slow and machine-dependent.</summary>
    private static string UnmountedDrive()
    {
        for (var letter = 'Z'; letter >= 'D'; letter--)
        {
            var root = $"{letter}:\\";

            if (!Path.Exists(root))
            {
                return root;
            }
        }

        throw new InvalidOperationException("Every drive letter is in use on this machine.");
    }

    [Fact]
    public async Task A_file_outside_the_monitored_folder_fails_in_words_a_person_can_act_on()
    {
        // Taken from a real ledger. Three rows sat on the Transfers tab reading
        //
        //   '\...\2026-04-Shock-ITP-Plasma\...F10_198.raw' is not inside
        //   '\...\2025-Levitt-AHA-StrokeEV-Plt1n2\Plate 1-QuantFiles'. (Parameter 'localFilePath')
        //
        // because the monitored folder had been changed and the ledger outlived the setting. The
        // general failure handler puts exception.Message into the row, so a framework sentence
        // naming a parameter was what a scientist read, against a file that would be retried
        // until its attempts ran out and could not have succeeded on any of them.
        var outside = Path.Combine(Path.GetDirectoryName(_local)!, "a-different-project");
        Directory.CreateDirectory(outside);

        var file = Path.Combine(outside, "run.raw");
        await File.WriteAllTextAsync(file, "acquired under a different setting");

        await RunWithAsync(NewCoordinator(), file);

        var row = await _store.GetAsync(file);
        row!.State.ShouldBe(TransferState.Failed);

        row.LastError.ShouldNotBeNull();
        row.LastError!.ShouldNotContain("Parameter", Case.Insensitive);
        row.LastError.ShouldNotContain("localFilePath");
        row.LastError.ShouldContain("not inside the folder being monitored");
        row.LastError.ShouldContain("has not been touched");

        _server.TotalCalls.ShouldBe(0, "nothing can be asked of the server about it");
        File.Exists(file).ShouldBeTrue("and the file itself is left alone");
    }

    [Fact]
    public async Task A_repaired_file_stops_carrying_the_marker_that_says_it_is_broken()
    {
        // Clearing this only on a successful upload stranded the file: a repaired acquisition
        // whose next attempt failed for an unrelated reason kept a marker meaning "broken",
        // which is held under every policy, so nothing offered it again and no setting could
        // reach it. It is cleared the moment the file reads as whole.
        var file = await WriteRawHeaderAsync("cut-short.raw", formatVersion: 66, padding: 0);

        await RunWithAsync(NewCoordinator(), file);
        (await _store.GetAsync(file))!.ConflictKind.ShouldBe(ConflictKind.LocalFileDamaged);

        // Re-copied complete, as somebody would after seeing it flagged -- and then the upload
        // fails for a reason that has nothing to do with the file. That combination is the whole
        // point: clearing the marker only on a successful upload leaves this row carrying
        // "broken", which is held under every policy, so no sweep offers it and no setting
        // reaches it. It never gets another attempt.
        var repaired = await WriteRawHeaderAsync("cut-short.raw", formatVersion: 66, padding: 4096);

        _server.FailUploadsBeforeSucceeding = 99;

        await RunWithAsync(NewCoordinator(), repaired);

        var row = await _store.GetAsync(repaired);
        row!.State.ShouldNotBe(TransferState.Verified, "the upload did fail");
        row.ConflictKind.ShouldBe(ConflictKind.Unknown, "but it is not held as damaged any more");
    }

    [Fact]
    public async Task A_row_whose_folder_was_deleted_under_a_reachable_drive_is_written_off()
    {
        // Keying the probe on each file's own folder read as "cannot look" for a folder somebody
        // had simply deleted, so the row was never written off and came back as interrupted work
        // on every start, for ever. A reachable drive with a missing folder is a real deletion.
        var gone = Path.Combine(_local, "deleted-acquisition", "run.raw");

        await _store.SaveAsync(
            new UploadRecord(gone, Destination.Append("run.raw").ToEncodedString(),
                10, 0, null, null, TransferState.Uploading, VerifyMethod.None, null, 1, null));

        var coordinator = NewCoordinator();

        (await coordinator.RecoverInterruptedAsync()).ShouldBe(0);

        var row = await _store.GetAsync(gone);
        row!.State.ShouldBe(TransferState.Failed);
        row.LastError.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_row_under_a_folder_that_cannot_be_seen_is_left_alone()
    {
        // The ledger outlives the setting: rows recorded while a different folder was watched are
        // still returned by recovery. Checking only today's monitored root said nothing about
        // whether yesterday's is reachable, so those rows were written off as deleted while the
        // data sat untouched on a share that simply was not mounted yet.
        var elsewhere = Path.Combine(UnmountedDrive(), "previously-watched", "run.raw");

        await _store.SaveAsync(
            new UploadRecord(elsewhere, Destination.Append("run.raw").ToEncodedString(),
                10, 0, null, null, TransferState.Uploading, VerifyMethod.None, null, 1, null));

        // The monitored folder is the ordinary local one and exists, so the whole-root check
        // passed and fell straight through to writing this row off.
        var coordinator = NewCoordinator();

        (await coordinator.RecoverInterruptedAsync()).ShouldBe(0);

        var row = await _store.GetAsync(elsewhere);
        row!.State.ShouldBe(TransferState.Uploading, "waiting for that drive, not gone");
        row.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task A_held_file_reaching_the_coordinator_directly_is_not_sent()
    {
        // The folder watcher and pbctl sync both enqueue without consulting the sweep, so a guard
        // that lived only in the sweep could be walked straight past -- and the ladder would then
        // re-save the row as an ordinary occupied-destination conflict, losing the marker that
        // was protecting it. Nothing is sent, and nothing is even asked of the server.
        var file = await WriteAsync("kept.raw", "mine");

        await _store.SaveAsync(
            UploadRecord.ForNewFile(
                LocalFileStamp.FromFile(file),
                Destination.Append("kept.raw").ToEncodedString())
            with
            {
                State = TransferState.Conflict,
                ConflictKind = ConflictKind.LocalFileDamaged,
                LastError = "This file ends before its data does.",
            });

        await RunWithAsync(NewCoordinator(policy: ConflictPolicy.Overwrite), file);

        _server.UploadCalls.ShouldBe(0, "a damaged file must not be sent");
        _server.TotalCalls.ShouldBe(0, "and it costs nothing to keep holding it");

        var row = await _store.GetAsync(file);
        row!.State.ShouldBe(TransferState.Conflict);
        row.ConflictKind.ShouldBe(ConflictKind.LocalFileDamaged, "the marker survives");
    }

    [Fact]
    public async Task An_interrupted_folder_upload_is_failed_with_an_honest_reason()
    {
        // A row from a version that sent folders as one archive. That has been withdrawn, so the
        // upload cannot be resumed -- and requeueing it would be silently dropped by the worker,
        // leaving the row interrupted for ever and returned by recovery on every start.
        var folder = Path.Combine(_local, "250314_HeLa.d");
        Directory.CreateDirectory(folder);

        await _store.SaveAsync(
            new UploadRecord(folder, Destination.Append("250314_HeLa.d.zip").ToEncodedString(),
                10, 0, null, null, TransferState.Uploading, VerifyMethod.None, null, 1, null));

        var coordinator = NewCoordinator();

        (await coordinator.RecoverInterruptedAsync()).ShouldBe(0);

        var row = await _store.GetAsync(folder);
        row!.State.ShouldBe(TransferState.Failed);
        row.LastError!.ShouldContain("folder as a single archive has been removed");
        Directory.Exists(folder).ShouldBeTrue("the folder itself is untouched");
    }

    [Fact]
    public async Task A_truncated_file_is_held_with_its_reason_recorded()
    {
        // The sweep can only hold a damaged row under every policy if the row says it is
        // damaged. That was inferred from message text once, and broke when a message was
        // reworded -- so it is stored, and this pins the store half of the contract.
        var file = await WriteRawHeaderAsync("cut-short.raw", formatVersion: 66, padding: 0);

        await RunWithAsync(NewCoordinator(), file);

        var row = await _store.GetAsync(file);
        row!.State.ShouldBe(TransferState.Conflict);
        row.ConflictKind.ShouldBe(ConflictKind.LocalFileDamaged);
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

    [Fact]
    public async Task Recovery_starts_workers_before_a_bounded_queue_can_fill()
    {
        // With verification off, Uploaded is terminal for a normal run but still returned by
        // recovery after a crash. The previous call order queued every such row before starting
        // a worker, so the 5,001st recovery entry blocked application startup forever.
        var files = new[]
        {
            await WriteAsync("first.raw", "first"),
            await WriteAsync("second.raw", "second"),
            await WriteAsync("third.raw", "third"),
        };

        foreach (var file in files)
        {
            await _store.SaveAsync(
                UploadRecord.ForNewFile(
                    LocalFileStamp.FromFile(file),
                    Destination.Append(Path.GetFileName(file)).ToEncodedString())
                with { State = TransferState.Uploaded });
        }

        await using var coordinator = NewCoordinator(verify: false, queueCapacity: 1);

        var recovered = await coordinator
            .RecoverInterruptedAsync()
            .WaitAsync(TimeSpan.FromSeconds(5));

        recovered.ShouldBe(files.Length);

        coordinator.CompleteAdding();
        var summary = await coordinator.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        summary.Uploaded.ShouldBe(files.Length);
    }

    [Fact]
    public async Task Disposing_after_recovery_joins_the_workers_it_started_without_running()
    {
        // A caller that never reaches RunAsync -- because something after recovery failed --
        // must not leave recovery's own workers running unobserved against a queue nobody will
        // ever complete or read from again.
        var file = await WriteAsync("interrupted.raw", "half sent");
        await _store.SaveAsync(
            UploadRecord.ForNewFile(LocalFileStamp.FromFile(file), Destination.Append("interrupted.raw").ToEncodedString())
                with { State = TransferState.Uploading });

        var coordinator = NewCoordinator();
        await coordinator.RecoverInterruptedAsync();

        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        (await _store.GetAsync(file))!.State.ShouldBe(TransferState.Verified);
    }

    [Fact]
    public async Task A_case_only_rename_settles_after_being_sent_to_its_new_remote_name()
    {
        // Windows identifies the two local paths as the same ledger key, but Panorama is
        // case-sensitive. Resolve from the current path and read from it too, or every sweep
        // re-offers the file and the worker then tries to open the old spelling.
        var original = await WriteAsync("run.raw", "acquisition");
        await RunWithAsync(NewCoordinator(), original);

        var temporary = Path.Combine(_local, "rename-in-progress.raw");
        var renamed = Path.Combine(_local, "RUN.raw");
        File.Move(original, temporary);
        File.Move(temporary, renamed);

        var scanner = new ReconciliationScanner(
            _store,
            new ReconciliationOptions
            {
                Root = _local,
                DestinationRoot = Destination,
                Filter = new CandidateFilter([".raw"]),
            });

        var offered = new List<string>();
        await scanner.SweepAsync((path, _) =>
        {
            offered.Add(path);
            return Task.CompletedTask;
        });

        offered.ShouldBe([renamed]);

        var summary = await RunWithAsync(NewCoordinator(), [.. offered]);
        summary.Uploaded.ShouldBe(1);
        _server.Content(Destination.Append("RUN.raw")).ShouldNotBeNull();

        var after = new List<string>();
        await scanner.SweepAsync((path, _) =>
        {
            after.Add(path);
            return Task.CompletedTask;
        });

        after.ShouldBeEmpty("the renamed file is now settled at its case-correct destination");
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
