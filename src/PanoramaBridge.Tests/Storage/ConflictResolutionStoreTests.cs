using PanoramaBridge.Core.Storage;

namespace PanoramaBridge.Tests.Storage;

/// <summary>
/// Recording what a person decided about a conflict, so the engine can act on it later.
/// </summary>
/// <remarks>
/// The decision is written to the ledger rather than held in memory because an instrument PC can
/// be restarted between somebody deciding and the transfer running. A decision that does not
/// survive that is a decision they get asked for twice.
/// </remarks>
public sealed class ConflictResolutionStoreTests : IAsyncDisposable
{
    private const string Path = @"C:\data\run.raw";

    private readonly SqliteStateStore _store = SqliteStateStore.InMemory();

    private Task ConflictedAsync() => _store.SaveAsync(new UploadRecord(
        LocalPath: Path,
        RemotePath: "/_webdav/uploads/run.raw",
        Length: 4096,
        LastWriteUnixMs: 1_700_000_000_000,
        Md5: "d41d8cd98f00b204e9800998ecf8427e",
        Sha256: null,
        State: TransferState.Conflict,
        VerifyMethod: VerifyMethod.None,
        VerifiedUtc: null,
        Attempts: 0,
        LastError: "A different file already occupies the destination.",
        IsDataset: false));

    [Fact]
    public async Task Overwrite_puts_the_row_back_in_front_of_the_sweep()
    {
        await ConflictedAsync();

        await _store.ResolveConflictAsync(Path, ConflictResolution.Overwrite);

        var row = await _store.GetAsync(Path);

        // Discovered rather than Queued: the sweep is what re-offers it, and it decides when.
        row!.State.ShouldBe(TransferState.Discovered);
        row.Resolution.ShouldBe(ConflictResolution.Overwrite);
        row.HasPendingResolution.ShouldBeTrue();

        // The reason it was held is no longer true and would otherwise sit in the Uploads tab
        // describing a conflict that has been settled.
        row.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task Rename_carries_the_name_it_was_given()
    {
        await ConflictedAsync();

        await _store.ResolveConflictAsync(Path, ConflictResolution.Rename, "run (2).raw");

        var row = await _store.GetAsync(Path);

        row!.State.ShouldBe(TransferState.Discovered);
        row.Resolution.ShouldBe(ConflictResolution.Rename);
        row.RenameTo.ShouldBe("run (2).raw");
    }

    [Fact]
    public async Task Keep_is_finished_the_moment_it_is_recorded()
    {
        await ConflictedAsync();

        await _store.ResolveConflictAsync(Path, ConflictResolution.Keep);

        var row = await _store.GetAsync(Path);

        // Declined, not Skipped. Skipped means an identical copy was already there and carries a
        // verified standing to prove it; here the two differ and somebody chose the remote one.
        row!.State.ShouldBe(TransferState.Declined);

        // Nothing pending: there is no work left for the engine to pick up.
        row.Resolution.ShouldBe(ConflictResolution.None);
        row.HasPendingResolution.ShouldBeFalse();

        // And it says so where every other reason for a row's state is said.
        row.LastError.ShouldBe("Kept the copy already on the server.");
    }

    [Fact]
    public async Task Keep_never_claims_the_file_was_verified()
    {
        await ConflictedAsync();

        await _store.ResolveConflictAsync(Path, ConflictResolution.Keep);

        var row = await _store.GetAsync(Path);

        // Nothing was sent and nothing was compared. A tick here would be the exact overstatement
        // this codebase spends the most effort avoiding.
        row!.VerifyMethod.ShouldBe(VerifyMethod.None);
        row.VerifiedUtc.ShouldBeNull();
    }

    [Fact]
    public async Task A_decision_survives_being_reloaded()
    {
        await ConflictedAsync();
        await _store.ResolveConflictAsync(Path, ConflictResolution.Rename, "run (2).raw");

        // The round trip through SQLite is the point: a decision held only in memory is one the
        // user gets asked for again after a reboot.
        var reloaded = await _store.GetByStateAsync([TransferState.Discovered]);

        var row = reloaded.Single();
        row.Resolution.ShouldBe(ConflictResolution.Rename);
        row.RenameTo.ShouldBe("run (2).raw");
    }

    [Fact]
    public async Task Resolving_needs_an_actual_decision()
    {
        await ConflictedAsync();

        await Should.ThrowAsync<ArgumentException>(
            () => _store.ResolveConflictAsync(Path, ConflictResolution.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Renaming_without_a_name_is_refused(string? name)
    {
        await ConflictedAsync();

        // Silently doing nothing here would leave the row conflicted with the user believing
        // they had resolved it.
        await Should.ThrowAsync<ArgumentException>(
            () => _store.ResolveConflictAsync(Path, ConflictResolution.Rename, name));
    }

    [Fact]
    public async Task A_row_the_engine_has_moved_on_to_is_left_alone()
    {
        await ConflictedAsync();

        // The Uploads tab is a snapshot. A sweep can re-offer a file between the list being drawn
        // and a button being pressed, and writing Declined underneath a running upload would flip
        // a transfer's state from beneath it -- then be overwritten again when it finishes.
        await _store.SetStateAsync(Path, TransferState.Uploading);

        await _store.ResolveConflictAsync(Path, ConflictResolution.Keep);

        (await _store.GetAsync(Path))!.State.ShouldBe(TransferState.Uploading);
    }

    [Theory]
    [InlineData(ConflictResolution.Keep)]
    [InlineData(ConflictResolution.Overwrite)]
    public async Task A_decision_that_is_not_a_rename_does_not_forget_where_the_file_lives(
        ConflictResolution resolution)
    {
        // The row already lives at run (2).raw because somebody sent it alongside earlier.
        await ConflictedAsync();
        await _store.ResolveConflictAsync(Path, ConflictResolution.Rename, "run (2).raw");
        await _store.SetStateAsync(Path, TransferState.Conflict);

        await _store.ResolveConflictAsync(Path, resolution);

        // Clearing this would send the sweep back to resolving the row to its original name, and
        // re-open the unbounded re-send the column was added to close.
        (await _store.GetAsync(Path))!.RenameTo.ShouldBe("run (2).raw");
    }

    public ValueTask DisposeAsync() => _store.DisposeAsync();
}
