using PanoramaBridge.Core.Monitoring;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;
using PanoramaBridge.Core.WebDav;
using PanoramaBridge.Tests.TestDoubles;

namespace PanoramaBridge.Tests.Monitoring;

/// <summary>
/// Monitoring end to end: a file arriving in a watched folder, through the sweep or the watcher,
/// through the readiness gate, into the transfer engine and onto the server.
/// </summary>
public sealed class ContinuousMonitorTests : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static readonly RemotePath Destination =
        RemotePath.Parse("/_webdav/MacCoss/maccoss/@files/uploads/");

    private readonly CountingStateStore _store = new();
    private readonly FakeWebDavClient _server = new();
    private readonly string _watched = Directory.CreateTempSubdirectory("pb-monitor-").FullName;
    private readonly CancellationTokenSource _stop = new();

    private MonitorOptions NewOptions(string? root = null) => new()
    {
        Root = root ?? _watched,
        DestinationRoot = Destination,
        Filter = new CandidateFilter([".raw"]),
        StabilityPeriod = TimeSpan.FromMilliseconds(200),
        ReconcileInterval = TimeSpan.FromMilliseconds(400),
        LockedFiles = LockedFilePolicy.None,
    };

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

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }

    /// <summary>Runs the monitor and the transfer engine together, as the application does.</summary>
    private async Task RunAsync(
        ContinuousMonitor monitor,
        TransferCoordinator coordinator,
        Func<Task> body)
    {
        var transfers = coordinator.RunAsync(CancellationToken.None);
        var monitoring = monitor.RunAsync(path => coordinator.EnqueueAsync(path), _stop.Token);

        try
        {
            await body();
        }
        finally
        {
            await _stop.CancelAsync();

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
        }
    }

    /// <summary>
    /// What the Check now button does: bring the next sweep forward instead of waiting for it.
    /// </summary>
    /// <remarks>
    /// The button's whole purpose is impatience -- somebody has put a file there and does not want
    /// to wait a quarter of an hour to see it move.
    ///
    /// Counted through the Swept event rather than by watching for the file to arrive, because
    /// change notifications would find a newly written file within milliseconds and the test would
    /// pass whether or not the request did anything. The reconcile interval is half an hour, so a
    /// second sweep inside the test's patience can only have come from the request.
    /// </remarks>
    [Fact]
    public async Task Asking_for_a_check_sweeps_now_rather_than_at_the_next_turn()
    {
        var options = NewOptions() with { ReconcileInterval = TimeSpan.FromMinutes(30) };

        await using var monitor = new ContinuousMonitor(_store, options);
        await using var coordinator = NewCoordinator();

        var sweeps = 0;
        monitor.Swept += _ => Interlocked.Increment(ref sweeps);

        await RunAsync(monitor, coordinator, async () =>
        {
            // The sweep every start performs.
            (await WaitForAsync(() => Volatile.Read(ref sweeps) >= 1)).ShouldBeTrue();
            var before = Volatile.Read(ref sweeps);

            monitor.RequestSweep("the user pressed Check now");

            (await WaitForAsync(() => Volatile.Read(ref sweeps) > before)).ShouldBeTrue(
                "asking for a check has to actually cause one, not wait 30 minutes");
        });
    }

    /// <summary>
    /// Hammering the button is harmless.
    /// </summary>
    /// <remarks>
    /// The pending-sweep signal holds one request at most, so every request beyond the first
    /// throws <see cref="SemaphoreFullException"/> internally and is swallowed. That catch is the
    /// thing under test: without it, a second click before the first sweep had started would put
    /// an exception on the UI thread.
    ///
    /// Deliberately not asserting how many sweeps a burst produces. Requests coalesce only while
    /// one is still pending, and on an idle folder a sweep can finish between two clicks, so the
    /// count is a property of timing rather than of the design. An earlier version of this test
    /// claimed otherwise and failed under load.
    /// </remarks>
    [Fact]
    public async Task Asking_for_a_check_repeatedly_never_throws()
    {
        await using var monitor = new ContinuousMonitor(_store, NewOptions());

        Should.NotThrow(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                monitor.RequestSweep("impatience");
            }
        });
    }

    [Fact]
    public async Task A_file_that_appears_in_the_watched_folder_is_uploaded_and_verified()
    {
        await using var monitor = new ContinuousMonitor(_store, NewOptions());
        await using var coordinator = NewCoordinator();

        var path = Path.Combine(_watched, "run1.raw");
        var content = new byte[96 * 1024];
        Array.Fill(content, (byte)'A');

        await RunAsync(monitor, coordinator, async () =>
        {
            await File.WriteAllBytesAsync(path, content);

            (await WaitForAsync(() => _server.Content(Destination.Append("run1.raw")) is not null))
                .ShouldBeTrue();
        });

        _server.Content(Destination.Append("run1.raw")).ShouldBe(content);

        var record = (await _store.GetAsync(path)).ShouldNotBeNull();
        record.State.ShouldBe(TransferState.Verified);
        record.VerifyMethod.ShouldBe(VerifyMethod.ServerMd5);
    }

    [Fact]
    public async Task A_file_already_present_when_monitoring_starts_is_found_by_the_first_sweep()
    {
        // Nothing announces a file that arrived while the application was closed, so the sweep at
        // startup is the only thing that can find it.
        var path = Path.Combine(_watched, "earlier.raw");
        await File.WriteAllTextAsync(path, "acquired before monitoring started");

        await using var monitor = new ContinuousMonitor(_store, NewOptions());
        await using var coordinator = NewCoordinator();

        await RunAsync(monitor, coordinator, async () =>
            (await WaitForAsync(() => _server.Content(Destination.Append("earlier.raw")) is not null))
                .ShouldBeTrue());
    }

    [Fact]
    public async Task A_file_written_the_way_a_copy_writes_one_does_not_wait_for_a_sweep()
    {
        // The sweep is the safety net, not the mechanism people experience. With the interval set
        // far beyond the life of this test, only a change notification can find the file -- which
        // is what makes this a test of promptness rather than of eventual delivery.
        //
        // Written the way a copy or an acquisition writes: held open, in blocks, with pauses. The
        // last write and the close land close together, and the notification for the close is the
        // one most easily lost.
        var path = Path.Combine(_watched, "copied.raw");
        var remote = Destination.Append("copied.raw");

        // The shipped locked-file policy, not the permissive one the other tests use. A file
        // being written is locked, and how patiently the gate treats a locked file is exactly
        // what decides whether this arrives now or in a quarter of an hour.
        var options = NewOptions() with
        {
            ReconcileInterval = TimeSpan.FromMinutes(30),
            LockedFiles = LockedFilePolicy.Default,
        };

        await using var monitor = new ContinuousMonitor(_store, options);
        await using var coordinator = NewCoordinator();

        await RunAsync(monitor, coordinator, async () =>
        {
            await using (var copy = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                for (var i = 0; i < 6; i++)
                {
                    await copy.WriteAsync(new byte[128 * 1024]);
                    await copy.FlushAsync();
                    await Task.Delay(300);
                }
            }

            (await WaitForAsync(() => _server.Content(remote) is not null))
                .ShouldBeTrue("the notification has to be enough on its own");
        });
    }

    [Fact]
    public async Task Sweeping_a_folder_that_is_already_transferred_costs_the_server_nothing()
    {
        // The steady state. This runs for the whole life of the application over a directory in
        // which nothing changes, so it has to be free -- no requests, no hashing, no uploads.
        var path = Path.Combine(_watched, "run1.raw");
        await File.WriteAllTextAsync(path, "acquisition");

        await using var monitor = new ContinuousMonitor(_store, NewOptions());
        await using var coordinator = NewCoordinator();

        var sweeps = 0;
        monitor.Swept += _ => Interlocked.Increment(ref sweeps);

        await RunAsync(monitor, coordinator, async () =>
        {
            (await WaitForAsync(() => _server.Content(Destination.Append("run1.raw")) is not null))
                .ShouldBeTrue();

            // Everything from here on is the steady state.
            _server.Reset();
            var settled = Volatile.Read(ref sweeps);

            (await WaitForAsync(() => Volatile.Read(ref sweeps) >= settled + 3))
                .ShouldBeTrue("the sweep keeps running whether or not it finds anything");

            _server.TotalCalls.ShouldBe(0, "a settled folder must not touch the server at all");
            _server.UploadCalls.ShouldBe(0);
        });
    }

    [Fact]
    public async Task A_file_still_being_written_when_the_sweep_finds_it_is_not_sent_early()
    {
        // The property everything else exists to protect. A sweep finds the file part way
        // through being written; nothing may be uploaded until the writer has let go.
        var path = Path.Combine(_watched, "acquiring.raw");
        var remote = Destination.Append("acquiring.raw");

        await using var monitor = new ContinuousMonitor(_store, NewOptions());
        await using var coordinator = NewCoordinator();

        await RunAsync(monitor, coordinator, async () =>
        {
            await using (var acquisition = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                for (var i = 0; i < 8; i++)
                {
                    var block = new byte[64 * 1024];
                    Array.Fill(block, (byte)('A' + i));

                    await acquisition.WriteAsync(block);
                    await acquisition.FlushAsync();
                    await Task.Delay(120);

                    _server.Content(remote).ShouldBeNull("the file is still open");
                }
            }

            (await WaitForAsync(() => _server.Content(remote) is not null)).ShouldBeTrue();
        });

        _server.Content(remote).ShouldBe(await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_monitored_folder_that_is_not_there_yet_is_waited_for_rather_than_fatal()
    {
        // A share that is not mounted when the application starts is ordinary, not exceptional.
        // Monitoring has to keep running and pick the folder up when it appears.
        var later = Path.Combine(_watched, "arrives-later");

        await using var monitor = new ContinuousMonitor(_store, NewOptions(root: later));

        await using var coordinator = new TransferCoordinator(
            _server,
            _store,
            new TransferEngineOptions
            {
                LocalBaseDirectory = later,
                DestinationRoot = Destination,
                MaxConcurrentTransfers = 1,
            });

        await RunAsync(monitor, coordinator, async () =>
        {
            (await WaitForAsync(() => monitor.Status.LastSweep?.Failed == true))
                .ShouldBeTrue("the first sweep reports the folder is not reachable");

            Directory.CreateDirectory(later);
            await File.WriteAllTextAsync(Path.Combine(later, "run1.raw"), "acquisition");

            (await WaitForAsync(() => _server.Content(Destination.Append("run1.raw")) is not null))
                .ShouldBeTrue("the folder appearing is enough; nothing has to be restarted");
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _stop.Dispose();
        await _store.DisposeAsync();

        try
        {
            Directory.Delete(_watched, recursive: true);
        }
        catch (IOException)
        {
            // A scratch folder left behind is better than a failed run.
        }
    }
}
