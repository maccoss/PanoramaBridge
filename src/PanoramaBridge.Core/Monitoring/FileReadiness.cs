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
}

/// <summary>The outcome of examining one file.</summary>
/// <param name="Reason">Why it is or is not ready.</param>
/// <param name="Length">Size in bytes at the moment of the check, or zero when unknown.</param>
/// <param name="Detail">A sentence fit to show the user.</param>
public readonly record struct FileReadiness(ReadinessReason Reason, long Length, string Detail)
{
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
        new(ReadinessReason.Missing, 0, $"'{Path.GetFileName(path)}' is no longer there.");

    public static FileReadiness Locked(long length, string path) =>
        new(
            ReadinessReason.Locked,
            length,
            $"'{Path.GetFileName(path)}' is open in another program. This is normal while an "
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
            $"'{Path.GetFileName(path)}' has been open in another program for all of the last "
            + $"{attempts} checks. It will be picked up at the next folder check, or as soon as "
            + "it changes.");

    public static FileReadiness Unreadable(string path, string message) =>
        new(ReadinessReason.Unreadable, 0, $"Cannot read '{Path.GetFileName(path)}': {message}");
}
