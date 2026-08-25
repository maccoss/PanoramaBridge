using PanoramaBridge.App.ViewModels;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;
using PanoramaBridge.Core.WebDav;
using PanoramaBridge.Tests.TestDoubles;

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
            RawCheck: rawCheck,
            ConflictKind: rawCheck is null
                ? ConflictKind.DestinationOccupied
                : ConflictKind.LocalFileDamaged));

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

        view.HasResolveProblem.ShouldBeTrue();
        view.ResolveProblem.ShouldContain("no connection");

        // And nothing was decided on the strength of a name nobody could check.
        (await _store.GetAsync(@"C:\data\a.raw"))!.State.ShouldBe(TransferState.Conflict);
    }

    [Fact]
    public async Task Refusing_to_overwrite_a_damaged_file_says_so()
    {
        // Doing nothing silently reads as a broken button rather than as the guard it is.
        await ConflictAsync("short.raw", rawCheck: "Incomplete: the file ends before its data does.");

        var view = await LoadedAsync();
        await view.ResolveOverwriteCommand.ExecuteAsync(null);

        view.HasResolveProblem.ShouldBeTrue();
        view.ResolveProblem.ShouldContain("damaged");
    }

    [Fact]
    public async Task A_message_does_not_outlive_the_situation_that_produced_it()
    {
        await ConflictAsync("a.raw");

        var view = await LoadedAsync();
        await view.ResolveRenameCommand.ExecuteAsync(null);
        view.HasResolveProblem.ShouldBeTrue("no connection");

        // Keep succeeds. A red line still claiming there is no connection, beside a list that has
        // just changed, is worse than no message at all.
        await view.ResolveKeepCommand.ExecuteAsync(null);

        view.HasResolveProblem.ShouldBeFalse();
    }

    [Fact]
    public async Task Replacing_asks_twice_before_destroying_anything()
    {
        await ConflictAsync("a.raw");

        var view = await LoadedAsync();

        // First press arms and explains. Nothing is decided yet.
        await view.ResolveOverwriteCommand.ExecuteAsync(null);

        view.OverwriteArmed.ShouldBeTrue();
        view.ResolveProblem.ShouldContain("1 file(s)");
        (await _store.GetAsync(@"C:\data\a.raw"))!.State.ShouldBe(TransferState.Conflict);

        // Second press goes ahead.
        await view.ResolveOverwriteCommand.ExecuteAsync(null);

        view.OverwriteArmed.ShouldBeFalse();
        (await _store.GetAsync(@"C:\data\a.raw"))!.Resolution
            .ShouldBe(ConflictResolution.Overwrite);
    }

    [Fact]
    public async Task Another_action_cancels_a_pending_replace()
    {
        await ConflictAsync("a.raw");

        var view = await LoadedAsync();
        await view.ResolveOverwriteCommand.ExecuteAsync(null);
        view.OverwriteArmed.ShouldBeTrue();

        // Pressing something else is how somebody backs out. Leaving it armed would let the next
        // press of Replace fire against a set of files it never counted.
        await view.ResolveKeepCommand.ExecuteAsync(null);

        view.OverwriteArmed.ShouldBeFalse();
        (await _store.GetAsync(@"C:\data\a.raw"))!.State.ShouldBe(TransferState.Declined);
    }

    [Fact]
    public async Task A_reload_cancels_a_pending_replace()
    {
        await ConflictAsync("a.raw");

        var view = await LoadedAsync();
        await view.ResolveOverwriteCommand.ExecuteAsync(null);

        // Typing in the search box rebuilds the list, so the count the confirmation quoted no
        // longer describes anything.
        await view.RefreshAsync();

        view.OverwriteArmed.ShouldBeFalse();
    }

    [Fact]
    public async Task Ticks_survive_the_reload_that_every_action_causes()
    {
        var picked = await ConflictAsync("a.raw");
        await ConflictAsync("b.raw");

        var view = await LoadedAsync();
        view.Rows.Single(r => r.Record.LocalPath == picked).IsSelected = true;

        // Every resolve command ends in a refresh, and so does typing in the search box. Dropping
        // the ticks here would silently widen the next decision from "this one" to "all of them"
        // between the tick and the click.
        await view.RefreshAsync();

        view.Rows.Single(r => r.Record.LocalPath == picked).IsSelected.ShouldBeTrue();
        view.Rows.Single(r => r.Record.LocalPath != picked).IsSelected.ShouldBeFalse();
    }

    [Fact]
    public async Task Deciding_about_all_of_them_reaches_rows_the_view_never_loaded()
    {
        await ConflictAsync("loaded.raw");
        await ConflictAsync("elsewhere.raw");

        // The list is narrowed to one row by the search box, which is exactly the situation where
        // reading the screen would have meant "all of the ones you happen to be looking at" while
        // the button said "all of them".
        var view = await LoadedAsync();
        view.Search = "loaded";
        await view.RefreshAsync();

        view.Rows.Count.ShouldBe(1);

        await view.ResolveKeepCommand.ExecuteAsync(null);

        var declined = await _store.GetByStateAsync([TransferState.Declined]);
        declined.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_tick_survives_the_row_being_filtered_out_of_view()
    {
        var picked = await ConflictAsync("plate-a.raw");
        await ConflictAsync("plate-b.raw");

        var view = await LoadedAsync();
        view.Rows.Single(r => r.Record.LocalPath == picked).IsSelected = true;

        // Narrow the list so the ticked row is not on screen, then decide. Reading the ticks off
        // the rows would find none, and "no ticks" means every held file -- so a decision aimed
        // at one acquisition would land on both.
        view.Search = "plate-b";
        await view.RefreshAsync();
        view.Rows.Count.ShouldBe(1);

        await view.ResolveKeepCommand.ExecuteAsync(null);

        (await _store.GetAsync(picked))!.State.ShouldBe(TransferState.Declined);
        (await _store.GetAsync(@"C:\data\plate-b.raw"))!.State.ShouldBe(TransferState.Conflict);
    }

    [Fact]
    public async Task The_banner_appears_for_conflicts_the_view_never_loaded()
    {
        await ConflictAsync("old.raw");

        var view = await LoadedAsync();

        // Narrowed to nothing. The buttons act on the whole ledger, so the banner that hosts them
        // has to be scoped the same way -- otherwise it hides in exactly the case the buttons
        // were widened to handle, and there is no way to press them at all.
        view.Search = "nothing-matches-this";
        await view.RefreshAsync();

        view.Rows.ShouldBeEmpty();
        view.HasConflicts.ShouldBeTrue();
    }

    [Fact]
    public async Task A_decision_that_did_not_land_is_reported()
    {
        var path = await ConflictAsync("a.raw");

        var view = await LoadedAsync();

        // Arming holds the exact list the confirmation counted, and the second press can be an
        // arbitrary time later -- long enough for a sweep to pick one of them up. The store
        // refuses to write over a row mid-transfer, which is right, but silently: without saying
        // so the user sees no error and believes every conflict is settled.
        await view.ResolveOverwriteCommand.ExecuteAsync(null);
        view.OverwriteArmed.ShouldBeTrue();

        await _store.SetStateAsync(path, TransferState.Uploading);

        await view.ResolveOverwriteCommand.ExecuteAsync(null);

        view.HasResolveProblem.ShouldBeTrue();
        view.ResolveProblem.ShouldContain("not changed");
    }

    [Fact]
    public async Task Confirming_replaces_only_what_the_first_press_counted()
    {
        await ConflictAsync("a.raw");

        var view = await LoadedAsync();
        await view.ResolveOverwriteCommand.ExecuteAsync(null);
        view.ResolveProblem.ShouldContain("1 file(s)");

        // A batch lands between the two presses. The count is the entire point of asking, so the
        // second press must act on what was counted rather than on whatever is there now.
        await ConflictAsync("arrived-later.raw");

        await view.ResolveOverwriteCommand.ExecuteAsync(null);

        (await _store.GetAsync(@"C:\data\a.raw"))!.Resolution
            .ShouldBe(ConflictResolution.Overwrite);
        (await _store.GetAsync(@"C:\data\arrived-later.raw"))!.State
            .ShouldBe(TransferState.Conflict, "it was never counted, so it was never confirmed");
    }

    [Fact]
    public async Task A_pick_that_is_no_longer_held_stops_counting()
    {
        // Picks live outside the rows now, so nothing prunes one when its file stops being a
        // conflict -- and the row's tick box is disabled the moment it stops being resolvable, so
        // it cannot be cleared by hand either. A single settled pick then makes every later
        // decision target "the held files that are picked", of which there are none: the buttons
        // go quietly dead for the rest of the session.
        var first = await ConflictAsync("a.raw");
        await ConflictAsync("b.raw");

        var view = await LoadedAsync();
        view.Rows.Single(r => r.Record.LocalPath == first).IsSelected = true;

        await view.ResolveKeepCommand.ExecuteAsync(null);
        (await _store.GetAsync(first))!.State.ShouldBe(TransferState.Declined);

        // Nothing is ticked any more in any sense the user can see, so this means "all of them".
        await view.ResolveKeepCommand.ExecuteAsync(null);

        (await _store.GetAsync(@"C:\data\b.raw"))!.State.ShouldBe(TransferState.Declined);
    }

    [Fact]
    public async Task Keeping_a_file_tells_the_rest_of_the_application_it_is_settled()
    {
        // The decision goes straight to the ledger, so nothing else hears about it unless it is
        // announced. Without this the progress aggregator counted the file under "needs
        // attention" for the life of the process and the status bar disagreed with the tab that
        // had just cleared it.
        await ConflictAsync("a.raw");

        var announced = new List<TransferProgress>();
        var view = new UploadsViewModel(_store, announce: announced.Add);
        await view.RefreshAsync();

        await view.ResolveKeepCommand.ExecuteAsync(null);

        announced.Single().State.ShouldBe(TransferState.Declined);
    }

    [Fact]
    public async Task Replacing_retracts_the_row_rather_than_restating_it()
    {
        await ConflictAsync("a.raw");

        var announced = new List<TransferProgress>();
        var forgotten = new List<string>();

        var view = new UploadsViewModel(_store, announce: announced.Add, forget: forgotten.Add);
        await view.RefreshAsync();

        await view.ResolveOverwriteCommand.ExecuteAsync(null);
        await view.ResolveOverwriteCommand.ExecuteAsync(null);

        // The sweep will report it from the start. Restating it as queued would leave a row
        // nothing ever removes, and the refresh timer awake for the life of the process -- on a
        // machine where that is the one thing this application must not do.
        forgotten.ShouldBe([Path.Combine(@"C:\data", "a.raw")]);
        announced.ShouldBeEmpty();
    }

    [Fact]
    public async Task Nothing_is_announced_for_a_decision_that_did_not_land()
    {
        var path = await ConflictAsync("a.raw");

        var announced = new List<TransferProgress>();
        var forgotten = new List<string>();

        var view = new UploadsViewModel(_store, announce: announced.Add, forget: forgotten.Add);
        await view.RefreshAsync();

        await view.ResolveOverwriteCommand.ExecuteAsync(null);
        await _store.SetStateAsync(path, TransferState.Uploading);
        await view.ResolveOverwriteCommand.ExecuteAsync(null);

        // Telling the status bar about a decision the store refused would be telling it something
        // untrue. Replace retracts rather than announces, so the retraction is what to assert --
        // checking `announced` alone passed whatever the store did, because this path never
        // announces anything.
        announced.ShouldBeEmpty();
        forgotten.ShouldBeEmpty();
    }

    [Fact]
    public async Task Picking_a_file_that_stops_being_held_does_not_widen_to_everything()
    {
        // Pruning stale picks was itself a fix, for the buttons going dead. Falling through to
        // "all of them" once the picks are gone over-corrects in the opposite direction: the user
        // aimed at one file, a sweep picked it up before they pressed the button, and every held
        // conflict on the machine gets decided instead. Silently, and without a confirmation.
        var aimed = await ConflictAsync("aimed-at.raw");
        await ConflictAsync("untouched-a.raw");
        await ConflictAsync("untouched-b.raw");

        var view = await LoadedAsync();
        view.Rows.Single(r => r.Record.LocalPath == aimed).IsSelected = true;

        // The sweep gets to it first.
        await _store.SetStateAsync(aimed, TransferState.Uploading);

        await view.ResolveKeepCommand.ExecuteAsync(null);

        (await _store.GetAsync(@"C:\data\untouched-a.raw"))!.State
            .ShouldBe(TransferState.Conflict, "it was never aimed at");
        (await _store.GetAsync(@"C:\data\untouched-b.raw"))!.State
            .ShouldBe(TransferState.Conflict, "it was never aimed at");

        // And the user is told, rather than left thinking their decision landed.
        view.HasResolveProblem.ShouldBeTrue();
    }

    [Fact]
    public async Task A_damaged_file_left_out_of_a_rename_is_reported()
    {
        // Filtered out before planning, so the plan measured against itself said every proposal
        // succeeded and the counts agreed. The user was left believing both were settled while
        // the damaged one is still held.
        await ConflictAsync("a.raw");
        await ConflictAsync("short.raw", rawCheck: "Incomplete: the file ends before its data does.");

        // A real server, or this takes the "there is no connection" branch and passes without
        // going anywhere near the planner.
        var server = new FakeWebDavClient();
        server.Seed(RemotePath.Parse("/_webdav/uploads/a.raw"), "occupying"u8.ToArray());

        var view = new UploadsViewModel(_store, () => server);
        await view.RefreshAsync();

        await view.ResolveRenameCommand.ExecuteAsync(null);

        view.ResolveProblem.ShouldContain("not changed");
    }

    public ValueTask DisposeAsync() => _store.DisposeAsync();
}
