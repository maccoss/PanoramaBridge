namespace PanoramaBridge.Core.Monitoring;

/// <summary>Why a file is or is not ready to be uploaded.</summary>
public enum ReadinessReason
{
    /// <summary>Nothing else holds the file and it has stopped changing.</summary>
    Ready = 0,

    /// <summary>The file is not there. It may have been moved or deleted mid-copy.</summary>
    Missing = 1,

    /// <summary>Another process holds a handle to it -- an instrument, or a copy in progress.</summary>
    Locked = 2,

    /// <summary>Its size changed since the last look.</summary>
    Growing = 3,

    /// <summary>It has stopped changing but has not been quiet for long enough yet.</summary>
    Settling = 4,

    /// <summary>It could not be examined, for example because of permissions.</summary>
    Unreadable = 5,

    /// <summary>
    /// A directory acquisition that is there but holds no files, so there is nothing to send.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Missing"/>, which means the path is not there at all. An empty
    /// folder exists, and packing it would produce a perfectly valid archive of nothing. Like
    /// Missing it is not worth retrying: the sweep declines to offer an empty acquisition, so if
    /// one is ever written into, that is what brings it back rather than this watch continuing.
    /// </remarks>
    Empty = 6,
}

/// <summary>The outcome of examining one file.</summary>
/// <param name="Reason">Why it is or is not ready.</param>
/// <param name="Length">Size in bytes at the moment of the check, or zero when unknown.</param>
/// <param name="Detail">A sentence fit to show the user.</param>
public readonly record struct FileReadiness(ReadinessReason Reason, long Length, string Detail)
{
    /// <summary>The name to show for a path, which may be a directory acquisition.</summary>
    /// <remarks>
    /// <see cref="Path.GetFileName(string)"/> returns an empty string for a path ending in a
    /// separator, so a folder written as <c>D:\runs\250314.d\</c> was reported to the user as
    /// "'' is no longer there." <see cref="DatasetFolder.ArchiveNameFor"/> trims for exactly this
    /// reason and has a test for it; these messages did not, and they are the ones a scientist
    /// actually reads.
    /// </remarks>
    private static string DisplayName(string path) =>
        Path.GetFileName(
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>True only when the file can safely be read from start to finish.</summary>
    public bool IsReady => Reason == ReadinessReason.Ready;

    /// <summary>
    /// True when waiting longer might change the answer. A missing or unreadable file will not
    /// improve by being asked again on the next tick.
    /// </summary>
    public bool IsWorthRetrying =>
        Reason is ReadinessReason.Locked or ReadinessReason.Growing or ReadinessReason.Settling;

    public static FileReadiness Ready(long length) =>
        new(ReadinessReason.Ready, length, "Ready to upload.");

    public static FileReadiness Missing(string path) =>
        new(ReadinessReason.Missing, 0, $"'{DisplayName(path)}' is no longer there.");

    public static FileReadiness Locked(long length, string path) =>
        new(
            ReadinessReason.Locked,
            length,
            $"'{DisplayName(path)}' is open in another program. This is normal while an "
            + "instrument is acquiring or a copy is still running.");

    public static FileReadiness Growing(long from, long to) =>
        new(
            ReadinessReason.Growing,
            to,
            $"Still being written ({from:N0} to {to:N0} bytes since the last check).");

    public static FileReadiness Settling(long length, TimeSpan quietFor, TimeSpan required) =>
        new(
            ReadinessReason.Settling,
            length,
            $"Unchanged for {quietFor.TotalSeconds:F0}s; waiting for {required.TotalSeconds:F0}s.");

    /// <summary>
    /// Held open by something else for so long that close watching has been given up.
    /// </summary>
    /// <remarks>
    /// Still <see cref="ReadinessReason.Locked"/>, because that is what is true about the file.
    /// The message is what differs: it has to tell someone watching for their data that it has
    /// not been forgotten, only stopped being asked about so often.
    /// </remarks>
    public static FileReadiness StillInUse(long length, string path, int attempts) =>
        new(
            ReadinessReason.Locked,
            length,
            $"'{DisplayName(path)}' has been open in another program for all of the last "
            + $"{attempts} checks. It will be picked up at the next folder check, or as soon as "
            + "it changes.");

    /// <summary>A directory acquisition that changed since the last look.</summary>
    /// <remarks>
    /// Deliberately not <see cref="Growing(long, long)"/>, which describes a change in bytes
    /// alone. A folder is measured by three numbers precisely because any one can hold still
    /// while another moves: Bruker closes the files in a <c>.d</c> at different moments, so a
    /// total stays put while a file is added, and a file can be rewritten in place without the
    /// count moving. Reporting the bytes alone printed "1,048,576 to 1,048,576 bytes since the
    /// last check" -- naming the one number that did not change and hiding the two that did,
    /// which is the opposite of what someone watching a stalled transfer needs.
    /// </remarks>
    public static FileReadiness DatasetGrowing(DatasetStamp from, DatasetStamp to)
    {
        var changed = new List<string>(3);

        if (from.TotalBytes != to.TotalBytes)
        {
            changed.Add($"{from.TotalBytes:N0} to {to.TotalBytes:N0} bytes");
        }

        if (from.FileCount != to.FileCount)
        {
            changed.Add($"{from.FileCount:N0} to {to.FileCount:N0} files");
        }

        if (from.NewestWriteUnixMs != to.NewestWriteUnixMs)
        {
            changed.Add("something inside was written to");
        }

        return new(
            ReadinessReason.Growing,
            to.TotalBytes,
            changed.Count == 0
                ? "Still being written."
                : $"Still being written ({string.Join("; ", changed)} since the last check).");
    }

    /// <summary>A directory acquisition that is there but holds nothing.</summary>
    public static FileReadiness Empty(string path) =>
        new(
            ReadinessReason.Empty,
            0,
            $"'{DisplayName(path)}' has no files in it, so there is nothing to transfer. It will "
            + "be offered again if an acquisition is written into it.");

    public static FileReadiness Unreadable(string path, string message) =>
        new(ReadinessReason.Unreadable, 0, $"Cannot read '{DisplayName(path)}': {message}");
}
