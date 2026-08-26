using PanoramaBridge.Core.Storage;

namespace PanoramaBridge.Tests.Storage;

/// <summary>Contract tests for the update-only ledger transitions.</summary>
public sealed class SqliteStateStoreTests : IAsyncDisposable
{
    private readonly SqliteStateStore _store = SqliteStateStore.InMemory();

    [Fact]
    public async Task A_state_transition_requires_an_existing_row()
    {
        const string path = @"C:\data\unknown.raw";

        await Should.ThrowAsync<InvalidOperationException>(() =>
            _store.SetStateAsync(path, TransferState.Uploading));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            _store.MarkVerifiedAsync(path, VerifyMethod.ServerMd5, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_saved_row_can_move_through_each_update_only_transition()
    {
        var stamp = new LocalFileStamp(@"C:\data\run.raw", 42, 1);
        await _store.SaveAsync(UploadRecord.ForNewFile(stamp, "/_webdav/uploads/run.raw"));

        await _store.SetStateAsync(stamp.Path, TransferState.Uploading);
        (await _store.GetAsync(stamp.Path))!.Attempts.ShouldBe(1);

        var verifiedAt = DateTimeOffset.UtcNow;
        await _store.MarkVerifiedAsync(stamp.Path, VerifyMethod.ServerMd5, verifiedAt);

        var row = await _store.GetAsync(stamp.Path);
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

        (await _store.GetAsync(@"C:\data\RUN.raw"))!.LocalPath.ShouldBe(@"C:\data\RUN.raw");
    }

    public ValueTask DisposeAsync() => _store.DisposeAsync();
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