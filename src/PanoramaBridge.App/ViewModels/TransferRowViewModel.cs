using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;

namespace PanoramaBridge.App.ViewModels;

/// <summary>
/// The block of the transfer grid a row sits in. Declaration order is display order.
/// </summary>
public enum TransferBand
{
    /// <summary>Bytes are moving, or the server is being asked to confirm them.</summary>
    Active = 0,

    /// <summary>Somebody has to do something about it.</summary>
    NeedsAttention = 1,

    /// <summary>Done with, one way or the other.</summary>
    Finished = 2,

    /// <summary>Found, but not safe to read or not started yet.</summary>
    Waiting = 3,
}

/// <summary>
/// One row of the transfer grid.
/// </summary>
/// <remarks>
/// Mutated in place rather than replaced, so the grid updates a cell instead of rebuilding a
/// row -- which matters because a row is recycled by virtualization and replacing it resets
/// selection and scroll position under the user's hands.
/// </remarks>
public sealed partial class TransferRowViewModel : ObservableObject
{
    public TransferRowViewModel(TransferProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        LocalPath = progress.LocalPath;
        FileName = progress.FileName;
        Apply(progress);
    }

    /// <summary>Identity of the row. Never changes.</summary>
    public string LocalPath { get; }

    /// <summary>File name alone, for the narrow first column.</summary>
    public string FileName { get; }

    [ObservableProperty]
    private string _remotePath = string.Empty;

    [ObservableProperty]
    private TransferState _state;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private double _percent;

    /// <summary>False for rows that have no meaningful bar, so it can be hidden rather than shown empty.</summary>
    [ObservableProperty]
    private bool _hasProgress;

    [ObservableProperty]
    private string _speed = string.Empty;

    [ObservableProperty]
    private string _eta = string.Empty;

    [ObservableProperty]
    private string _verification = string.Empty;

    /// <summary>True when a person has to do something about this row.</summary>
    [ObservableProperty]
    private bool _needsAttention;

    /// <summary>Bytes, kept so the grid can sort and the summary can total.</summary>
    [ObservableProperty]
    private long _totalBytes;

    /// <summary>Which block of the grid this row belongs in. Lower sorts higher.</summary>
    public TransferBand Band => BandFor(State);

    /// <summary>
    /// Groups a state into the block of the grid it belongs in.
    /// </summary>
    /// <remarks>
    /// The order follows what a person watching a transfer is actually looking for: what is
    /// moving now, then anything that went wrong, then what has finished, then what has not
    /// started. Files therefore travel down the grid as they progress -- a row moves from the
    /// top block to the middle one at the moment it is verified -- rather than staying wherever
    /// it happened to be inserted.
    /// </remarks>
    public static TransferBand BandFor(TransferState state) => state switch
    {
        // Queued belongs here rather than with the waiting files: the engine has accepted it and
        // is working on it, which includes the checks that run before any bytes move. Waiting is
        // reserved for files that are not safe to read yet.
        TransferState.Queued or TransferState.Uploading or TransferState.Uploaded =>
            TransferBand.Active,

        // Kept near the top rather than buried under a session's worth of finished rows: these
        // are the only rows that need a person to do something.
        TransferState.Failed or TransferState.Conflict or TransferState.Superseded =>
            TransferBand.NeedsAttention,

        TransferState.Verified or TransferState.Skipped => TransferBand.Finished,

        _ => TransferBand.Waiting,
    };

    /// <summary>Updates this row from a newer report.</summary>
    public void Apply(TransferProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        RemotePath = progress.RemotePath;
        State = progress.State;

        // The phase is what the engine is doing at this instant; the state is only where the
        // file stands. Preferring the phase is what stops a row reading "Queued" while the
        // server is spending half a minute hashing a folder on its behalf.
        Status = string.IsNullOrWhiteSpace(progress.Phase)
            ? Describe(progress.State)
            : progress.Phase;
        Detail = progress.Message ?? string.Empty;
        TotalBytes = progress.TotalBytes;

        HasProgress = progress.State is TransferState.Uploading && progress.Fraction is not null;
        Percent = (progress.Fraction ?? (IsFinished(progress.State) ? 1 : 0)) * 100;

        Speed = progress.State == TransferState.Uploading && progress.BytesPerSecond > 0
            ? FormatRate(progress.BytesPerSecond)
            : string.Empty;

        Eta = progress.State == TransferState.Uploading && progress.Eta is { } eta
            ? FormatEta(eta)
            : string.Empty;

        // Only claim a verification standing once there is something to claim.
        Verification = progress.State is TransferState.Verified or TransferState.Uploaded
            or TransferState.Skipped
            ? progress.DescribeVerification()
            : string.Empty;

        NeedsAttention = progress.State
            is TransferState.Failed or TransferState.Conflict or TransferState.Superseded;
    }

    private static bool IsFinished(TransferState state) =>
        state is TransferState.Verified or TransferState.Skipped or TransferState.Uploaded;

    /// <summary>The state in the words a user would use, not the enum name.</summary>
    private static string Describe(TransferState state) => state switch
    {
        TransferState.Discovered => "Waiting",
        TransferState.Queued => "Queued",
        TransferState.Uploading => "Uploading",
        TransferState.Uploaded => "Verifying",
        TransferState.Verified => "Verified",
        TransferState.Skipped => "Already there",
        TransferState.Conflict => "Needs a decision",
        TransferState.LockedRetrying => "File in use",
        TransferState.Superseded => "Changed",
        TransferState.Failed => "Failed",
        _ => state.ToString(),
    };

    private static string FormatRate(double bytesPerSecond)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var unit = 0;

        while (bytesPerSecond >= 1024 && unit < units.Length - 1)
        {
            bytesPerSecond /= 1024;
            unit++;
        }

        return $"{bytesPerSecond:F1} {units[unit]}/s";
    }

    private static string FormatEta(TimeSpan eta) => eta.TotalHours >= 1
        ? $"{(int)eta.TotalHours}h {eta.Minutes}m"
        : eta.TotalMinutes >= 1
            ? $"{(int)eta.TotalMinutes}m {eta.Seconds}s"
            : $"{Math.Max(1, (int)eta.TotalSeconds)}s";
}
