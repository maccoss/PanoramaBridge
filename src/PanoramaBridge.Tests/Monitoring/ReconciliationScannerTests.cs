using PanoramaBridge.Core.Monitoring;
using PanoramaBridge.Core.Storage;
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
        string[]? extensions = null) =>
        new(
            _store,
            new ReconciliationOptions
            {
                Root = _root,
                DestinationRoot = destination ?? Destination,
                Filter = new CandidateFilter(extensions ?? [".raw"]),
                IncludeSubdirectories = includeSubdirectories,
                MaxUploadAttempts = maxUploadAttempts,
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
        RemotePath? destination = null)
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
            IsDataset: false));
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

    // -- directory acquisitions ----------------------------------------------------------------

    [Fact]
    public async Task A_bruker_folder_is_offered_as_one_thing()
    {
        // A .d is one acquisition, not a folder of candidates. It reaches Panorama as a single
        // .d.zip, so the sweep has to hand over the folder itself.
        Write(Path.Combine("250314_HeLa_DIA_01.d", "analysis.tdf"), "the sqlite index");
        Write(Path.Combine("250314_HeLa_DIA_01.d", "analysis.tdf_bin"), "the binary data");

        var (_, offered) = await SweepAsync(NewScanner(extensions: [".raw", ".d"]));

        offered.ShouldHaveSingleItem();
        offered[0].ShouldEndWith("250314_HeLa_DIA_01.d");
    }

    [Fact]
    public async Task The_files_inside_a_bruker_folder_are_never_offered_separately()
    {
        // The property that matters. Descending into a .d is how a folder still being written
        // transfers in pieces -- and a piece of an acquisition on the server is worse than
        // nothing there, because it looks like a complete upload.
        Write(Path.Combine("run.d", "analysis.tdf"), "index");
        Write(Path.Combine("run.d", "inner", "buried.raw"), "a file that matches the filter");

        var (_, offered) = await SweepAsync(NewScanner(extensions: [".raw", ".d"]));

        offered.ShouldHaveSingleItem();
        offered[0].ShouldEndWith("run.d");
        offered.ShouldNotContain(p => p.EndsWith("buried.raw", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_folder_the_user_did_not_ask_for_is_still_walked_into()
    {
        // Only extensions the user listed become acquisitions. An ordinary subfolder is still a
        // subfolder, and the files inside it are still candidates.
        Write(Path.Combine("2026-03-14", "run.raw"), "an ordinary acquisition");

        var (_, offered) = await SweepAsync(NewScanner(extensions: [".raw", ".d"]));

        offered.ShouldHaveSingleItem();
        offered[0].ShouldEndWith("run.raw");
    }

    [Fact]
    public async Task An_empty_bruker_folder_is_not_offered()
    {
        // Created but not written into. Offering it would pack an archive of nothing.
        Directory.CreateDirectory(Path.Combine(_root, "empty.d"));

        var (_, offered) = await SweepAsync(NewScanner(extensions: [".raw", ".d"]));

        offered.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_working_archive_beside_an_acquisition_is_not_offered()
    {
        // It is built inside the folder being swept. The tilde is what keeps it out, and this is
        // the test that says so at the level where it would do damage.
        Write(Path.Combine("run.d", "analysis.tdf"), "index");
        File.WriteAllText(Path.Combine(_root, "~run.d.zip"), "a working archive");

        var (_, offered) = await SweepAsync(NewScanner(extensions: [".raw", ".d", ".zip"]));

        offered.ShouldHaveSingleItem();
        offered[0].ShouldEndWith("run.d");
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
