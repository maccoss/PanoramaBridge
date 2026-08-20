using System.Collections.Concurrent;
using System.Threading.Channels;
using PanoramaBridge.Core.Monitoring;

namespace PanoramaBridge.Tests.Monitoring;

/// <summary>
/// The gate in continuous mode: a stream of candidates that never closes.
/// </summary>
/// <remarks>
/// Real files and real time with short intervals, for the same reason the rest of the monitoring
/// tests work that way -- what is being checked is how a concurrent writer and the gate
/// interleave, which a fake clock would paper over.
/// </remarks>
public sealed class ReadinessGateWatchTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly string _watched = Directory.CreateTempSubdirectory("pb-watchgate-").FullName;
    private readonly Channel<string> _candidates = Channel.CreateUnbounded<string>();
    private readonly ConcurrentQueue<GateReport> _reports = new();
    private readonly ConcurrentQueue<string> _released = new();
    private readonly CancellationTokenSource _stop = new();

    private Task Watch(ReadinessGate gate) =>
        gate.WatchAsync(
            _candidates.Reader,
            path =>
            {
                _released.Enqueue(path);
                return Task.CompletedTask;
            },
            report => _reports.Enqueue(report),
            _stop.Token);

    private static ReadinessGate NewGate(
        double quietSeconds = 0.3,
        double pollSeconds = 0.1,
        LockedFilePolicy? lockedFiles = null) =>
        new(
            new FileStabilityTracker(TimeSpan.FromSeconds(quietSeconds)),
            TimeSpan.FromSeconds(pollSeconds),
            lockedFiles ?? LockedFilePolicy.None);

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

    private string PathFor(string name) => Path.Combine(_watched, name);

    /// <summary>Cancels the watch and waits for it, treating the cancellation as expected.</summary>
    private async Task StopAsync(Task watching)
    {
        await _stop.CancelAsync();

        try
        {
            await watching;
        }
        catch (OperationCanceledException)
        {
            // How a cancelled watch is supposed to end.
        }
    }

    [Fact]
    public async Task A_file_still_being_written_is_released_only_once_it_is_finished()
    {
        var path = PathFor("acquiring.raw");
        var watching = Watch(NewGate());

        await using (var acquisition = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            await acquisition.WriteAsync(new byte[64 * 1024]);
            await acquisition.FlushAsync();

            _candidates.Writer.TryWrite(path).ShouldBeTrue();

            for (var i = 0; i < 6; i++)
            {
                await acquisition.WriteAsync(new byte[64 * 1024]);
                await acquisition.FlushAsync();
                await Task.Delay(100);

                _released.ShouldBeEmpty("nothing may be released while the file is still open");
            }
        }

        (await WaitForAsync(() => !_released.IsEmpty)).ShouldBeTrue();
        _released.ShouldContain(path);

        _candidates.Writer.TryComplete();
        await watching;
    }

    [Fact]
    public async Task An_idle_gate_does_no_work_at_all()
    {
        // With nothing pending it blocks on the channel rather than polling it. This is what an
        // instrument computer sees for almost the whole life of the application, and a gate that
        // woke up to check an empty list would be exactly the recurring idle cost the design
        // exists to avoid.
        var watching = Watch(NewGate());

        await Task.Delay(750);

        _reports.ShouldBeEmpty();
        watching.IsCompleted.ShouldBeFalse("it is waiting, not finished");

        _candidates.Writer.TryComplete();
        await watching;

        watching.IsCompletedSuccessfully.ShouldBeTrue("a closed stream ends the watch cleanly");
    }

    [Fact]
    public async Task A_file_held_open_is_looked_at_again_and_released_as_soon_as_it_is_let_go()
    {
        // The behaviour a long deferral broke, and the reason there is no longer one. Nothing
        // announces that a handle has been closed -- there is no notification for it and the file
        // does not change -- so the only way to find out is to look. A gate that stops looking
        // leaves the file sitting there until the periodic folder check comes round.
        var path = PathFor("held.raw");

        var holder = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await holder.WriteAsync(new byte[4096]);
        await holder.FlushAsync();

        var watching = Watch(NewGate(
            lockedFiles: new LockedFilePolicy(TimeSpan.FromMilliseconds(200), MaxChecks: 100)));

        _candidates.Writer.TryWrite(path);

        // The first look never judges a file; the second finds it in use.
        (await WaitForAsync(() =>
            _reports.Any(r => r.Readiness.Reason == ReadinessReason.Locked))).ShouldBeTrue();

        _released.ShouldBeEmpty("something still holds it");

        // Released with no further notification and no change to the file.
        await holder.DisposeAsync();

        (await WaitForAsync(() => !_released.IsEmpty))
            .ShouldBeTrue("nothing else will ever tell the gate the handle was closed");

        _candidates.Writer.TryComplete();
        await watching;
    }

    [Fact]
    public async Task A_file_in_use_is_looked_at_no_faster_than_the_agreed_cadence()
    {
        // The other half of the same trade. Looking is what makes it prompt; looking constantly
        // is what would make it unwelcome on the machine running the instrument.
        var path = PathFor("held.raw");

        await using var holder = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.Read);

        await holder.WriteAsync(new byte[4096]);
        await holder.FlushAsync();

        var watching = Watch(NewGate(
            pollSeconds: 0.4,
            lockedFiles: new LockedFilePolicy(TimeSpan.FromMilliseconds(400), MaxChecks: 100)));

        _candidates.Writer.TryWrite(path);

        await Task.Delay(2000);

        // Two seconds at four hundred milliseconds is at most six looks, allowing for scheduling.
        _reports.Count.ShouldBeLessThanOrEqualTo(6);
        _reports.Count.ShouldBeGreaterThanOrEqualTo(2, "but it is definitely still looking");

        await StopAsync(watching);
    }

    [Fact]
    public async Task Running_out_of_patience_hands_the_file_back_rather_than_dropping_it()
    {
        // Giving up must never mean forgetting. The sweep offers it again on its next pass, so
        // a file held open all day is transferred the moment it is finally released.
        var path = PathFor("stuck.raw");

        await using var holder = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.Read);

        await holder.WriteAsync(new byte[4096]);
        await holder.FlushAsync();

        var watching = Watch(NewGate(
            lockedFiles: new LockedFilePolicy(TimeSpan.FromMilliseconds(50), MaxChecks: 3)));

        _candidates.Writer.TryWrite(path);

        (await WaitForAsync(() => _reports.Any(r => !r.StillWatching))).ShouldBeTrue();

        var final = _reports.Last(r => !r.StillWatching);
        final.Path.ShouldBe(path);
        final.Readiness.Reason.ShouldBe(ReadinessReason.Locked);
        final.Readiness.Detail.ShouldContain("next folder check");

        // Nothing further, because it is no longer being followed.
        var seen = _reports.Count;
        await Task.Delay(500);
        _reports.Count.ShouldBe(seen);

        _candidates.Writer.TryComplete();
        await watching;
    }

    [Fact]
    public async Task A_file_that_disappears_is_not_waited_on_for_ever()
    {
        // Mid-copy deletions and moves happen. Asking again will not bring it back.
        var path = PathFor("vanished.raw");
        var watching = Watch(NewGate());

        _candidates.Writer.TryWrite(path);

        (await WaitForAsync(() => _reports.Any(r => !r.StillWatching))).ShouldBeTrue();
        _reports.Last().Readiness.Reason.ShouldBe(ReadinessReason.Missing);

        _candidates.Writer.TryComplete();
        await watching;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();

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
