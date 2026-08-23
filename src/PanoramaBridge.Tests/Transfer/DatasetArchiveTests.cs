using System.IO.Compression;
using PanoramaBridge.Core.Transfer;

namespace PanoramaBridge.Tests.Transfer;

/// <summary>
/// Packing a directory acquisition into the single file Panorama stores.
/// </summary>
/// <remarks>
/// The archive is the transfer item, so anything wrong with it is wrong with the upload. What
/// matters here is that it contains exactly the acquisition, that it never touches the source,
/// and that a failure leaves nothing behind on a machine where a stray six-gigabyte temporary
/// file is its own problem.
/// </remarks>
public sealed class DatasetArchiveTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pb-archive-").FullName;

    private string Acquisition(params (string Name, string Content)[] files)
    {
        var folder = Path.Combine(_root, "250314_HeLa_DIA_01.d");
        Directory.CreateDirectory(folder);

        foreach (var (name, content) in files)
        {
            var path = Path.Combine(folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        return folder;
    }

    private string ArchivePath => Path.Combine(_root, "out", "250314_HeLa_DIA_01.d.zip");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task The_archive_holds_exactly_what_the_acquisition_held()
    {
        var folder = Acquisition(
            ("analysis.tdf", "the sqlite index"),
            ("analysis.tdf_bin", "the binary data"),
            (Path.Combine("inner", "extra.bin"), "something nested"));

        var result = await DatasetArchive.CreateAsync(folder, ArchivePath, expectedBytes: 100);

        result.Succeeded.ShouldBeTrue(result.Detail);

        using var zip = ZipFile.OpenRead(result.Path!);

        zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ShouldBe(
            ["analysis.tdf", "analysis.tdf_bin", "inner/extra.bin"]);

        using var reader = new StreamReader(zip.GetEntry("analysis.tdf")!.Open());
        (await reader.ReadToEndAsync()).ShouldBe("the sqlite index");
    }

    [Fact]
    public async Task Paths_inside_the_archive_are_relative_and_use_forward_slashes()
    {
        // What the zip format specifies and what every reader, on every platform, expects. A
        // Windows-separated entry name is readable by some tools and not others.
        var folder = Acquisition((Path.Combine("inner", "deep", "file.bin"), "x"));

        var result = await DatasetArchive.CreateAsync(folder, ArchivePath, expectedBytes: 10);

        using var zip = ZipFile.OpenRead(result.Path!);

        zip.Entries.ShouldHaveSingleItem();
        zip.Entries[0].FullName.ShouldBe("inner/deep/file.bin");
        zip.Entries[0].FullName.ShouldNotContain("\\");
    }

    [Fact]
    public async Task The_acquisition_is_not_touched()
    {
        // It is the instrument's data. Packing it must be a read and nothing else.
        var folder = Acquisition(("analysis.tdf", "the sqlite index"));
        var file = Path.Combine(folder, "analysis.tdf");

        var before = await File.ReadAllBytesAsync(file);
        var written = File.GetLastWriteTimeUtc(file);

        await DatasetArchive.CreateAsync(folder, ArchivePath, expectedBytes: 100);

        File.ReadAllBytes(file).ShouldBe(before);
        File.GetLastWriteTimeUtc(file).ShouldBe(written);
    }

    [Fact]
    public async Task Packing_does_not_stop_a_reader_of_the_acquisition()
    {
        // Shared for reading, so this can never be the reason something else failed to read an
        // instrument's own output.
        var folder = Acquisition(("analysis.tdf", "data"));

        using var held = new FileStream(
            Path.Combine(folder, "analysis.tdf"),
            FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = await DatasetArchive.CreateAsync(folder, ArchivePath, expectedBytes: 100);

        result.Succeeded.ShouldBeTrue(result.Detail);
    }

    [Fact]
    public async Task Progress_is_reported_against_bytes_read()
    {
        var folder = Acquisition(("analysis.tdf", new string('x', 4096)));

        var reports = new List<long>();
        var result = await DatasetArchive.CreateAsync(
            folder, ArchivePath, expectedBytes: 4096, new Progress<long>(reports.Add));

        result.Succeeded.ShouldBeTrue();

        // Progress<T> posts, so give the callbacks a moment to land.
        await Task.Delay(100);
        reports.ShouldNotBeEmpty();
        reports[^1].ShouldBe(4096);
    }

    [Fact]
    public async Task A_missing_acquisition_is_reported_rather_than_thrown()
    {
        var result = await DatasetArchive.CreateAsync(
            Path.Combine(_root, "gone.d"), ArchivePath, expectedBytes: 100);

        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldBe(ArchiveFailure.SourceMissing);
        File.Exists(ArchivePath).ShouldBeFalse();
    }

    [Fact]
    public async Task An_acquisition_too_large_for_the_disk_is_refused_before_anything_is_written()
    {
        // Filling the disk on an instrument computer is far worse than declining to transfer
        // something, so the check is pessimistic and happens first.
        var folder = Acquisition(("analysis.tdf", "small in reality"));

        var result = await DatasetArchive.CreateAsync(
            folder, ArchivePath, expectedBytes: long.MaxValue / 2);

        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldBe(ArchiveFailure.NotEnoughRoom);
        result.Detail.ShouldNotBeNullOrWhiteSpace("it has to say how much is needed and available");
        File.Exists(ArchivePath).ShouldBeFalse("nothing should have been written");
    }

    [Fact]
    public async Task Cancelling_leaves_no_partial_archive_behind()
    {
        var folder = Acquisition(
            ("a.bin", new string('a', 200_000)),
            ("b.bin", new string('b', 200_000)),
            ("c.bin", new string('c', 200_000)));

        using var stopping = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            DatasetArchive.CreateAsync(
                folder,
                ArchivePath,
                expectedBytes: 600_000,
                progress: new Progress<long>(_ => stopping.Cancel()),
                cancellationToken: stopping.Token));

        File.Exists(ArchivePath).ShouldBeFalse(
            "a half-written multi-gigabyte temporary file is its own problem");
    }

    [Fact]
    public async Task Discarding_an_archive_that_is_not_there_is_harmless()
    {
        Should.NotThrow(() => DatasetArchive.Discard(Path.Combine(_root, "never-made.zip")));
        Should.NotThrow(() => DatasetArchive.Discard(null));

        await Task.CompletedTask;
    }

    [Fact]
    public async Task An_empty_acquisition_still_produces_a_readable_archive()
    {
        // Guarded upstream by the readiness tracker, which never releases an empty folder. If it
        // ever reaches here it must not produce something corrupt.
        var folder = Path.Combine(_root, "empty.d");
        Directory.CreateDirectory(folder);

        var result = await DatasetArchive.CreateAsync(folder, ArchivePath, expectedBytes: 0);

        result.Succeeded.ShouldBeTrue();
        using var zip = ZipFile.OpenRead(result.Path!);
        zip.Entries.ShouldBeEmpty();
    }
}
