using PanoramaBridge.App.ViewModels;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;

namespace PanoramaBridge.Tests.App;

/// <summary>
/// Resolving a conflict from the Uploads tab.
/// </summary>
/// <remarks>
/// Deliberately not a modal. This runs unattended on an instrument computer, and a dialog that
/// appears at three in the morning blocks every transfer behind it until somebody clicks -- one
/// held file becoming a stalled night. Conflicts wait in the ledger and are decided when a person
/// is actually there.
/// </remarks>
public sealed class UploadsConflictTests : IAsyncDisposable
{
    private readonly SqliteStateStore _store = SqliteStateStore.InMemory();

    private async Task<string> ConflictAsync(string name, string? rawCheck = null)
    {
        var path = @"C:\data\" + name;

        // A damaged local file is held with its raw-check summary as the reason, which is what
        // tells the two kinds of conflict apart.
        var reason = rawCheck ?? "A different file already occupies the destination.";

        await _store.SaveAsync(new UploadRecord(
            LocalPath: path,
            RemotePath: "/_webdav/uploads/" + name,
            Length: 4096,
            LastWriteUnixMs: 1_700_000_000_000,
            Md5: "d41d8cd98f00b204e9800998ecf8427e",
            Sha256: null,
            State: TransferState.Conflict,
            VerifyMethod: VerifyMethod.None,
            VerifiedUtc: null,
            Attempts: 0,
            LastError: reason,
            IsDataset: false,
            RawCheck: rawCheck));

        return path;
    }

    private async Task<UploadsViewModel> LoadedAsync()
    {
        var view = new UploadsViewModel(_store);
        await view.RefreshAsync();
        return view;
    }

    [Fact]
    public async Task Nothing_picked_means_every_conflict()
    {
        // What somebody looking at a filtered list of conflicts is asking for when they press a
        // button without ticking anything.
        await ConflictAsync("a.raw");
        await ConflictAsync("b.raw");

        var view = await LoadedAsync();
        view.HasConflicts.ShouldBeTrue();

        await view.ResolveKeepCommand.ExecuteAsync(null);

        var rows = await _store.GetByStateAsync([TransferState.Declined]);
        rows.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Picking_rows_narrows_it_to_those()
    {
        var kept = await ConflictAsync("a.raw");
        await ConflictAsync("b.raw");

        var view = await LoadedAsync();
        view.Rows.Single(r => r.Record.LocalPath == kept).IsSelected = true;

        await view.ResolveKeepCommand.ExecuteAsync(null);

        (await _store.GetAsync(kept))!.State.ShouldBe(TransferState.Declined);

        // The one that was not picked is untouched and still needs a decision.
        (await _store.GetAsync(@"C:\data\b.raw"))!.State.ShouldBe(TransferState.Conflict);
    }

    [Fact]
    public async Task A_damaged_local_file_is_never_offered_an_overwrite()
    {
        // Held in the same state as a destination clash, and wanting the opposite thing.
        // Overwriting here would push a short acquisition over a good remote copy, which is
        // exactly what the truncation check exists to prevent.
        var truncated = await ConflictAsync(
            "short.raw", rawCheck: "Incomplete: the file ends before its data does.");

        var view = await LoadedAsync();
        view.Rows.Single().IsLocalFileProblem.ShouldBeTrue();

        await view.ResolveOverwriteCommand.ExecuteAsync(null);

        (await _store.GetAsync(truncated))!.State.ShouldBe(TransferState.Conflict);
    }

    [Fact]
    public async Task A_damaged_local_file_can_still_be_set_aside()
    {
        // Keep applies to it: the remote copy is the good one, and this stops the row nagging.
        var truncated = await ConflictAsync(
            "short.raw", rawCheck: "Incomplete: the file ends before its data does.");

        var view = await LoadedAsync();
        await view.ResolveKeepCommand.ExecuteAsync(null);

        (await _store.GetAsync(truncated))!.State.ShouldBe(TransferState.Declined);
    }

    [Fact]
    public async Task Only_a_conflict_can_be_decided_about()
    {
        // A failed upload wants retrying and a superseded one resolves itself on the next sweep.
        // Offering the same three buttons for those would imply choices that do not apply.
        await _store.SaveAsync(new UploadRecord(
            LocalPath: @"C:\data\failed.raw",
            RemotePath: "/_webdav/uploads/failed.raw",
            Length: 1,
            LastWriteUnixMs: 1,
            Md5: null,
            Sha256: null,
            State: TransferState.Failed,
            VerifyMethod: VerifyMethod.None,
            VerifiedUtc: null,
            Attempts: 5,
            LastError: "Refused by the server.",
            IsDataset: false));

        var view = await LoadedAsync();

        view.Rows.Single().CanResolve.ShouldBeFalse();
        view.HasConflicts.ShouldBeFalse();
    }

    [Fact]
    public async Task Renaming_without_a_connection_says_so_rather_than_failing_quietly()
    {
        await ConflictAsync("a.raw");

        // No client: the audit tab can read the ledger offline, but a free name has to be
        // checked against the server.
        var view = await LoadedAsync();

        await view.ResolveRenameCommand.ExecuteAsync(null);

        view.HasRenameProblem.ShouldBeTrue();
        view.RenameProblem.ShouldContain("no connection");

        // And nothing was decided on the strength of a name nobody could check.
        (await _store.GetAsync(@"C:\data\a.raw"))!.State.ShouldBe(TransferState.Conflict);
    }

    public ValueTask DisposeAsync() => _store.DisposeAsync();
}
