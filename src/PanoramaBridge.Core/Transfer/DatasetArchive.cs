using System.IO.Compression;

namespace PanoramaBridge.Core.Transfer;

/// <summary>Why an archive could not be built.</summary>
public enum ArchiveFailure
{
    /// <summary>It was built.</summary>
    None = 0,

    /// <summary>The folder is not there any more.</summary>
    SourceMissing = 1,

    /// <summary>Not enough room to write the archive.</summary>
    NotEnoughRoom = 2,

    /// <summary>Something changed inside the folder while it was being read.</summary>
    ChangedWhileReading = 3,

    /// <summary>The archive could not be written.</summary>
    WriteFailed = 4,
}

/// <summary>The outcome of building one archive.</summary>
/// <param name="Path">Where it was written, when it was.</param>
/// <param name="Bytes">Its size.</param>
/// <param name="Failure">Why not, when it was not.</param>
/// <param name="Detail">Something a person can act on.</param>
public sealed record ArchiveResult(
    string? Path,
    long Bytes,
    ArchiveFailure Failure,
    string? Detail)
{
    /// <summary>True when there is an archive to upload.</summary>
    public bool Succeeded => Failure == ArchiveFailure.None && Path is not null;
}

/// <summary>
/// Packs a directory acquisition into the single file Panorama stores.
/// </summary>
/// <remarks>
/// <para>
/// A Bruker <c>.d</c> arrives on Panorama as one <c>.d.zip</c>. That is how this lab already
/// stores them, and it is a much better shape than uploading the files inside one at a time: one
/// object either verifies against the server's checksum or it does not, so nothing has to
/// engineer atomicity across a set of uploads, and every existing mechanism -- verification, the
/// checksum sidecar, conflict handling, the ledger -- works on it unchanged.
/// </para>
/// <para>
/// <b>Stored, not compressed.</b> Bruker's binary data is already compressed, so deflate buys
/// almost nothing on it while costing real processor time -- on the computer attached to the
/// mass spectrometer, where the instrument comes first. The archive is a container, not a
/// squeeze. If a future format turns out to compress well, this is the one line to revisit, and
/// it should be revisited with a measurement rather than an intuition.
/// </para>
/// <para>
/// Nothing here writes to the acquisition. Every file is opened read-only and shared for reading,
/// so packing one can never be the reason an instrument's own write failed.
/// </para>
/// </remarks>
public static class DatasetArchive
{
    /// <summary>
    /// Where to build the archive for an acquisition: beside it, under a working name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Beside the acquisition rather than in a scratch directory elsewhere, because that volume
    /// already holds the data and so is the one most likely to have room for it, and because
    /// writing there is a same-volume operation rather than a copy across two.
    /// </para>
    /// <para>
    /// The name is tilde-prefixed, which is not decoration.
    /// <see cref="Monitoring.CandidateFilter"/> already rejects dot- and tilde-prefixed names as
    /// working files, so the archive cannot be mistaken for an acquisition and offered for
    /// transfer in its own right. Without that, building it inside the monitored folder would
    /// hand the sweep a six-gigabyte candidate that appears every time a dataset is packed.
    /// </para>
    /// <para>
    /// One consequence worth knowing: when the acquisition lives on a network share, so does the
    /// archive, and the bytes cross the wire three times -- read, written, read again -- before
    /// the upload. Staging locally would halve that at the cost of needing the room on the system
    /// drive. Beside the data is the simpler default and the one that cannot fail for want of
    /// space somewhere the user did not choose.
    /// </para>
    /// </remarks>
    public static string StagingPathFor(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var full = Path.GetFullPath(folder.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var parent = Path.GetDirectoryName(full)
            ?? throw new ArgumentException(
                $"{folder} has no parent directory to build an archive in.", nameof(folder));

        return Path.Combine(parent, "~" + Path.GetFileName(full) + ".zip");
    }

    /// <summary>Room required beyond the acquisition's own size, as a safety margin.</summary>
    /// <remarks>
    /// Filling the disk on an instrument computer is a far worse outcome than declining to
    /// transfer something, so the check is deliberately pessimistic.
    /// </remarks>
    private const long HeadroomBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Builds the archive, reporting bytes read as it goes.
    /// </summary>
    /// <param name="folder">The acquisition directory.</param>
    /// <param name="archivePath">Where to write it. Overwritten if present.</param>
    /// <param name="expectedBytes">
    /// What the folder measured, used for the free-space check and for progress.
    /// </param>
    /// <param name="progress">Bytes read from the acquisition so far.</param>
    /// <param name="cancellationToken">Abandons the archive and removes the partial file.</param>
    public static async Task<ArchiveResult> CreateAsync(
        string folder,
        string archivePath,
        long expectedBytes,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        if (!Directory.Exists(folder))
        {
            return Failed(ArchiveFailure.SourceMissing, $"{folder} is not there.");
        }

        if (RoomFor(archivePath) is { } free && free < expectedBytes + HeadroomBytes)
        {
            return Failed(
                ArchiveFailure.NotEnoughRoom,
                $"Packing this acquisition needs about {expectedBytes / 1024 / 1024:N0} MB plus "
                + $"headroom, and {Path.GetPathRoot(archivePath)} has "
                + $"{free / 1024 / 1024:N0} MB free.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);

        try
        {
            await PackAsync(folder, archivePath, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Discard(archivePath);
            throw;
        }
        catch (FileNotFoundException ex)
        {
            // A file vanished between the walk and the read. The acquisition is moving under us,
            // so this archive would be wrong even if it completed.
            Discard(archivePath);
            return Failed(ArchiveFailure.ChangedWhileReading, ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            Discard(archivePath);
            return Failed(ArchiveFailure.ChangedWhileReading, ex.Message);
        }
        catch (IOException ex)
        {
            Discard(archivePath);
            return Failed(ArchiveFailure.WriteFailed, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            Discard(archivePath);
            return Failed(ArchiveFailure.WriteFailed, ex.Message);
        }

        return new ArchiveResult(
            archivePath, new FileInfo(archivePath).Length, ArchiveFailure.None, null);
    }

    /// <summary>Removes an archive that is no longer wanted.</summary>
    /// <remarks>
    /// Called after a successful upload and after a failed build. A stale multi-gigabyte
    /// temporary file on an instrument computer is its own kind of failure.
    /// </remarks>
    public static void Discard(string? archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return;
        }

        try
        {
            File.Delete(archivePath);
        }
        catch (IOException)
        {
            // Nothing useful to do. It is a temporary file.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task PackAsync(
        string folder,
        string archivePath,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        var root = Path.GetFullPath(folder);
        long read = 0;

        // One buffer for the whole acquisition, not one per file. A megabyte lands on the large
        // object heap, and a .d holds thousands of small files: allocating inside the loop made
        // packing a folder a few thousand LOH allocations and the collections that follow, on the
        // machine attached to the mass spectrometer.
        var buffer = new byte[1 << 20];

        // The stream is created here rather than by ZipFile so the archive can be removed
        // reliably if anything goes wrong part-way.
        await using var output = new FileStream(
            archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        foreach (var file in Directory.EnumerateFiles(root, "*", options).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Paths inside the archive are relative to the acquisition and always use forward
            // slashes, which is what the zip format specifies and what every reader expects.
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

            var entry = zip.CreateEntry(relative, CompressionLevel.NoCompression);
            entry.LastWriteTime = File.GetLastWriteTime(file);

            await using var source = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);

            await using var target = entry.Open();

            int taken;

            while ((taken = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, taken), cancellationToken)
                    .ConfigureAwait(false);

                read += taken;
                progress?.Report(read);
            }
        }
    }

    /// <summary>Free space on the volume the archive would be written to, or null if unknown.</summary>
    private static long? RoomFor(string archivePath)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(archivePath));

            return string.IsNullOrEmpty(root)
                ? null
                : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            // A path whose volume cannot be interrogated -- a share, for one. Not knowing is not
            // a reason to refuse; the write will say so if there is no room.
            return null;
        }
    }

    private static ArchiveResult Failed(ArchiveFailure failure, string detail) =>
        new(null, 0, failure, detail);
}
