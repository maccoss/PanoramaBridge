using Microsoft.Extensions.Logging.Abstractions;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;
using PanoramaBridge.Core.WebDav;
using PanoramaBridge.Tests.TestDoubles;

namespace PanoramaBridge.Tests.Transfer;

/// <summary>
/// How far a recorded failure reaches when several configurations share a file.
/// </summary>
/// <remarks>
/// <para>
/// The ledger now holds a row per destination, so a failure has to say which destinations it is
/// true of. A file that is not a file -- a directory, a name no server will accept -- fails for
/// everyone. A server that is down, or a folder one configuration is not watching, does not.
/// </para>
/// <para>
/// Marking them all was the first version of this and it is the expensive mistake: it overwrites
/// another configuration's Verified row with this one's error, so a file whose copy is on the
/// server and fine shows as failed and is sent again on the next sweep.
/// </para>
/// </remarks>
public sealed class FailureScopeTests : IAsyncLifetime
{
    private const string MineRoot = "/_webdav/MacCoss/mine/@files";
    private const string TheirsRoot = "/_webdav/MacCoss/theirs/@files";

    private readonly string _directory = Directory.CreateTempSubdirectory("pb-scope-").FullName;
    private SqliteStateStore _store = null!;

    public Task InitializeAsync()
    {
        _store = SqliteStateStore.InMemory();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private TransferCoordinator NewEngine(IWebDavClient client) => new(
        client,
        _store,
        new TransferEngineOptions
        {
            LocalBaseDirectory = _directory,
            DestinationRoot = RemotePath.Parse(MineRoot).AsCollection(),
            VerifyUploads = false,
            WriteChecksumSidecars = false,
            MaxConcurrentTransfers = 1,
        },
        log: NullLogger<TransferCoordinator>.Instance);

    private static UploadRecord Verified(string localPath, string remotePath) => new(
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

    private async Task<IReadOnlyList<UploadRecord>> RowsFor(string localPath) =>
        (await _store.GetManyAsync([localPath]))[localPath];

    [Fact]
    public async Task A_server_that_refuses_one_upload_does_not_fail_another_destination()
    {
        // Two configurations send one file to two servers. This engine's upload throws; the other
        // configuration's copy is verified and untouched by any of it.
        var localPath = Path.Combine(_directory, "run.raw");
        await File.WriteAllBytesAsync(localPath, new byte[4096]);

        await _store.SaveAsync(Verified(localPath, $"{MineRoot}/run.raw"));
        await _store.SaveAsync(Verified(localPath, $"{TheirsRoot}/run.raw"));

        var server = new FakeWebDavClient { FailUploadsBeforeSucceeding = int.MaxValue };

        await using (var engine = NewEngine(server))
        {
            var running = engine.RunAsync();
            await engine.EnqueueAsync(localPath);
            engine.CompleteAdding();
            await running;
        }

        var rows = await RowsFor(localPath);

        rows.Single(r => r.RemotePath == $"{TheirsRoot}/run.raw")
            .State
            .ShouldBe(
                TransferState.Verified,
                "the other configuration's server was never involved in this failure");
    }

    [Fact]
    public async Task A_folder_offered_as_a_file_fails_for_every_destination()
    {
        // Not a file for anyone. Recording it once per row is what stops each configuration
        // rediscovering it on every sweep.
        var folder = Path.Combine(_directory, "acquisition.d");
        Directory.CreateDirectory(folder);

        await _store.SaveAsync(Verified(folder, $"{MineRoot}/acquisition.d"));
        await _store.SaveAsync(Verified(folder, $"{TheirsRoot}/acquisition.d"));

        await using (var engine = NewEngine(new FakeWebDavClient()))
        {
            var running = engine.RunAsync();
            await engine.EnqueueAsync(folder);
            engine.CompleteAdding();
            await running;
        }

        var rows = await RowsFor(folder);

        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(r => r.State == TransferState.Failed);
    }

    [Fact]
    public async Task A_failure_with_no_row_of_its_own_leaves_another_configuration_alone()
    {
        // Only the other configuration has ever recorded this file. A destination-scoped failure
        // has nothing of its own to mark, and must not borrow theirs.
        var localPath = Path.Combine(_directory, "run.raw");
        await File.WriteAllBytesAsync(localPath, new byte[4096]);

        await _store.SaveAsync(Verified(localPath, $"{TheirsRoot}/run.raw"));

        var server = new FakeWebDavClient { FailUploadsBeforeSucceeding = int.MaxValue };

        await using (var engine = NewEngine(server))
        {
            var running = engine.RunAsync();
            await engine.EnqueueAsync(localPath);
            engine.CompleteAdding();
            await running;
        }

        var rows = await RowsFor(localPath);

        rows.Single(r => r.RemotePath == $"{TheirsRoot}/run.raw")
            .State
            .ShouldBe(TransferState.Verified);
    }

    [Fact]
    public async Task A_destination_that_merely_starts_with_the_same_letters_is_not_this_engine_s()
    {
        // A configuration sending to /@files/mine must not claim rows under /@files/mineArchive.
        // A plain prefix test would mark those failed too, and they belong to somebody else.
        var localPath = Path.Combine(_directory, "run.raw");
        await File.WriteAllBytesAsync(localPath, new byte[4096]);

        await _store.SaveAsync(Verified(localPath, $"{MineRoot}Archive/run.raw"));

        var server = new FakeWebDavClient { FailUploadsBeforeSucceeding = int.MaxValue };

        await using (var engine = NewEngine(server))
        {
            var running = engine.RunAsync();
            await engine.EnqueueAsync(localPath);
            engine.CompleteAdding();
            await running;
        }

        var rows = await RowsFor(localPath);

        rows.Single(r => r.RemotePath == $"{MineRoot}Archive/run.raw")
            .State
            .ShouldBe(TransferState.Verified);
    }
}
