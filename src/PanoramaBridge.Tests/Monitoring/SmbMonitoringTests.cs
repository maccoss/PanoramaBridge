using PanoramaBridge.Core.Monitoring;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;
using PanoramaBridge.Core.WebDav;
using PanoramaBridge.Tests.TestDoubles;

namespace PanoramaBridge.Tests.Monitoring;

/// <summary>
/// Monitoring a folder on a remote SMB share.
/// </summary>
/// <remarks>
/// <para>
/// Instrument data frequently lands on a file server rather than a local disk, and a network
/// share behaves differently from a local volume in ways that matter here. Change notifications
/// may never arrive at all, depending on the server; and the SMB client caches file metadata --
/// ten seconds by default -- so the size Windows reports for a file can be badly out of date.
/// Either would be enough to make a naive monitor either miss files or upload half-written ones.
/// </para>
/// <para>
/// Opt-in, because it needs a writable share. Set <c>PANORAMABRIDGE_SMB_PATH</c> to a folder on
/// one, for example a UNC path or a mapped drive. Everything created is removed afterwards.
/// </para>
/// </remarks>
public sealed class SmbMonitoringTests : IAsyncDisposable
{
    private const string ShareVariable = "PANORAMABRIDGE_SMB_PATH";

    private static readonly RemotePath Destination =
        RemotePath.Parse("/_webdav/MacCoss/maccoss/@files/uploads/");

    private readonly string? _root;
    private readonly SqliteStateStore _store = SqliteStateStore.InMemory();
    private readonly FakeWebDavClient _server = new();

    public SmbMonitoringTests()
    {
        var share = Environment.GetEnvironmentVariable(ShareVariable);

        if (string.IsNullOrWhiteSpace(share) || !Directory.Exists(share))
        {
            return;
        }

        _root = Path.Combine(share, "pb-smb-tests-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_root);
    }

    private string Require()
    {
        Skip.If(_root is null, $"Set {ShareVariable} to a writable folder on an SMB share.");
        return _root!;
    }

    private string PathFor(string name) => Path.Combine(Require(), name);

    // -- The metadata cache -------------------------------------------------------------------

    [SkippableFact]
    public async Task The_size_windows_reports_can_be_stale_but_a_handle_tells_the_truth()
    {
        // This is the reason the stability check reads the length from an opened handle rather
        // than from FileInfo. Over SMB the client caches file metadata for ten seconds by
        // default, so a file being actively written can report the same size on consecutive
        // looks -- and a monitor built on that would conclude it had settled and upload a
        // partial acquisition.
        var path = PathFor("growing.raw");
        var block = new byte[256 * 1024];

        await using var writer = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.Read);

        await writer.WriteAsync(block);
        await writer.FlushAsync();

        var staleReadings = 0;
        long lastMetadata = -1;

        for (var i = 0; i < 6; i++)
        {
            await writer.WriteAsync(block);
            await writer.FlushAsync();

            var expected = (i + 2) * (long)block.Length;

            // What Windows reports from the directory entry, subject to the client cache.
            var metadata = new FileInfo(path).Length;

            // What the file actually is, read through a handle.
            long fromHandle;
            await using (var reader = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fromHandle = reader.Length;
            }

            fromHandle.ShouldBe(expected, "a handle must always see the real end of file");

            if (metadata != expected || metadata == lastMetadata)
            {
                staleReadings++;
            }

            lastMetadata = metadata;
            await Task.Delay(150);
        }

        // Recorded rather than asserted: whether the cache actually goes stale depends on the
        // server and on timing. The point being proven is the handle reading, which is exact
        // either way. If this ever reports zero on every server anyone uses, the handle read is
        // still correct -- just no longer load-bearing.
        Console.WriteLine(
            $"[smb] metadata readings that were stale or unchanged: {staleReadings} of 6");
    }

    // -- Detecting a file still in use --------------------------------------------------------

    [SkippableFact]
    public void A_file_held_open_on_a_share_is_reported_as_locked()
    {
        // Share modes are enforced by the SMB server, not just locally, so this has to be
        // confirmed against a real share rather than assumed from local behaviour.
        var path = PathFor("held.raw");
        File.WriteAllBytes(path, new byte[4096]);

        var tracker = new FileStabilityTracker(TimeSpan.Zero);

        using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            tracker.Check(path);
            var readiness = tracker.Check(path);

            readiness.IsReady.ShouldBeFalse("something still holds the file");
            readiness.Reason.ShouldBe(ReadinessReason.Locked);
        }

        // Released. Size unchanged and the quiet period is zero, so the very next check
        // releases it. Deliberately asserted on the first check: the tracker stops following a
        // file once it reports ready, so asking twice starts the observation over and would
        // report "settling" again.
        tracker.Check(path).IsReady.ShouldBeTrue();
    }

    [SkippableFact]
    public async Task A_file_being_written_to_a_share_is_never_released_early()
    {
        // The scenario that matters: an acquisition writing to a file server while this
        // application watches the same folder.
        var path = PathFor("acquiring.raw");
        var tracker = new FileStabilityTracker(TimeSpan.FromSeconds(1));
        var releasedEarly = false;

        await using (var acquisition = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            for (var i = 0; i < 12; i++)
            {
                await acquisition.WriteAsync(new byte[128 * 1024]);
                await acquisition.FlushAsync();

                // Long enough between writes to satisfy any quiet-period check on its own.
                await Task.Delay(150);

                if (tracker.Check(path).IsReady)
                {
                    releasedEarly = true;
                }
            }
        }

        releasedEarly.ShouldBeFalse("nothing may be released while the file is still open");

        tracker.Check(path);
        await Task.Delay(1200);
        tracker.Check(path).IsReady.ShouldBeTrue("the write finished and the handle was closed");
    }

    // -- Change notifications -----------------------------------------------------------------

    [SkippableFact]
    public async Task Whether_change_notifications_arrive_is_recorded_not_relied_upon()
    {
        // FileSystemWatcher over SMB depends on the server supporting change notify, and plenty
        // of appliances do not. The design therefore treats notifications as an optimisation and
        // a periodic sweep as the mechanism. This measures which we are getting, so the answer
        // is known rather than assumed.
        var root = Require();
        var observed = 0;

        using var watcher = new FileSystemWatcher(root)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            IncludeSubdirectories = true,
        };

        watcher.Created += (_, _) => Interlocked.Increment(ref observed);
        watcher.Changed += (_, _) => Interlocked.Increment(ref observed);

        try
        {
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            // Some servers refuse to register a watch at all, which is exactly the case the
            // sweep exists for.
            Console.WriteLine($"[smb] the share refused a watch: {ex.Message}");
            return;
        }

        for (var i = 0; i < 3; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(root, $"notify{i}.txt"), "hello");
        }

        await Task.Delay(2500);

        Console.WriteLine($"[smb] change notifications observed for 3 new files: {observed}");

        // Deliberately no assertion on the count. The sweep below is what must work.
    }

    [SkippableFact]
    public async Task A_directory_sweep_finds_files_on_a_share_regardless_of_notifications()
    {
        // The mechanism the design actually depends on. Whatever the server does about change
        // notify, enumerating the folder has to find what is there.
        var root = Require();

        await File.WriteAllTextAsync(Path.Combine(root, "swept.raw"), "acquisition");
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        await File.WriteAllTextAsync(Path.Combine(root, "nested", "deeper.raw"), "more");

        var found = Directory
            .EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
                    | FileAttributes.ReparsePoint,
            })
            .Select(Path.GetFileName)
            .ToList();

        found.ShouldContain("swept.raw");
        found.ShouldContain("deeper.raw");
    }

    // -- Path handling -------------------------------------------------------------------------

    [SkippableFact]
    public void A_share_path_maps_onto_the_remote_folder_structure()
    {
        // A UNC base directory has to produce the same relative structure a local one does; a
        // leading double separator is easy to get wrong.
        var root = Require();
        var local = Path.Combine(root, "2026", "batch 7", "sample.raw");

        var resolved = PathSafety.ResolveDestination(root, local, Destination);

        resolved.ToEncodedString()
            .ShouldBe("/_webdav/MacCoss/maccoss/@files/uploads/2026/batch%207/sample.raw");
        resolved.IsUnder(Destination).ShouldBeTrue();
    }

    [Fact]
    public void A_unc_base_directory_resolves_without_needing_a_real_share()
    {
        // Pure path arithmetic, so it runs everywhere and guards the UNC case even when no
        // share is configured.
        var unc = @"\\fileserver\instruments\QE";

        PathSafety.ResolveDestination(
                unc,
                Path.Combine(unc, "2026", "run 3.raw"),
                Destination)
            .ToEncodedString()
            .ShouldBe("/_webdav/MacCoss/maccoss/@files/uploads/2026/run%203.raw");

        // A file outside the share must still be refused.
        Should.Throw<ArgumentException>(() => PathSafety.ResolveDestination(
            unc, @"\\fileserver\other\elsewhere.raw", Destination));
    }

    // -- End to end ----------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_file_copied_onto_a_share_is_uploaded_complete_and_intact()
    {
        // The whole path: a file arriving on a network share, through the readiness gate, into
        // the transfer engine, byte-for-byte.
        var root = Require();
        var path = Path.Combine(root, "arriving.raw");
        const int Blocks = 8;
        const int BlockSize = 128 * 1024;

        var created = new TaskCompletionSource();

        var copy = Task.Run(async () =>
        {
            await using var stream = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.Read);

            created.TrySetResult();

            for (var i = 0; i < Blocks; i++)
            {
                var block = new byte[BlockSize];
                Array.Fill(block, (byte)('A' + i));
                await stream.WriteAsync(block);
                await stream.FlushAsync();
                await Task.Delay(120);
            }
        });

        await created.Task;

        await using var coordinator = new TransferCoordinator(
            _server,
            _store,
            new TransferEngineOptions
            {
                LocalBaseDirectory = root,
                DestinationRoot = Destination,
                MaxConcurrentTransfers = 2,
            });

        var gate = new ReadinessGate(
            new FileStabilityTracker(TimeSpan.FromSeconds(1)),
            TimeSpan.FromMilliseconds(200));

        var gateTask = gate.PumpAsync(
            [path],
            onReady: p => coordinator.EnqueueAsync(p),
            giveUpAfter: TimeSpan.FromSeconds(60));

        var run = coordinator.RunAsync();

        await copy;
        var outcome = await gateTask;
        coordinator.CompleteAdding();
        var summary = await run;

        outcome.Released.ShouldHaveSingleItem();
        summary.Uploaded.ShouldBe(1);

        var uploaded = _server.Content(Destination.Append("arriving.raw")).ShouldNotBeNull();
        uploaded.Length.ShouldBe(Blocks * BlockSize);
        uploaded.ShouldBe(await File.ReadAllBytesAsync(path));
    }

    // -- Continuous monitoring -----------------------------------------------------------------

    [SkippableFact]
    public async Task Monitoring_a_share_transfers_a_file_that_appears_on_it()
    {
        // The whole mechanism against a real file server: the sweep, the watcher, the readiness
        // gate and the engine, with a file arriving while it all runs.
        var root = Require();

        await using var monitor = new ContinuousMonitor(
            _store,
            new MonitorOptions
            {
                Root = root,
                DestinationRoot = Destination,
                Filter = new CandidateFilter([".raw"]),
                StabilityPeriod = TimeSpan.FromSeconds(1),
                ReconcileInterval = TimeSpan.FromSeconds(5),
                LockedFiles = LockedFilePolicy.None,
            });

        await using var coordinator = new TransferCoordinator(
            _server,
            _store,
            new TransferEngineOptions
            {
                LocalBaseDirectory = root,
                DestinationRoot = Destination,
                MaxConcurrentTransfers = 2,
            });

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var transfers = coordinator.RunAsync(CancellationToken.None);
        var monitoring = monitor.RunAsync(path => coordinator.EnqueueAsync(path), stop.Token);

        var content = new byte[256 * 1024];
        Array.Fill(content, (byte)'M');
        await File.WriteAllBytesAsync(Path.Combine(root, "monitored.raw"), content);

        var remote = Destination.Append("monitored.raw");
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);

        while (_server.Content(remote) is null && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        // Sampled before stopping: the watch is torn down on the way out, so asking afterwards
        // would always report that notifications were unavailable.
        var watching = monitor.Status.WatchingForChanges;

        await stop.CancelAsync();

        try
        {
            await monitoring;
        }
        catch (OperationCanceledException)
        {
            // How a cancelled monitor is supposed to end.
        }

        coordinator.CompleteAdding();
        await transfers;

        _server.Content(remote).ShouldBe(content);

        Console.WriteLine(
            $"[smb] change notifications were {(watching ? "" : "not ")}available on this share");
    }

    [SkippableFact]
    public async Task What_a_sweep_of_a_share_costs_is_recorded()
    {
        // Walking a network folder is a different cost from walking a local disk, and idle cost
        // is the one thing this application has to be careful about. Recorded rather than
        // asserted: the number depends entirely on the server and the size of the tree, so the
        // point is to have it written down for whoever changes the reconciliation interval.
        var root = Require();

        for (var i = 0; i < 25; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(root, $"swept{i:D2}.raw"), "acquisition");
        }

        var scanner = new ReconciliationScanner(
            _store,
            new ReconciliationOptions
            {
                Root = root,
                DestinationRoot = Destination,
                Filter = new CandidateFilter([".raw"]),
            });

        // The first sweep pays for whatever the SMB client has not cached yet; the second is the
        // one that repeats every quarter of an hour for the rest of the day.
        var first = await scanner.SweepAsync((_, _) => Task.CompletedTask);
        var second = await scanner.SweepAsync((_, _) => Task.CompletedTask);

        first.Failed.ShouldBeFalse();
        first.Examined.ShouldBeGreaterThanOrEqualTo(25);

        Console.WriteLine(
            $"[smb] sweep of {first.Examined} file(s): "
            + $"{first.Elapsed.TotalMilliseconds:F0} ms cold, "
            + $"{second.Elapsed.TotalMilliseconds:F0} ms warm");
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();

        if (_root is not null && Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A share can hold a handle briefly after a test closes it; leaving one empty
                // scratch folder behind is better than failing the run over cleanup.
            }
        }
    }
}
