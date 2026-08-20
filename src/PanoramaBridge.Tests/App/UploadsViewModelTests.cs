using PanoramaBridge.App.ViewModels;
using PanoramaBridge.Core.Storage;

namespace PanoramaBridge.Tests.App;

/// <summary>
/// The Uploads tab: the durable answer to "did that actually get uploaded?".
/// </summary>
/// <remarks>
/// It reads the ledger rather than the in-memory transfer list, so it still answers next week and
/// after a rebuild. What matters most is that it never overstates: a row saying "Verified" has to
/// mean the server's own hash was compared, and nothing weaker may look the same.
/// </remarks>
public sealed class UploadsViewModelTests : IAsyncDisposable
{
    private readonly SqliteStateStore _store = SqliteStateStore.InMemory();

    private async Task RecordAsync(
        string name,
        TransferState state,
        VerifyMethod verified = VerifyMethod.ServerMd5,
        string? error = null)
    {
        await _store.SaveAsync(new UploadRecord(
            LocalPath: @"C:\data\" + name,
            RemotePath: "/_webdav/uploads/" + name,
            Length: 4096,
            LastWriteUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Md5: "d41d8cd98f00b204e9800998ecf8427e",
            Sha256: null,
            State: state,
            VerifyMethod: verified,
            VerifiedUtc: verified == VerifyMethod.ServerMd5 ? DateTimeOffset.UtcNow : null,
            Attempts: 1,
            LastError: error,
            IsDataset: false));
    }

    private async Task<UploadsViewModel> LoadedAsync()
    {
        await RecordAsync("verified.raw", TransferState.Verified);
        await RecordAsync("skipped.raw", TransferState.Skipped);
        await RecordAsync("failed.raw", TransferState.Failed, VerifyMethod.None, "The server refused it.");
        await RecordAsync("conflict.raw", TransferState.Conflict, VerifyMethod.None);

        var view = new UploadsViewModel(_store);
        await view.RefreshAsync();
        return view;
    }

    [Fact]
    public async Task Everything_recorded_is_shown_by_default()
    {
        var view = await LoadedAsync();

        view.Rows.Count.ShouldBe(4);
        view.IsEmpty.ShouldBeFalse();
        view.IsLoading.ShouldBeFalse();
    }

    [Fact]
    public async Task An_empty_ledger_says_so_rather_than_sitting_blank()
    {
        var view = new UploadsViewModel(_store);

        await view.RefreshAsync();

        view.Rows.ShouldBeEmpty();
        view.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task The_filter_narrows_to_what_was_asked_for()
    {
        var view = await LoadedAsync();

        view.Filter = UploadFilter.Verified;
        await view.RefreshAsync();
        view.Rows.Select(r => r.FileName).ShouldBe(["skipped.raw", "verified.raw"], ignoreOrder: true);

        view.Filter = UploadFilter.NeedsAttention;
        await view.RefreshAsync();
        view.Rows.Select(r => r.FileName).ShouldBe(["failed.raw", "conflict.raw"], ignoreOrder: true);
    }

    [Fact]
    public async Task Searching_matches_the_file_name()
    {
        var view = await LoadedAsync();

        view.Search = "fail";
        await view.RefreshAsync();

        view.Rows.ShouldHaveSingleItem().FileName.ShouldBe("failed.raw");
    }

    [Fact]
    public async Task Searching_ignores_case_because_nobody_types_a_run_name_exactly()
    {
        var view = await LoadedAsync();

        view.Search = "VERIFIED";
        await view.RefreshAsync();

        view.Rows.ShouldHaveSingleItem().FileName.ShouldBe("verified.raw");
    }

    [Fact]
    public async Task Only_a_row_checked_against_the_servers_own_hash_is_called_trustworthy()
    {
        // The single most important thing this screen does. A tick that means less than it looks
        // like is how upload tracking loses trust, and regaining it is expensive.
        await RecordAsync("proven.raw", TransferState.Verified, VerifyMethod.ServerMd5);
        await RecordAsync("sized.raw", TransferState.Uploaded, VerifyMethod.SizeOnly);
        await RecordAsync("unchecked.raw", TransferState.Uploaded, VerifyMethod.None);

        var view = new UploadsViewModel(_store);
        await view.RefreshAsync();

        var rows = view.Rows.ToDictionary(r => r.FileName, StringComparer.Ordinal);

        rows["proven.raw"].IsTrustworthy.ShouldBeTrue();
        rows["proven.raw"].Verification.ShouldContain("server");

        rows["sized.raw"].IsTrustworthy.ShouldBeFalse();
        rows["sized.raw"].Verification.ShouldNotContain("server", Case.Insensitive);

        rows["unchecked.raw"].IsTrustworthy.ShouldBeFalse();
    }

    [Fact]
    public async Task A_row_that_went_wrong_carries_the_reason()
    {
        var view = await LoadedAsync();

        var failed = view.Rows.Single(r => r.FileName == "failed.raw");

        failed.NeedsAttention.ShouldBeTrue();
        failed.Detail.ShouldBe("The server refused it.");
    }

    [Fact]
    public async Task The_summary_says_what_the_ledger_holds()
    {
        var view = await LoadedAsync();

        view.Summary.ShouldNotBe("Nothing recorded yet.");
        view.Summary.ShouldContain("4");
    }

    public ValueTask DisposeAsync() => _store.DisposeAsync();
}
