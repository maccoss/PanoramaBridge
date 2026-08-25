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

    // -- Acting on a decision somebody made about a conflict ----------------------------------

    [Fact]
    public async Task A_conflict_resolved_as_overwrite_replaces_the_remote_copy()
    {
        var file = await WriteAsync("run.raw", "the local one");
        _server.Seed(Destination.Append("run.raw"), "something else entirely"u8.ToArray());

        // First pass: the policy is Ask, so it is held rather than sent.
        await RunWithAsync(NewCoordinator(), file);
        (await _store.GetAsync(file))!.State.ShouldBe(TransferState.Conflict);

        await _store.ResolveConflictAsync(file, ConflictResolution.Overwrite);

        await RunWithAsync(NewCoordinator(), file);

        var after = await _store.GetAsync(file);
        after!.State.ShouldBe(TransferState.Verified);

        // The decision is consumed, not left standing. Otherwise a one-off "overwrite this"
        // quietly becomes a permanent policy for one file that nobody remembers agreeing to.
        after.Resolution.ShouldBe(ConflictResolution.None);
        after.HasPendingResolution.ShouldBeFalse();
    }

    [Fact]
    public async Task A_conflict_resolved_as_rename_sends_it_under_the_new_name()
    {
        var file = await WriteAsync("run.raw", "the local one");
        _server.Seed(Destination.Append("run.raw"), "something else entirely"u8.ToArray());

        await RunWithAsync(NewCoordinator(), file);

        await _store.ResolveConflictAsync(file, ConflictResolution.Rename, "run (2).raw");

        await RunWithAsync(NewCoordinator(), file);

        var after = await _store.GetAsync(file);
        after!.State.ShouldBe(TransferState.Verified);

        // The row now points at where the bytes actually went. A verified row naming the
        // destination it was refused at would be worse than no row.
        // Percent-encoded, parentheses included, which is RemotePath doing its job.
        after.RemotePath.ShouldEndWith("run%20%282%29.raw");

        // The name is kept, not cleared. It is where this file lives now, and everything that
        // asks "is this accounted for?" resolves the destination through it. Clearing it made the
        // sweep compare a row verified at run (2).raw against a destination of run.raw, decide
        // the row described somewhere else, and offer the file again -- for ever.
        after.RenameTo.ShouldBe("run (2).raw");

        // Spent, though: the instruction was carried out.
        after.Resolution.ShouldBe(ConflictResolution.None);
    }

    [Fact]
    public async Task Renaming_leaves_the_copy_that_was_already_there_untouched()
    {
        var file = await WriteAsync("run.raw", "the local one");
        var occupied = Destination.Append("run.raw");
        _server.Seed(occupied, "something else entirely"u8.ToArray());

        await RunWithAsync(NewCoordinator(), file);
        await _store.ResolveConflictAsync(file, ConflictResolution.Rename, "run (2).raw");
        await RunWithAsync(NewCoordinator(), file);

        // The whole reason somebody picks Rename over Overwrite.
        _server.Content(occupied).ShouldBe("something else entirely"u8.ToArray());
    }

    [Fact]
    public async Task The_rename_policy_sends_it_alongside_without_asking()
    {
        // This setting shipped in the first release and did nothing: the ladder returned a
        // conflict saying "a new name is needed" and nothing ever picked one, so choosing Rename
        // behaved exactly like Ask and files waited for a decision nobody knew to make.
        var file = await WriteAsync("run.raw", "the local one");
        var occupied = Destination.Append("run.raw");
        _server.Seed(occupied, "something else entirely"u8.ToArray());

        await RunWithAsync(NewCoordinator(policy: ConflictPolicy.Rename), file);

        var after = await _store.GetAsync(file);
        after!.State.ShouldBe(TransferState.Verified);
        after.RemotePath.ShouldEndWith("run%20%282%29.raw");

        // The copy already there is what the policy exists to preserve.
        _server.Content(occupied).ShouldBe("something else entirely"u8.ToArray());
        _server.Content(Destination.Append("run (2).raw")).ShouldBe("the local one"u8.ToArray());
    }

    [Fact]
    public async Task Replacing_a_renamed_file_replaces_the_copy_it_actually_made()
    {
        // Sent alongside first, so the row lives at run (2).raw.
        var file = await WriteAsync("run.raw", "mine");
        var original = Destination.Append("run.raw");
        _server.Seed(original, "somebody else's"u8.ToArray());

        await RunWithAsync(NewCoordinator(), file);
        await _store.ResolveConflictAsync(file, ConflictResolution.Rename, "run (2).raw");
        await RunWithAsync(NewCoordinator(), file);

        // The local file changes, so its renamed copy is now stale and conflicts afresh.
        await File.WriteAllTextAsync(file, "mine, edited");
        await RunWithAsync(NewCoordinator(), file);

        await _store.ResolveConflictAsync(file, ConflictResolution.Overwrite);
        await RunWithAsync(NewCoordinator(), file);

        // Replace means "replace the copy this row made", not "replace the file somebody chose to
        // preserve when they picked Rename in the first place".
        _server.Content(original).ShouldBe("somebody else's"u8.ToArray());
        _server.Content(Destination.Append("run (2).raw"))
            .ShouldBe("mine, edited"u8.ToArray());
    }

    [Fact]
    public async Task A_rename_that_could_not_be_carried_out_still_knows_where_the_file_lives()
    {
        // Sent alongside once, so the row lives at run (2).raw.
        var file = await WriteAsync("run.raw", "mine");
        var original = Destination.Append("run.raw");
        _server.Seed(original, "somebody else's"u8.ToArray());

        await RunWithAsync(NewCoordinator(), file);
        await _store.ResolveConflictAsync(file, ConflictResolution.Rename, "run (2).raw");
        await RunWithAsync(NewCoordinator(), file);

        // It changes and conflicts again; the user picks Rename a second time, but the proposed
        // name turns out to be occupied, so the rename cannot be carried out.
        await File.WriteAllTextAsync(file, "mine, edited");
        await RunWithAsync(NewCoordinator(), file);

        _server.Seed(Destination.Append("run (3).raw"), "someone else again"u8.ToArray());
        await _store.ResolveConflictAsync(file, ConflictResolution.Rename, "run (3).raw");
        await RunWithAsync(NewCoordinator(), file);

        // Held, as it should be. But it must not have forgotten where its own copy is: clearing
        // that sends the next Replace to run.raw and destroys the file the user chose to keep.
        var after = await _store.GetAsync(file);
        after!.State.ShouldBe(TransferState.Conflict);
        after.RenameTo.ShouldBe("run (2).raw");

        await _store.ResolveConflictAsync(file, ConflictResolution.Overwrite);
        await RunWithAsync(NewCoordinator(), file);

        _server.Content(original).ShouldBe("somebody else's"u8.ToArray());
    }

    [Fact]
    public async Task A_rename_never_overwrites_something_that_arrived_at_the_new_name()
    {
        var file = await WriteAsync("run.raw", "the local one");
        _server.Seed(Destination.Append("run.raw"), "something else entirely"u8.ToArray());

        await RunWithAsync(NewCoordinator(), file);
        await _store.ResolveConflictAsync(file, ConflictResolution.Rename, "run (2).raw");

        // Somebody else put a file at the new name between the decision and the transfer. That
        // gap can be minutes or a reboot, and "send it alongside" must not quietly become
        // "replace whatever is there now" -- which would destroy data while carrying out an
        // instruction whose entire purpose was to avoid destroying data.
        var target = Destination.Append("run (2).raw");
        _server.Seed(target, "somebody else's file"u8.ToArray());

        await RunWithAsync(NewCoordinator(), file);

        _server.Content(target).ShouldBe("somebody else's file"u8.ToArray());

        var after = await _store.GetAsync(file);
        after!.State.ShouldBe(TransferState.Conflict);
        after.LastError!.ShouldContain("occupied as well");

        // The spent decision is cleared, so the next pass asks rather than retrying a name that
        // is now known to be taken.
        after.Resolution.ShouldBe(ConflictResolution.None);
    }

    [Fact]
    public async Task A_decision_is_honoured_without_asking_the_policy_again()
    {
        var file = await WriteAsync("run.raw", "the local one");
        _server.Seed(Destination.Append("run.raw"), "something else entirely"u8.ToArray());

        await RunWithAsync(NewCoordinator(), file);
        await _store.ResolveConflictAsync(file, ConflictResolution.Overwrite);

        // Still Ask, which is what raised the conflict in the first place. If the engine
        // consulted it again the file would simply be held a second time and the decision would
        // vanish -- silently, which is the part that would make it hard to find.
        await RunWithAsync(NewCoordinator(policy: ConflictPolicy.Ask), file);

        (await _store.GetAsync(file))!.State.ShouldBe(TransferState.Verified);
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

    // -- directory acquisitions ------------------------------------------------------------

    /// <summary>Writes a Bruker-shaped acquisition folder under the watched directory.</summary>
    private string Acquisition(string name, params (string Name, string Content)[] files)
    {
        var folder = Path.Combine(_local, name);
        Directory.CreateDirectory(folder);

        foreach (var (file, content) in files)
        {
            var path = Path.Combine(folder, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        return folder;
    }

    [Fact]
    public async Task A_bruker_acquisition_arrives_as_one_verified_archive()
    {
        // The whole feature, end to end. A .d is a directory locally and one .d.zip remotely,
        // which is how this lab already stores them -- and it is what makes the transfer atomic
        // without any machinery: the archive either arrives and verifies or it does not.
        var folder = Acquisition(
            "250314_HeLa_DIA_01.d",
            ("analysis.tdf", "the sqlite index"),
            ("analysis.tdf_bin", "the binary data"));

        var summary = await RunWithAsync(NewCoordinator(), folder);

        summary.Uploaded.ShouldBe(1);

        var remote = Destination.Append("250314_HeLa_DIA_01.d.zip");
        _server.Content(remote).ShouldNotBeNull("the acquisition should be there as one archive");

        // And it is a real archive holding the acquisition, not merely bytes of the right length.
        using var zip = new System.IO.Compression.ZipArchive(
            new MemoryStream(_server.Content(remote)!));

        zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(["analysis.tdf", "analysis.tdf_bin"]);
    }

    [Fact]
    public async Task The_ledger_records_the_folder_and_marks_it_a_dataset()
    {
        // Keyed on the folder, because that is the thing the user has and can point at. The
        // archive is a means of carrying it and does not outlive the transfer.
        var folder = Acquisition("run_002.d", ("analysis.tdf", "index"));

        await RunWithAsync(NewCoordinator(), folder);

        var record = await _store.GetAsync(folder);

        record.ShouldNotBeNull();
        record!.IsDataset.ShouldBeTrue();
        record.State.ShouldBe(TransferState.Verified);
        record.RemotePath.ShouldEndWith("run_002.d.zip");
    }

    [Fact]
    public async Task The_working_archive_is_removed_afterwards()
    {
        // It is built inside the folder being monitored and is six gigabytes in real life.
        var folder = Acquisition("run_003.d", ("analysis.tdf", "index"));

        await RunWithAsync(NewCoordinator(), folder);

        File.Exists(DatasetArchive.StagingPathFor(folder)).ShouldBeFalse();
    }

    [Fact]
    public async Task An_unchanged_acquisition_is_not_packed_again()
    {
        // Packing six gigabytes to discover it was already there would be the most expensive way
        // possible to answer that question, so the ledger is consulted first.
        var folder = Acquisition("run_004.d", ("analysis.tdf", "index"));

        await RunWithAsync(NewCoordinator(), folder);
        _server.Reset();

        var summary = await RunWithAsync(NewCoordinator(), folder);

        summary.Skipped.ShouldBe(1);
        _server.UploadCalls.ShouldBe(0, "nothing changed, so nothing should have been sent");
    }

    [Fact]
    public async Task A_changed_acquisition_is_packed_and_sent_again()
    {
        var folder = Acquisition("run_005.d", ("analysis.tdf", "index"));
        await RunWithAsync(NewCoordinator(), folder);

        await File.WriteAllTextAsync(
            Path.Combine(folder, "analysis.tdf_bin"), "a file that was not there before");

        var summary = await RunWithAsync(NewCoordinator(), folder);

        summary.Uploaded.ShouldBe(1, "the acquisition grew, so it is a new version");
    }

    [Fact]
    public async Task The_acquisition_itself_is_never_modified()
    {
        var folder = Acquisition("run_006.d", ("analysis.tdf", "the sqlite index"));
        var file = Path.Combine(folder, "analysis.tdf");

        var before = await File.ReadAllBytesAsync(file);
        var written = File.GetLastWriteTimeUtc(file);

        await RunWithAsync(NewCoordinator(), folder);

        File.ReadAllBytes(file).ShouldBe(before);
        File.GetLastWriteTimeUtc(file).ShouldBe(written);
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
    public async Task A_decision_to_overwrite_does_not_license_sending_a_short_file()
    {
        // The resolution path returns before the check every other route to the server passes
        // through, so it was the one way to upload a file nobody had read. Two ways in: a row
        // migrated from a build before ConflictKind existed defaults to Unknown, so the view
        // offers Replace for an acquisition held precisely because it is damaged; and a file can
        // be truncated between the conflict being recorded and the decision being acted on.
        //
        // A decision to overwrite says which copy is wanted. It is not a warrant to send one that
        // is broken.
        var file = await WriteRawHeaderAsync("cut-short.raw", formatVersion: 66, padding: 0);
        _server.Seed(Destination.Append("cut-short.raw"), "the good copy"u8.ToArray());

        await RunWithAsync(NewCoordinator(), file);

        // Recorded as Unknown, exactly as a row written by the previous release would be.
        var held = await _store.GetAsync(file);
        await _store.SaveAsync(held! with { ConflictKind = ConflictKind.Unknown });

        await _store.ResolveConflictAsync(file, ConflictResolution.Overwrite);
        await RunWithAsync(NewCoordinator(), file);

        _server.Content(Destination.Append("cut-short.raw"))
            .ShouldBe("the good copy"u8.ToArray(), "the short file must never reach the server");

        var after = await _store.GetAsync(file);
        after!.State.ShouldBe(TransferState.Conflict);
        after.ConflictKind.ShouldBe(ConflictKind.LocalFileDamaged);
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
