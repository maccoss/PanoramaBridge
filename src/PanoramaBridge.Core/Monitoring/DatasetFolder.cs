using System.Globalization;

namespace PanoramaBridge.Core.Monitoring;

/// <summary>
/// What a directory acquisition looked like at a moment in time.
/// </summary>
/// <param name="TotalBytes">Sum of every file inside, at any depth.</param>
/// <param name="FileCount">How many files, at any depth.</param>
/// <param name="NewestWriteUnixMs">The most recent modification time inside it.</param>
/// <remarks>
/// Three numbers rather than one, because a folder can change in ways a single one would miss.
/// Bruker writes several files into a <c>.d</c> and finishes them at different moments: the total
/// can hold still while a file is added, and a file can be rewritten in place without the count
/// moving. All three have to settle together before the folder has.
/// </remarks>
public readonly record struct DatasetStamp(long TotalBytes, int FileCount, long NewestWriteUnixMs)
{
    /// <summary>Nothing there.</summary>
    public static DatasetStamp Empty { get; }

    /// <summary>True when the folder holds no files at all, at any depth.</summary>
    public bool IsEmpty => FileCount == 0;

    /// <summary>The newest modification time inside, as a moment.</summary>
    public DateTimeOffset NewestWriteUtc =>
        DateTimeOffset.FromUnixTimeMilliseconds(NewestWriteUnixMs);

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{FileCount} file(s), {TotalBytes:N0} bytes");
}

/// <summary>
/// Recognises and measures the directory acquisitions some instruments write.
/// </summary>
/// <remarks>
/// <para>
/// Bruker writes a <c>.d</c> directory and Waters a <c>.raw</c> directory. Neither is a file, so
/// neither can travel through the ordinary path: the question is not whether one file matches a
/// filter but whether a whole folder has finished being written.
/// </para>
/// <para>
/// These reach Panorama as a single <c>.d.zip</c>, which is how this lab already stores them and
/// what the tooling downstream expects. That is a considerably better shape than uploading the
/// files inside one by one and hoping the set arrives complete: one object either verifies
/// against the server's checksum or it does not, so atomicity comes for free rather than being
/// something to engineer.
/// </para>
/// </remarks>
public static class DatasetFolder
{
    /// <summary>
    /// Whether a path is a directory acquisition the user has asked to transfer.
    /// </summary>
    /// <remarks>
    /// Governed by the same extension list as files, so a user who types <c>.d</c> gets Bruker
    /// folders without a second setting to find. The directory test is what separates a Waters
    /// <c>.raw</c> folder from a Thermo <c>.raw</c> file, which share an extension and are
    /// nothing alike.
    /// </remarks>
    public static bool Is(string path, CandidateFilter filter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(filter);

        return Directory.Exists(path) && filter.Accepts(path);
    }

    /// <summary>The name the acquisition takes on the server.</summary>
    /// <remarks>
    /// <c>run.d</c> becomes <c>run.d.zip</c>: the folder's own name is kept whole rather than
    /// having its extension replaced, so the vendor and the original name both survive, and it
    /// matches what is already on Panorama.
    /// </remarks>
    public static string ArchiveNameFor(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            + ".zip";
    }

    /// <summary>
    /// Measures a folder, or null when it is not there.
    /// </summary>
    /// <remarks>
    /// One walk, reading size and time from what the enumeration already returned, so the cost is
    /// one pass over the directory entries rather than a stat per file. Unreadable subfolders are
    /// skipped rather than fatal, exactly as the sweep does: on a shared instrument volume there
    /// is usually at least one.
    /// </remarks>
    public static DatasetStamp? Measure(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!Directory.Exists(path))
        {
            return null;
        }

        long total = 0;
        var count = 0;
        long newest = 0;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        try
        {
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                total += file.Length;
                count++;

                var written = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                if (written > newest)
                {
                    newest = written;
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Removed while being measured. Not there is a real answer.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return new DatasetStamp(total, count, newest);
    }

    /// <summary>
    /// Whether anything still holds a file inside the folder open for writing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The folder equivalent of the exclusive-open probe used on a single file, and needed for
    /// the same reason: an instrument leaves its output readable while it is still writing, so a
    /// plain read proves nothing.
    /// </para>
    /// <para>
    /// Shared as <see cref="FileShare.Read"/>, not <see cref="FileShare.None"/>. Both fail while
    /// another handle holds a file for writing, which is the question; None would additionally
    /// lock out every other reader of an instrument's own data for as long as the walk takes,
    /// which is not something to do on that machine.
    /// </para>
    /// </remarks>
    public static bool IsAnythingWriting(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", options))
            {
                try
                {
                    using var probe = new FileStream(
                        file, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                catch (IOException)
                {
                    // Held for writing by something. That is the answer.
                    return true;
                }
                catch (UnauthorizedAccessException)
                {
                    // Permissions, not a writer. Not evidence either way, so it does not hold
                    // the folder back -- the stability clock still has to be satisfied.
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Gone. Nothing is writing to a folder that is not there, and Measure will report it
            // missing on the next pass.
            return false;
        }
        catch (IOException)
        {
            // The walk itself failed -- an SMB share blinking is the case that matters, and
            // monitoring one is supported. Two things were wrong with letting this out.
            //
            // It escaped: DatasetStabilityTracker.Check has no handler, and ReadinessGate
            // catches only OperationCanceledException, so one folder hiccuping stopped
            // monitoring for every watched path. Measure, directly above, has caught IOException
            // and UnauthorizedAccessException all along; this did not, and the asymmetry was the
            // whole bug.
            //
            // And the answer is true rather than false. The question is "may anything still be
            // writing in here", so failing to complete the walk is not evidence that nothing is.
            // False would let an unprovable folder through on a satisfied quiet clock, which is
            // the one outcome this application must never produce. True holds it back; the
            // tracker reports Locked, the gate keeps retrying, and a share that stays broken is
            // eventually handed back to the periodic check by the locked-file policy.
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Enumeration refused outright, which is not the same as the per-file case handled
            // in the loop: there, one unreadable file among many is not evidence either way,
            // while here nothing was examined at all. Unprovable, so held back.
            return true;
        }

        return false;
    }
}
