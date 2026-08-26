using PanoramaBridge.Core.Monitoring;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;
using PanoramaBridge.Core.WebDav;
using PanoramaBridge.Tests.TestDoubles;

namespace PanoramaBridge.Tests.Monitoring;

/// <summary>
/// The periodic walk of the monitored tree.
/// </summary>
/// <remarks>
/// This is the mechanism continuous monitoring rests on, so the tests are as much about what it
/// declines to do as about what it finds. A sweep that re-offered everything it saw would put the
/// whole tree through the readiness gate every quarter of an hour, opening every file on the disk
/// an instrument is writing to.
/// </remarks>
public sealed class ReconciliationScannerTests : IAsyncDisposable
{
    private static readonly RemotePath Destination =
        RemotePath.Parse("/_webdav/MacCoss/maccoss/@files/uploads/");

    private readonly CountingStateStore _store = new();
    private readonly string _root = Directory.CreateTempSubdirectory("pb-sweep-").FullName;

    private ReconciliationScanner NewScanner(
        RemotePath? destination = null,
        bool includeSubdirectories = true,
        int maxUploadAttempts = 5,
        string[]? extensions = null,
        ConflictPolicy conflictPolicy = ConflictPolicy.Ask) =>
        new(
            _store,
            new ReconciliationOptions
            {
                Root = _root,
                DestinationRoot = destination ?? Destination,
                Filter = new CandidateFilter(extensions ?? [".raw"]),
                IncludeSubdirectories = includeSubdirectories,
                MaxUploadAttempts = maxUploadAttempts,
                ConflictPolicy = conflictPolicy,
            });

    private string Write(string relative, string content = "acquisition")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Records a file as uploaded and confirmed against the server's own hash.</summary>
    private async Task RecordAsync(
        string path,
        TransferState state = TransferState.Verified,
        VerifyMethod verified = VerifyMethod.ServerMd5,
        int attempts = 1,
        RemotePath? destination = null,
        ConflictKind kind = ConflictKind.Unknown)
    {
        var stamp = LocalFileStamp.FromFile(path);
        var remote = PathSafety
            .ResolveDestination(_root, path, destination ?? Destination)
            .ToEncodedString();

        await _store.SaveAsync(new UploadRecord(
            LocalPath: stamp.Path,
            RemotePath: remote,
            Length: stamp.Length,
            LastWriteUnixMs: stamp.LastWriteUnixMs,
            Md5: "d41d8cd98f00b204e9800998ecf8427e",
            Sha256: null,
            State: state,
            VerifyMethod: verified,
            VerifiedUtc: DateTimeOffset.UtcNow,
            Attempts: attempts,
            LastError: state == TransferState.Failed ? "The server refused it." : null,
            ConflictKind: kind));
    }

    private static async Task<(SweepResult Result, List<string> Offered)> SweepAsync(
        ReconciliationScanner scanner)
    {
        var offered = new List<string>();

        var result = await scanner.SweepAsync((path, _) =>
        {
            offered.Add(path);
            return Task.CompletedTask;
        });

        return (result, offered);
    }

    [Fact]
    public async Task A_directory_is_walked_into_as_an_ordinary_folder()
    {
        Write(Path.Combine("250314_HeLa.d", "analysis.tdf"), "the index");
        Write(Path.Combine("250314_HeLa.d", "analysis.tdf_bin"), "the data");

        // A file inside that does match, so "walked into" is provable rather than assumed:
        // asserting only that the folder was not offered would pass if the sweep found nothing at
        // all, or threw and was swallowed.
        Write(Path.Combine("250314_HeLa.d", "extra.raw"), "a thermo file inside the folder");

        var (_, offered) = await SweepAsync(NewScanner(extensions: [".raw", ".d"]));

        offered.ShouldNotContain(Path.Combine(_root, "250314_HeLa.d"));
        offered.ShouldContain(Path.Combine(_root, "250314_HeLa.d", "extra.raw"));
    }

    [Fact]
    public async Task A_file_the_ledger_has_never_seen_is_offered()
    {
        var path = Write("run1.raw");

        var (result, offered) = await SweepAsync(NewScanner());

        offered.ShouldBe([path]);
        result.Examined.ShouldBe(1);
        result.Offered.ShouldBe(1);
        result.Failed.ShouldBeFalse();
    }

    [Fact]
    public async Task Files_the_filter_rejects_are_never_even_looked_up()
    {
        Write("notes.txt");
        Write("~working.raw");
        var wanted = Write("run1.raw");

        var (result, offered) = await SweepAsync(NewScanner());

        offered.ShouldBe([wanted]);
        result.Examined.ShouldBe(1, "the other two are not data");
        _store.PathsLookedUp.ShouldBe(1, "filtering happens before the ledger is consulted");
    }

    [Fact]
    public async Task A_verified_file_costs_one_lookup_and_nothing_else()
    {
        // The case that has to stay free. This runs every quarter of an hour for the whole life
        // of the application, over a directory in which nothing has changed.
        var path = Write("run1.raw");
        await RecordAsync(path);
        _store.Reset();

        var (result, offered) = await SweepAsync(NewScanner());

        offered.ShouldBeEmpty();
        result.AlreadyAccountedFor.ShouldBe(1);
        _store.BatchedGets.ShouldBe(1, "one statement for the whole batch");
        _store.Gets.ShouldBe(0, "and never a lookup per file");
        _store.Saves.ShouldBe(0, "a sweep that finds nothing must not write anything");
    }

    [Fact]
    public async Task A_file_that_changed_since_it_was_verified_comes_back()
    {
        var path = Write("run1.raw");
        await RecordAsync(path);

        // Re-acquired, or corrected, or copied over. Either way it is not the file that was
        // verified, whatever the ledger says about the old one.
        await Task.Delay(20);
        File.WriteAllText(path, "a longer acquisition than before");

        var (_, offered) = await SweepAsync(NewScanner());

        offered.ShouldBe([path]);
    }

    [Fact]
    public async Task An_upload_that_was_never_verified_is_not_treated_as_finished()
    {
        // Reaching the server is not the same as being proven to have arrived intact. Only the
        // server's own hash settles it, so a row that never got that far has to be offered again.
        var path = Write("run1.raw");
        await RecordAsync(path, TransferState.Uploaded, VerifyMethod.SizeOnly);

        var (_, offered) = await SweepAsync(NewScanner());

        offered.ShouldBe([path]);
    }

    [Fact]
    public async Task Changing_the_destination_makes_a_verified_file_need_sending_again()
    {
        // A verified row proves the bytes reached somewhere. It says nothing about the folder
        // that is configured now, and the previous implementation got exactly this wrong.
        var path = Write("run1.raw");
        await RecordAsync(path);

        var elsewhere = RemotePath.Parse("/_webdav/MacCoss/maccoss/@files/somewhere-else/");
        var (_, offered) = await SweepAsync(NewScanner(destination: elsewhere));

        offered.ShouldBe([path]);
    }

    [Fact]
    public async Task A_conflict_waits_for_a_person_rather_than_being_asked_about_every_sweep()
    {
        // Re-running the decision ladder over it would spend a request per sweep on an answer
        // that has not changed. It comes back when the file does.
        var path = Write("run1.raw");
        await RecordAsync(path, TransferState.Conflict, VerifyMethod.None);

        var (_, offered) = await SweepAsync(NewScanner());
        offered.ShouldBeEmpty();

        await Task.Delay(20);
        File.WriteAllText(path, "changed locally, so worth asking about again");

        var (_, afterChange) = await SweepAsync(NewScanner());
        afterChange.ShouldBe([path]);
    }

    [Fact]
    public async Task A_conflict_the_ladder_skipped_by_policy_is_not_asked_about_again()
    {
        // The ladder resolves a Skip conflict to State.Skipped with no server hash, so it never
        // matches the settled check above. Without an arm for it here, the file went through the
        // whole ladder again on every sweep for as long as the policy stayed Skip -- another
        // listing, and sometimes another collection hash, to reach the same answer as last time.
        var path = Write("run1.raw");
        await RecordAsync(path, TransferState.Skipped, VerifyMethod.None);

        var (_, offered) = await SweepAsync(NewScanner(conflictPolicy: ConflictPolicy.Skip));

        offered.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ConflictPolicy.Skip, true)]
    [InlineData(ConflictPolicy.Overwrite, true)]
    [InlineData(ConflictPolicy.Ask, false)]
    public async Task Whether_a_damaged_file_is_offered_follows_the_policy_like_any_held_file(
        ConflictPolicy policy, bool expectOffered)
    {
        // There is no arm here for damage. Holding one in the sweep as well kept it off the queue,
        // so nothing reported it, and the Transfers tab and the attention count are built from
        // reports alone -- the coordinator turns it back before the ladder, so offering it costs a
        // queue trip and not one request, and that report is what keeps it visible.
        //
        // Ask is included because it is the default and it behaves differently: a held file is not
        // offered while the answer is still "ask me", which is true of every held file and not
        // special to damage. The first version of this test pinned only Skip and Overwrite, so it
        // passed while claiming something about the case it never ran.
        var path = Write("short.raw");
        await RecordAsync(path, TransferState.Conflict, VerifyMethod.None,
            kind: ConflictKind.LocalFileDamaged);

        var (_, offered) = await SweepAsync(NewScanner(conflictPolicy: policy));

        if (expectOffered)
        {
            offered.ShouldBe([path]);
        }
        else
        {
            offered.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task A_name_the_server_would_mangle_is_offered_once_and_not_for_ever()
    {
        // These fail before an upload begins, and attempts is only counted when one starts, so
        // the Failed arm never retires them: the file was offered on every sweep for ever, failing
        // identically each time and adding to the failure count each time.
        var path = Write("run;rep1.raw");

        var first = await SweepAsync(NewScanner());
        first.Offered.ShouldBe([path], "offered once, so the failure is recorded against it");

        // Written directly: RecordAsync resolves a destination, which is the very thing this
        // name cannot have.
        var stamp = LocalFileStamp.FromFile(path);
        await _store.SaveAsync(new UploadRecord(
            LocalPath: stamp.Path,
            RemotePath: string.Empty,
            Length: stamp.Length,
            LastWriteUnixMs: stamp.LastWriteUnixMs,
            Md5: null,
            Sha256: null,
            State: TransferState.Failed,
            VerifyMethod: VerifyMethod.None,
            VerifiedUtc: null,
            Attempts: 0,
            LastError: "contains a semicolon"));

        var second = await SweepAsync(NewScanner());
        second.Offered.ShouldBeEmpty("and not again while the file and the setting are unchanged");
    }

    [Fact]
    public async Task A_damaged_file_is_offered_again_once_it_changes()
    {
        // The way out is fixing the file, not the policy. Re-copying the acquisition changes the
        // stamp, and the stamp check releases the row before the state is even considered.
        var path = Write("short.raw");
        await RecordAsync(path, TransferState.Conflict, VerifyMethod.None,
            kind: ConflictKind.LocalFileDamaged);

        await File.AppendAllTextAsync(path, "the rest of the acquisition");

        var (_, offered) = await SweepAsync(
            NewScanner(conflictPolicy: ConflictPolicy.Overwrite));

        offered.ShouldBe([path]);
    }

    [Fact]
    public async Task A_skipped_conflict_is_asked_about_again_once_the_policy_changes()
    {
        // Skip is not a permanent answer. Changing the setting has to be able to clear the
        // backlog, so a row it produced must not be held the way an Ask conflict is.
        var path = Write("run1.raw");
        await RecordAsync(path, TransferState.Skipped, VerifyMethod.None);

        var (_, offered) = await SweepAsync(NewScanner(conflictPolicy: ConflictPolicy.Overwrite));

        offered.ShouldBe([path]);
    }

    [Fact]
    public async Task A_failed_upload_is_retried_until_its_attempts_run_out()
    {
        // An unattended monitor has to recover on its own from a server that was down overnight.
        // It must not, however, keep re-sending a seven-gigabyte file the server keeps refusing.
        var path = Write("run1.raw");
        await RecordAsync(path, TransferState.Failed, VerifyMethod.None, attempts: 2);

        var (_, retried) = await SweepAsync(NewScanner(maxUploadAttempts: 5));
        retried.ShouldBe([path]);

        await RecordAsync(path, TransferState.Failed, VerifyMethod.None, attempts: 5);

        var (_, exhausted) = await SweepAsync(NewScanner(maxUploadAttempts: 5));
        exhausted.ShouldBeEmpty("it stays in the ledger as failed, and stops costing anything");
    }

    [Fact]
    public async Task A_failure_that_happened_before_the_upload_started_keeps_being_retried()
    {
        // Attempts count uploads that actually began, so a row with none of them failed while
        // the ladder was still deciding -- an unreachable server, typically. Retrying that costs
        // a ledger read, and not retrying it means an overnight outage stops transfers for good.
        var path = Write("run1.raw");
        await RecordAsync(path, TransferState.Failed, VerifyMethod.None, attempts: 0);

        var (_, offered) = await SweepAsync(NewScanner(maxUploadAttempts: 5));

        offered.ShouldBe([path]);
    }

    [Fact]
    public async Task Subdirectories_are_walked_only_when_the_setting_says_so()
    {
        Write("run1.raw");
        var nested = Write(Path.Combine("2026", "batch7", "run2.raw"));

        var (_, shallow) = await SweepAsync(NewScanner(includeSubdirectories: false));
        shallow.ShouldNotContain(nested);

        var (_, deep) = await SweepAsync(NewScanner(includeSubdirectories: true));
        deep.ShouldContain(nested);
    }

    [Fact]
    public async Task A_monitored_folder_that_is_not_reachable_is_reported_rather_than_thrown()
    {
        // A share that is offline is the normal case here, not an exceptional one, and it must
        // not stop monitoring: the next sweep has to find the folder back again.
        var scanner = new ReconciliationScanner(
            _store,
            new ReconciliationOptions
            {
                Root = Path.Combine(_root, "not-mounted"),
                DestinationRoot = Destination,
                Filter = CandidateFilter.Everything,
            });

        var (result, offered) = await SweepAsync(scanner);

        result.Failed.ShouldBeTrue();

        var problem = result.Problem.ShouldNotBeNull();
        problem.ShouldContain("not-mounted");
        // The message has to say it recovers by itself, or the reader reaches for the manual.
        problem.ShouldContain("resume by themselves");

        offered.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_large_tree_is_looked_up_in_batches_rather_than_one_file_at_a_time()
    {
        // Two hundred thousand files is a realistic instrument volume. The difference between a
        // batched read and a read per file is the difference between a sweep costing a second
        // and one costing minutes.
        for (var i = 0; i < 501; i++)
        {
            Write($"run{i:D4}.raw");
        }

        var (result, offered) = await SweepAsync(NewScanner());

        result.Examined.ShouldBe(501);
        offered.Count.ShouldBe(501);
        _store.PathsLookedUp.ShouldBe(501);
        _store.BatchedGets.ShouldBe(2, "batches of five hundred, so two statements for 501 files");
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A scratch folder left behind is better than a failed run.
        }
    }
}
