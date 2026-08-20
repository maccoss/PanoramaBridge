using System.Collections.Concurrent;
using PanoramaBridge.Core.Monitoring;

namespace PanoramaBridge.Tests.Monitoring;

/// <summary>
/// The change watcher, against real file system notifications.
/// </summary>
/// <remarks>
/// Real events rather than a simulated stream, because everything interesting here is about what
/// Windows actually delivers: duplicates for a single arrival, events for directories, a rename
/// where a creation was expected. A test that invented its own events would agree with whatever
/// the code assumed.
/// </remarks>
public sealed class DirectoryMonitorTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly string _root = Directory.CreateTempSubdirectory("pb-watch-").FullName;
    private readonly ConcurrentQueue<string> _reported = new();
    private DirectoryMonitor? _monitor;

    private DirectoryMonitor Start(TimeSpan? window = null)
    {
        _monitor = new DirectoryMonitor(
            _root,
            includeSubdirectories: true,
            new CandidateFilter([".raw"]),
            window ?? TimeSpan.FromSeconds(5));

        _monitor.Changed += path => _reported.Enqueue(path);
        _monitor.Start().ShouldBeTrue();

        return _monitor;
    }

    private async Task<bool> WaitForAsync(Func<bool> condition)
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

    [Fact]
    public async Task A_file_arriving_is_reported()
    {
        Start();

        var path = Path.Combine(_root, "run1.raw");
        await File.WriteAllTextAsync(path, "acquisition");

        (await WaitForAsync(() => !_reported.IsEmpty)).ShouldBeTrue();
        _reported.ShouldContain(path);
    }

    [Fact]
    public async Task A_file_arriving_by_rename_is_reported_too()
    {
        // Instrument and copy software routinely write to a working name and rename on
        // completion. That arrives as a rename, not a creation, so watching only for creations
        // means every such file waits for the next sweep.
        Start();

        var working = Path.Combine(_root, "~working.tmp");
        await File.WriteAllTextAsync(working, "acquisition");

        var finished = Path.Combine(_root, "run1.raw");
        File.Move(working, finished);

        (await WaitForAsync(() => _reported.Contains(finished))).ShouldBeTrue();
    }

    [Fact]
    public async Task Repeated_notifications_for_one_file_collapse_into_one()
    {
        // The share this was measured against sent three notifications for every new file, and a
        // file being appended to produces one per write. Passing them all on would put the same
        // path through the readiness gate over and over; the first is enough, because the gate
        // keeps watching until the file settles.
        Start(window: TimeSpan.FromSeconds(30));

        var path = Path.Combine(_root, "run1.raw");

        for (var i = 0; i < 6; i++)
        {
            await File.AppendAllTextAsync(path, "more data");
        }

        (await WaitForAsync(() => !_reported.IsEmpty)).ShouldBeTrue();
        await Task.Delay(500);

        _reported.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Files_that_are_not_data_are_never_reported()
    {
        Start();

        await File.WriteAllTextAsync(Path.Combine(_root, "notes.txt"), "not data");
        await File.WriteAllTextAsync(Path.Combine(_root, "~working.raw"), "not data either");

        var real = Path.Combine(_root, "run1.raw");
        await File.WriteAllTextAsync(real, "acquisition");

        // Waiting for the real file proves the watcher was alive for the other two as well.
        (await WaitForAsync(() => _reported.Contains(real))).ShouldBeTrue();
        await Task.Delay(300);

        _reported.ShouldBe([real]);
    }

    [Fact]
    public async Task A_directory_whose_name_matches_is_not_offered_as_a_file()
    {
        // Directories raise these events too. One handed to the readiness gate would be opened,
        // fail, and tell the user their file could not be read. Folder acquisitions are a
        // different kind of transfer item and do not arrive through here.
        Start();

        Directory.CreateDirectory(Path.Combine(_root, "dataset.raw"));

        var real = Path.Combine(_root, "run1.raw");
        await File.WriteAllTextAsync(real, "acquisition");

        (await WaitForAsync(() => _reported.Contains(real))).ShouldBeTrue();
        await Task.Delay(300);

        _reported.ShouldBe([real]);
    }

    [Fact]
    public void A_folder_that_cannot_be_watched_fails_softly()
    {
        // Some SMB servers refuse to register a watch at all. On those the sweep carries the
        // whole job, so this must be a note rather than an error that stops monitoring.
        using var monitor = new DirectoryMonitor(
            Path.Combine(_root, "not-mounted"),
            includeSubdirectories: true,
            CandidateFilter.Everything);

        monitor.Start().ShouldBeFalse();
        monitor.IsWatching.ShouldBeFalse();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _monitor?.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A scratch folder left behind is better than a failed run.
        }
    }
}
