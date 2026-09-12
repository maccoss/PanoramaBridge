using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;

namespace PanoramaBridge.Tests.Storage;

/// <summary>
/// Renaming a file so that only its case changes.
/// </summary>
/// <remarks>
/// <para>
/// Widening the ledger key to (local path, destination) made this possible. Local paths still
/// collapse case-insensitively, because Windows does; destinations compare exactly, because a
/// server need not. So renaming <c>run.raw</c> to <c>RUN.raw</c> produces one local path and two
/// destinations, and the second upload leaves the first row behind.
/// </para>
/// <para>
/// Nothing re-uploads either way -- the sweep asks about the destination it would use now. What
/// it costs is a row: the Uploads table would show one file twice, and the extra entry would look
/// like a transfer nobody could account for. That is worth clearing before phase 5 puts that
/// table in front of anyone.
/// </para>
/// </remarks>
public sealed class CaseOnlyRenameTests : IAsyncLifetime
{
    private SqliteStateStore _store = null!;

    public Task InitializeAsync()
    {
        _store = SqliteStateStore.InMemory();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _store.DisposeAsync().AsTask();

    private static UploadRecord Row(string localPath, string remotePath) => new(
        localPath,
        remotePath,
        Length: 4096,
        LastWriteUnixMs: 1_700_000_000_000,
        Md5: "d41d8cd98f00b204e9800998ecf8427e",
        Sha256: null,
        State: TransferState.Verified,
        VerifyMethod: VerifyMethod.ServerMd5,
        VerifiedUtc: DateTimeOffset.UnixEpoch,
        Attempts: 1,
        LastError: null);

    [Fact]
    public async Task A_rename_that_changes_only_case_leaves_one_row()
    {
        await _store.SaveAsync(Row(@"D:\Data\run.raw", "/_webdav/p/@files/run.raw"));
        await _store.SaveAsync(Row(@"D:\Data\RUN.raw", "/_webdav/p/@files/RUN.raw"));

        var rows = await _store.GetManyAsync([@"D:\Data\run.raw"]);

        rows[@"D:\Data\run.raw"].ShouldHaveSingleItem()
            .RemotePath.ShouldBe("/_webdav/p/@files/RUN.raw", "the destination in use now");
    }

    [Fact]
    public async Task Two_real_destinations_for_one_file_still_coexist()
    {
        // The whole reason the key was widened. Two configurations watching one folder and
        // sending it to two Panorama projects must each keep their own row, or they re-upload
        // over each other for ever.
        await _store.SaveAsync(Row(@"D:\Data\run.raw", "/_webdav/ProjectA/@files/run.raw"));
        await _store.SaveAsync(Row(@"D:\Data\run.raw", "/_webdav/ProjectB/@files/run.raw"));

        var rows = await _store.GetManyAsync([@"D:\Data\run.raw"]);

        rows[@"D:\Data\run.raw"].Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_destination_differing_by_more_than_case_is_left_alone()
    {
        // One character apart and not a case change. Clearing this would silently drop the
        // record of a real upload, and the next sweep would send the file again.
        await _store.SaveAsync(Row(@"D:\Data\run.raw", "/_webdav/p/@files/run.raw"));
        await _store.SaveAsync(Row(@"D:\Data\run.raw", "/_webdav/p/@files/run2.raw"));

        var rows = await _store.GetManyAsync([@"D:\Data\run.raw"]);

        rows[@"D:\Data\run.raw"].Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_case_change_in_the_folder_rather_than_the_file_is_cleared_too()
    {
        // The same thing one level up: somebody renames the acquisition folder, and every file
        // under it acquires a second row.
        await _store.SaveAsync(Row(@"D:\Data\run.raw", "/_webdav/p/@files/monday/run.raw"));
        await _store.SaveAsync(Row(@"D:\Data\run.raw", "/_webdav/p/@files/Monday/run.raw"));

        var rows = await _store.GetManyAsync([@"D:\Data\run.raw"]);

        rows[@"D:\Data\run.raw"].ShouldHaveSingleItem()
            .RemotePath.ShouldBe("/_webdav/p/@files/Monday/run.raw");
    }

    [Fact]
    public async Task Saving_the_same_row_twice_does_not_delete_it()
    {
        // The delete has to exclude the row being written. Getting this wrong would clear the
        // ledger entry on every save, and every file would be uploaded again on every sweep.
        await _store.SaveAsync(Row(@"D:\Data\run.raw", "/_webdav/p/@files/run.raw"));
        await _store.SaveAsync(Row(@"D:\Data\run.raw", "/_webdav/p/@files/run.raw"));

        var row = await _store.GetAsync(
            new LedgerKey(@"D:\Data\run.raw", "/_webdav/p/@files/run.raw"));

        row.ShouldNotBeNull();
        row.State.ShouldBe(TransferState.Verified);
    }

    [Fact]
    public async Task Another_file_entirely_is_untouched()
    {
        await _store.SaveAsync(Row(@"D:\Data\other.raw", "/_webdav/p/@files/OTHER.raw"));
        await _store.SaveAsync(Row(@"D:\Data\run.raw", "/_webdav/p/@files/run.raw"));
        await _store.SaveAsync(Row(@"D:\Data\run.raw", "/_webdav/p/@files/RUN.raw"));

        var rows = await _store.GetManyAsync([@"D:\Data\other.raw", @"D:\Data\run.raw"]);

        rows[@"D:\Data\other.raw"].ShouldHaveSingleItem();
        rows[@"D:\Data\run.raw"].ShouldHaveSingleItem();
    }
}
