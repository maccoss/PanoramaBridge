using PanoramaBridge.Core.Storage;

namespace PanoramaBridge.Tests.Storage;

/// <summary>Contract tests for the update-only ledger transitions.</summary>
public sealed class SqliteStateStoreTests : IAsyncLifetime
{
    /// <summary>The destination these tests save against, so a lookup can name the row.</summary>
    private const string Uploaded = "/_webdav/uploads/run.raw";

    private static LedgerKey Key(string localPath, string remote = Uploaded) =>
        new(localPath, remote);

    private readonly SqliteStateStore _store = SqliteStateStore.InMemory();

    [Fact]
    public async Task A_state_transition_requires_an_existing_row()
    {
        const string path = @"C:\data\unknown.raw";

        await Should.ThrowAsync<InvalidOperationException>(() =>
            _store.SetStateAsync(Key(path), TransferState.Uploading));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            _store.MarkVerifiedAsync(Key(path), VerifyMethod.ServerMd5, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_failure_can_be_forgotten()
    {
        // A failure can outlive every reason to retry it. Two sequence files failed against a
        // server that could not answer in time; the application then learned to skip that kind of
        // file, so nothing would ever attempt them again -- and nothing would ever clear them.
        const string remote = "/_webdav/uploads/9f2c.sld";

        var stamp = new LocalFileStamp(@"C:\data\9f2c.sld", 42, 1);
        await _store.SaveAsync(UploadRecord.ForNewFile(stamp, remote));
        await _store.SetErrorAsync(Key(stamp.Path, remote), "the operation was canceled");
        await _store.SetStateAsync(Key(stamp.Path, remote), TransferState.Failed);

        (await _store.ForgetAsync(Key(stamp.Path, remote))).ShouldBeTrue();
        (await _store.GetAsync(Key(stamp.Path, remote))).ShouldBeNull();
    }

    [Theory]
    [InlineData(TransferState.Verified)]
    [InlineData(TransferState.Uploaded)]
    [InlineData(TransferState.Skipped)]
    public async Task A_row_for_a_file_that_reached_the_server_is_never_forgotten(
        TransferState state)
    {
        // The one that matters. This ledger is the record of what is on Panorama, and on a
        // rebuilt machine it is the only evidence: a row saying a file is there must not be
        // removable by a button meant for clearing failures. Enforced here and not only in the
        // view model, because the view model is not what the next caller will go through.
        var stamp = new LocalFileStamp(@"C:\data\run.raw", 42, 1);
        await _store.SaveAsync(UploadRecord.ForNewFile(stamp, "/_webdav/uploads/run.raw"));
        await _store.SetStateAsync(Key(stamp.Path), TransferState.Uploading);
        await _store.SetStateAsync(Key(stamp.Path), state);

        (await _store.ForgetAsync(Key(stamp.Path))).ShouldBeFalse();

        var row = await _store.GetAsync(Key(stamp.Path));
        row.ShouldNotBeNull();
        row!.State.ShouldBe(state);
    }

    [Fact]
    public async Task Forgetting_a_row_that_is_not_there_is_not_an_error()
    {
        // Two people looking at the same list, or a refresh in between. Nothing to remove is the
        // outcome that was wanted, not a failure to report.
        (await _store.ForgetAsync(Key(@"C:\data\never-existed.raw"))).ShouldBeFalse();
    }

    [Fact]
    public async Task A_saved_row_can_move_through_each_update_only_transition()
    {
        var stamp = new LocalFileStamp(@"C:\data\run.raw", 42, 1);
        await _store.SaveAsync(UploadRecord.ForNewFile(stamp, "/_webdav/uploads/run.raw"));

        await _store.SetStateAsync(Key(stamp.Path), TransferState.Uploading);
        (await _store.GetAsync(Key(stamp.Path)))!.Attempts.ShouldBe(1);

        var verifiedAt = DateTimeOffset.UtcNow;
        await _store.MarkVerifiedAsync(Key(stamp.Path), VerifyMethod.ServerMd5, verifiedAt);

        var row = await _store.GetAsync(Key(stamp.Path));
        row!.State.ShouldBe(TransferState.Verified);
        row.VerifyMethod.ShouldBe(VerifyMethod.ServerMd5);
        row.VerifiedUtc!.Value.ToUnixTimeMilliseconds()
            .ShouldBe(verifiedAt.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Saving_a_case_only_renamed_path_updates_the_stored_spelling()
    {
        // The primary key is NOCASE, so this is an UPDATE by the SQL engine's own rules, not a
        // fresh row. Without local_path in the SET list, the ledger would keep displaying the
        // pre-rename casing forever, even though remote_path and every other column follow the
        // rename correctly.
        var original = new LocalFileStamp(@"C:\data\run.raw", 42, 1);
        await _store.SaveAsync(UploadRecord.ForNewFile(original, "/_webdav/uploads/run.raw"));

        var renamed = new LocalFileStamp(@"C:\data\RUN.raw", 42, 1);
        await _store.SaveAsync(UploadRecord.ForNewFile(renamed, "/_webdav/uploads/RUN.raw"));

        (await _store.GetAsync(Key(@"C:\data\RUN.raw", "/_webdav/uploads/RUN.raw")))!
            .LocalPath.ShouldBe(@"C:\data\RUN.raw");

        // And only one row. Widening the key made a second one possible -- the local halves
        // collapse under NOCASE as they always did, but destinations are compared exactly because
        // Panorama is case-sensitive -- so the pre-rename destination used to be left behind.
        // Nothing ever re-uploaded, but the Uploads table would show one file twice, and saving
        // now clears it. CaseOnlyRenameTests is where the narrowness of that clearing is pinned:
        // a destination differing by more than case is a different destination and is kept.
        (await _store.GetAsync(Key(@"C:\data\run.raw")))
            .ShouldBeNull("the row for the pre-rename destination is cleared on save");
    }

    // IAsyncLifetime, not IAsyncDisposable: xUnit v2 never calls IAsyncDisposable on a test
    // class, so this teardown silently did not run at all.
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _store.DisposeAsync().AsTask();
    [Fact]
    public void The_withdrawn_state_value_is_still_free()
    {
        // The conversion that rewrites rows left by the withdrawn per-file conflict choices runs
        // on every open and cannot be guarded by the schema version, because a rolled-back build
        // can write one of those rows after the version is already current. It matches state 10,
        // as a literal, in SQL -- so the day somebody gives a new TransferState that value, every
        // row in it is silently rewritten on every launch, for ever, and nothing else would say
        // so.
        //
        // Here rather than in a static constructor, which was the first attempt: that turns the
        // mistake into an application that will not start, with the reason inside a
        // TypeInitializationException, on an instrument. This fails in CI instead, where whoever
        // added the value can still change it.
        Enum.IsDefined(typeof(TransferState), 10).ShouldBeFalse(
            "TransferState 10 is reserved: SqliteStateStore rewrites every row in it on open. "
            + "Give the new state a different value, or retire that conversion.");
    }

}