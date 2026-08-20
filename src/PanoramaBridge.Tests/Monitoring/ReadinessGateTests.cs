using PanoramaBridge.Core.Monitoring;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;
using PanoramaBridge.Core.WebDav;
using PanoramaBridge.Tests.TestDoubles;

namespace PanoramaBridge.Tests.Monitoring;

/// <summary>
/// End-to-end: a real file being copied or acquired into the watched folder, going through the
/// real gate and the real transfer engine. These run in real time with short quiet periods
/// rather than on a fake clock, because the thing being verified is that a concurrent writer and
/// the gate interleave correctly -- which a fake clock would paper over.
/// </summary>
public sealed class ReadinessGateTests : IAsyncDisposable
{
    private static readonly RemotePath Destination =
        RemotePath.Parse("/_webdav/MacCoss/maccoss/@files/uploads/");

    private readonly SqliteStateStore _store = SqliteStateStore.InMemory();
    private readonly FakeWebDavClient _server = new();
    private readonly string _watched = Directory.CreateTempSubdirectory("pb-gate-").FullName;

    private ReadinessGate NewGate(double quietSeconds = 0.5, double pollSeconds = 0.05) =>
        new(
            new FileStabilityTracker(TimeSpan.FromSeconds(quietSeconds)),
            TimeSpan.FromSeconds(pollSeconds));

    private TransferCoordinator NewCoordinator() =>
        new(
            _server,
            _store,
            new TransferEngineOptions
            {
                LocalBaseDirectory = _watched,
                DestinationRoot = Destination,
                MaxConcurrentTransfers = 2,
            });

    /// <summary>
    /// Writes a file the way a copy or an acquisition does: hold it open, write in blocks with
    /// pauses, then close.
    /// </summary>
    private async Task WriteGraduallyAsync(
        string path,
        int blocks,
        int blockSize,
        TimeSpan pause,
        TaskCompletionSource? created = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.Read);

        // A file only reaches the gate because something observed it, so tests must not start
        // watching a path that does not exist yet -- the gate would rightly abandon it.
        created?.TrySetResult();

        for (var i = 0; i < blocks; i++)
        {
            // Distinguishable content, so a truncated upload cannot accidentally match.
            var block = new byte[blockSize];
            Array.Fill(block, (byte)('A' + (i % 26)));

            await stream.WriteAsync(block, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            await Task.Delay(pause, cancellationToken);
        }
    }

    [Fact]
    public async Task A_file_still_being_copied_in_is_uploaded_only_once_it_is_complete()
    {
        // The scenario that matters most: something lands in the watched folder while the
        // application is looking at it. The upload must contain every byte, not a prefix.
        var path = Path.Combine(_watched, "arriving.raw");
        const int Blocks = 12;
        const int BlockSize = 32 * 1024;

        var created = new TaskCompletionSource();
        var copy = Task.Run(() => WriteGraduallyAsync(
            path, Blocks, BlockSize, TimeSpan.FromMilliseconds(40), created));

        await created.Task;

        await using var coordinator = NewCoordinator();

        var gate = NewGate();
        var gateTask = gate.PumpAsync(
            [path],
            onReady: p => coordinator.EnqueueAsync(p),
            giveUpAfter: TimeSpan.FromSeconds(30));

        var run = coordinator.RunAsync();

        await copy;
        var outcome = await gateTask;
        coordinator.CompleteAdding();
        var summary = await run;

        outcome.Released.ShouldHaveSingleItem().ShouldBe(path);
        summary.Uploaded.ShouldBe(1);

        // The decisive assertion: the remote copy is byte-for-byte the finished file, not any
        // intermediate state the gate might have released early.
        var uploaded = _server.Content(Destination.Append("arriving.raw")).ShouldNotBeNull();
        uploaded.Length.ShouldBe(Blocks * BlockSize);
        uploaded.ShouldBe(await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_file_held_open_by_an_instrument_is_not_uploaded_while_the_run_continues()
    {
        // The gate is given a bounded wait, as a manual scan would, and the "instrument" keeps
        // the file open throughout. Nothing may be released.
        var path = Path.Combine(_watched, "acquiring.raw");
        await File.WriteAllBytesAsync(path, new byte[4096]);

        await using var instrument = new FileStream(
            path, FileMode.Open, FileAccess.Write, FileShare.Read);

        var waitingReasons = new List<ReadinessReason>();

        var outcome = await NewGate().PumpAsync(
            [path],
            onReady: _ => throw new Xunit.Sdk.XunitException(
                "released a file the instrument still had open"),
            onWaiting: (_, readiness) => waitingReasons.Add(readiness.Reason),
            giveUpAfter: TimeSpan.FromSeconds(1.5));

        outcome.Released.ShouldBeEmpty();
        outcome.StillWaiting.Keys.ShouldContain(path);
        waitingReasons.ShouldContain(ReadinessReason.Locked);
    }

    [Fact]
    public async Task The_same_file_is_uploaded_once_the_instrument_lets_go()
    {
        // Continues the previous scenario: the run ends, and the file goes through normally.
        var path = Path.Combine(_watched, "finished.raw");
        var content = new byte[8192];
        Array.Fill(content, (byte)'Z');

        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await stream.WriteAsync(content);
        await stream.FlushAsync();

        // Let go after a moment, as an instrument does when acquisition completes.
        var release = Task.Run(async () =>
        {
            await Task.Delay(400);
            await stream.DisposeAsync();
        });

        await using var coordinator = NewCoordinator();

        var gateTask = NewGate().PumpAsync(
            [path],
            onReady: p => coordinator.EnqueueAsync(p),
            giveUpAfter: TimeSpan.FromSeconds(30));

        var run = coordinator.RunAsync();

        await release;
        await gateTask;
        coordinator.CompleteAdding();
        var summary = await run;

        summary.Uploaded.ShouldBe(1);
        _server.Content(Destination.Append("finished.raw")).ShouldBe(content);
    }

    [Fact]
    public async Task Several_files_arriving_at_once_are_each_released_when_they_are_ready()
    {
        // A batch copy: files finish at different times, and each should go as soon as it can
        // rather than everything waiting for the slowest.
        var paths = new List<string>();
        var copies = new List<Task>();
        var creations = new List<Task>();

        for (var i = 0; i < 5; i++)
        {
            var path = Path.Combine(_watched, $"batch{i}.raw");
            paths.Add(path);

            // Deliberately staggered durations.
            var created = new TaskCompletionSource();
            creations.Add(created.Task);
            copies.Add(Task.Run(() => WriteGraduallyAsync(
                path, blocks: 3 + i, blockSize: 16 * 1024, TimeSpan.FromMilliseconds(30), created)));
        }

        await Task.WhenAll(creations);

        await using var coordinator = NewCoordinator();

        var gateTask = NewGate().PumpAsync(
            paths,
            onReady: p => coordinator.EnqueueAsync(p),
            giveUpAfter: TimeSpan.FromSeconds(30));

        var run = coordinator.RunAsync();

        await Task.WhenAll(copies);
        var outcome = await gateTask;
        coordinator.CompleteAdding();
        var summary = await run;

        outcome.Released.Count.ShouldBe(5);
        summary.Uploaded.ShouldBe(5);

        // Every remote copy matches its source exactly.
        foreach (var path in paths)
        {
            _server.Content(Destination.Append(Path.GetFileName(path)))
                .ShouldBe(await File.ReadAllBytesAsync(path));
        }
    }

    [Fact]
    public async Task A_copy_that_is_cancelled_part_way_leaves_nothing_uploaded()
    {
        // An interrupted copy, and then the partial file is removed. Nothing should have been
        // sent, and the gate should stop asking about it.
        var path = Path.Combine(_watched, "abandoned.raw");

        using var cancelCopy = new CancellationTokenSource();
        var copy = Task.Run(async () =>
        {
            try
            {
                await WriteGraduallyAsync(
                    path, blocks: 100, blockSize: 16 * 1024,
                    TimeSpan.FromMilliseconds(20), created: null,
                    cancellationToken: cancelCopy.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        });

        await Task.Delay(200);
        await cancelCopy.CancelAsync();
        await copy;

        File.Delete(path);

        await using var coordinator = NewCoordinator();

        var outcome = await NewGate().PumpAsync(
            [path],
            onReady: p => coordinator.EnqueueAsync(p),
            giveUpAfter: TimeSpan.FromSeconds(5));

        coordinator.CompleteAdding();
        var summary = await coordinator.RunAsync();

        outcome.Released.ShouldBeEmpty();
        outcome.Abandoned.Keys.ShouldContain(path);
        outcome.Abandoned[path].Reason.ShouldBe(ReadinessReason.Missing);
        summary.Total.ShouldBe(0);
        _server.UploadCalls.ShouldBe(0);
    }

    [Fact]
    public async Task The_reason_for_waiting_is_reported_so_the_delay_can_be_explained()
    {
        // Without this the UI looks stalled, and someone reasonably concludes the application
        // has hung rather than that an instrument is still writing.
        var path = Path.Combine(_watched, "explain.raw");

        var created = new TaskCompletionSource();
        var copy = Task.Run(() => WriteGraduallyAsync(
            path, blocks: 6, blockSize: 8192, TimeSpan.FromMilliseconds(50), created));

        await created.Task;

        var details = new List<string>();

        var gateTask = NewGate().PumpAsync(
            [path],
            onReady: _ => Task.CompletedTask,
            onWaiting: (_, readiness) => details.Add(readiness.Detail),
            giveUpAfter: TimeSpan.FromSeconds(10));

        await copy;
        await gateTask;

        details.ShouldNotBeEmpty();
        details.ShouldContain(d =>
            d.Contains("open in another program") || d.Contains("Still being written"));
    }

    [Fact]
    public async Task Cancelling_the_gate_stops_it_promptly()
    {
        var path = Path.Combine(_watched, "cancelled.raw");
        await File.WriteAllBytesAsync(path, new byte[1024]);

        await using var held = new FileStream(
            path, FileMode.Open, FileAccess.Write, FileShare.Read);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Should.ThrowAsync<OperationCanceledException>(() => NewGate().PumpAsync(
            [path],
            onReady: _ => Task.CompletedTask,
            cancellationToken: cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();

        if (Directory.Exists(_watched))
        {
            Directory.Delete(_watched, recursive: true);
        }
    }
}
