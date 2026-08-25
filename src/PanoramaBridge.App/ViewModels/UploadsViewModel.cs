using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;
using PanoramaBridge.Core.WebDav;

namespace PanoramaBridge.App.ViewModels;

/// <summary>One row of the upload ledger, as shown in the audit view.</summary>
/// <remarks>
/// Observable only because a row can now be picked for a bulk decision. Everything else about it
/// is fixed at construction: the ledger is reloaded to reflect a change rather than edited in
/// place, so a row never has to tell the view that a value it already showed was wrong.
/// </remarks>
public sealed partial class UploadRowViewModel : ObservableObject
{
    public UploadRowViewModel(UploadRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        Record = record;
        FileName = Path.GetFileName(record.LocalPath);
    }

    public UploadRecord Record { get; }

    public string FileName { get; }

    public string LocalPath => Record.LocalPath;

    public string RemotePath => Record.RemotePath;

    public string State => Record.State switch
    {
        TransferState.Verified => "Verified",
        TransferState.Skipped => "Already there",
        TransferState.Uploaded => "Uploaded",
        TransferState.Failed => "Failed",
        TransferState.Conflict => "Needs a decision",
        TransferState.Superseded => "Changed",
        TransferState.LockedRetrying => "File in use",
        _ => Record.State.ToString(),
    };

    /// <summary>
    /// How the remote copy was checked, stated so it never implies more than was proven.
    /// </summary>
    public string Verification => Record.VerifyMethod switch
    {
        VerifyMethod.ServerMd5 => "Server MD5",
        VerifyMethod.SizeOnly => "Size only",
        _ => "Not verified",
    };

    /// <summary>
    /// What reading the file itself established, for the formats that can be asked.
    /// </summary>
    /// <remarks>
    /// Blank for anything not checked, which is most things. The values worth looking for are the
    /// unchecked ones: they name a gap in the checker, and a gap nobody can see does not get
    /// closed.
    /// </remarks>
    public string RawCheck => Record.RawCheck ?? string.Empty;

    /// <summary>True when the file was examined and nothing could be established.</summary>
    public bool RawCheckInconclusive =>
        Record.RawCheck?.StartsWith("Unchecked", StringComparison.Ordinal) == true;

    /// <summary>True when the row can be relied on: hash-checked and unchanged since.</summary>
    public bool IsTrustworthy => Record.VerifyMethod == VerifyMethod.ServerMd5;

    public string Size => FormatBytes(Record.Length);

    public string VerifiedAt => Record.VerifiedUtc?.ToLocalTime()
        .ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? string.Empty;

    public string Detail => Record.LastError ?? string.Empty;

    public bool NeedsAttention =>
        Record.State is TransferState.Failed or TransferState.Conflict or TransferState.Superseded;

    /// <summary>True when this row is one a person can actually decide about.</summary>
    /// <remarks>
    /// Only a conflict. A failed upload needs retrying rather than deciding, and a superseded one
    /// resolves itself on the next sweep -- offering the same three buttons for all of them would
    /// imply choices that do not apply and, for Overwrite, one that is actively wrong.
    /// </remarks>
    public bool CanResolve => Record.State == TransferState.Conflict;

    /// <summary>
    /// True when the conflict is that the local file is damaged rather than that the destination
    /// is occupied.
    /// </summary>
    /// <remarks>
    /// A proven-truncated acquisition is held in the same state as a destination clash, and the
    /// two want opposite things. Offering Overwrite here would let somebody push a short file
    /// over a good remote copy -- the exact outcome the truncation check exists to prevent -- so
    /// the view offers only Keep, and says why.
    /// </remarks>
    public bool IsLocalFileProblem =>
        Record.State == TransferState.Conflict
        && Record.RawCheck is { Length: > 0 }
        && Record.LastError is { Length: > 0 }
        && string.Equals(Record.RawCheck, Record.LastError, StringComparison.Ordinal);

    /// <summary>Whether this row is picked for a bulk decision.</summary>
    [ObservableProperty]
    private bool _isSelected;

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:F1} {units[unit]}";
    }
}

/// <summary>Which rows the audit view is showing.</summary>
public enum UploadFilter
{
    All,
    Verified,
    NeedsAttention,

    /// <summary>Files the content check could not reach a conclusion about.</summary>
    /// <remarks>
    /// Its own filter because these are a work list rather than a problem: each one is a format
    /// revision or a layout the checker does not yet understand, and finding them should be a
    /// click rather than a search through logs.
    /// </remarks>
    NotChecked,
}

/// <summary>
/// The Uploads tab: the durable answer to "did that actually get uploaded?".
/// </summary>
/// <remarks>
/// Reads the ledger rather than the in-memory transfer list, so it still answers the question
/// after a restart, next week, or on a machine that has been rebuilt. The CSV export exists
/// because in a lab the real requirement is not to see that a file transferred but to be able to
/// show that it did.
/// </remarks>
public sealed partial class UploadsViewModel : ObservableObject
{
    private static readonly TransferState[] AllStates = Enum.GetValues<TransferState>();

    private static readonly TransferState[] AttentionStates =
    [
        TransferState.Failed,
        TransferState.Conflict,
        TransferState.Superseded,
    ];

    private readonly IStateStore _store;

    /// <summary>
    /// How to reach the server, when there is one. Null while disconnected.
    /// </summary>
    /// <remarks>
    /// A delegate rather than the transfer service itself, so the audit tab stays a reader of the
    /// ledger and gains exactly one capability: listing a folder to find a free name. It is
    /// resolved per call because the client comes and goes with the connection, and a captured
    /// one would be stale the first time somebody edits the server settings.
    /// </remarks>
    private readonly Func<IWebDavClient?> _client;

    public UploadsViewModel(IStateStore store, Func<IWebDavClient?>? client = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _client = client ?? (static () => null);
    }

    /// <summary>Rows picked for a decision, or every conflict when nothing is picked.</summary>
    /// <remarks>
    /// Selecting nothing and pressing a button means "all of them", which is what somebody
    /// looking at a filtered list of conflicts is asking for. Picking rows narrows it.
    /// </remarks>
    private IReadOnlyList<UploadRowViewModel> Targets()
    {
        var picked = Rows.Where(r => r.IsSelected && r.CanResolve).ToArray();

        return picked.Length > 0 ? picked : Rows.Where(r => r.CanResolve).ToArray();
    }

    /// <summary>True when there is anything here to decide about.</summary>
    public bool HasConflicts => Rows.Any(r => r.CanResolve);

    /// <summary>Rows currently shown.</summary>
    public ObservableCollection<UploadRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    [ObservableProperty]
    private string _summary = "Nothing recorded yet.";

    [ObservableProperty]
    private UploadFilter _filter = UploadFilter.All;

    /// <summary>Why a rename could not be worked out, for the view to show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRenameProblem))]
    private string _renameProblem = string.Empty;

    /// <summary>True when there is a rename problem worth a line of red text.</summary>
    public bool HasRenameProblem => RenameProblem.Length > 0;

    /// <summary>Search text applied to the file name.</summary>
    [ObservableProperty]
    private string _search = string.Empty;

    /// <summary>True when there is nothing to show, so the view can say so rather than sit blank.</summary>
    public bool IsEmpty => !IsLoading && Rows.Count == 0;

    partial void OnFilterChanged(UploadFilter value) => _ = RefreshAsync();

    partial void OnSearchChanged(string value) => _ = RefreshAsync();

    /// <summary>Reloads from the ledger.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var states = Filter switch
            {
                UploadFilter.Verified => [TransferState.Verified, TransferState.Skipped],
                UploadFilter.NeedsAttention => AttentionStates,
                UploadFilter.NotChecked => AllStates,
                _ => AllStates,
            };

            var records = await _store.GetByStateAsync(states, limit: 5000).ConfigureAwait(true);

            if (Filter == UploadFilter.NotChecked)
            {
                // Filtered here rather than in SQL because it is a property of the recorded text
                // and not of the state, and because this list is short by definition: if it is
                // long, the checker has a gap worth closing rather than paging through.
                records = records
                    .Where(r => r.RawCheck?.StartsWith("Unchecked", StringComparison.Ordinal) == true)
                    .ToArray();
            }

            var needle = Search.Trim();
            if (needle.Length > 0)
            {
                records = records
                    .Where(r => Path.GetFileName(r.LocalPath)
                        .Contains(needle, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }

            Rows.Clear();
            foreach (var record in records)
            {
                Rows.Add(new UploadRowViewModel(record));
            }

            OnPropertyChanged(nameof(HasConflicts));

            await UpdateSummaryAsync().ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>Replaces the remote copy with the local one.</summary>
    [RelayCommand]
    private async Task ResolveOverwriteAsync()
    {
        // Deliberately not offered for a damaged local file: that would push a short acquisition
        // over a good remote copy.
        foreach (var row in Targets().Where(r => !r.IsLocalFileProblem))
        {
            await _store
                .ResolveConflictAsync(row.Record.LocalPath, ConflictResolution.Overwrite)
                .ConfigureAwait(true);
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>Keeps what is on the server and stops offering the local file.</summary>
    [RelayCommand]
    private async Task ResolveKeepAsync()
    {
        foreach (var row in Targets())
        {
            await _store
                .ResolveConflictAsync(row.Record.LocalPath, ConflictResolution.Keep)
                .ConfigureAwait(true);
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Sends the local file alongside the remote one, under the first free name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Names are worked out here, before anything is written, so the list of them can be shown
    /// and agreed to rather than discovered afterwards. One listing per destination folder covers
    /// a whole batch, and names handed out within the batch are remembered, so five hundred files
    /// landing in one folder do not all propose the same name.
    /// </para>
    /// <para>
    /// The engine checks the name again before it sends anything. This proposal can be minutes or
    /// a reboot old by then, and being merely probably-free is not good enough when the cost of
    /// being wrong is somebody else's data.
    /// </para>
    /// </remarks>
    [RelayCommand]
    private async Task ResolveRenameAsync()
    {
        var client = _client();

        if (client is null)
        {
            RenameProblem =
                "A free name has to be checked against the server, and there is no connection at "
                + "the moment. Test the connection on the Server tab, then try again.";
            return;
        }

        RenameProblem = string.Empty;

        var taken = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var proposals = new List<(UploadRowViewModel Row, string Name)>();

        foreach (var row in Targets().Where(r => !r.IsLocalFileProblem))
        {
            RemotePath path;

            try
            {
                path = RemotePath.Parse(row.Record.RemotePath);
            }
            catch (ArgumentException)
            {
                continue;
            }

            var folder = path.Parent;
            var key = folder.ToEncodedString();

            if (!taken.TryGetValue(key, out var names))
            {
                try
                {
                    var listing = await client.ListAsync(folder).ConfigureAwait(true);
                    names = listing.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                catch (WebDavException ex)
                {
                    RenameProblem = ex.ToUserMessage();
                    return;
                }

                taken[key] = names;
            }

            var proposed = ConflictNames.NextFree(path.Name, names);

            // Remembered, so the next file in this batch does not propose the same name.
            names.Add(proposed);
            proposals.Add((row, proposed));
        }

        foreach (var (row, name) in proposals)
        {
            await _store
                .ResolveConflictAsync(row.Record.LocalPath, ConflictResolution.Rename, name)
                .ConfigureAwait(true);
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Writes the current rows to a CSV file.
    /// </summary>
    /// <remarks>
    /// Exports what is on screen, filters included, because the usual reason to export is to
    /// answer a specific question rather than to dump everything.
    /// </remarks>
    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export the upload record",
            FileName = $"panoramabridge-uploads-{DateTime.Now:yyyy-MM-dd}.csv",
            Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var csv = new StringBuilder();
        csv.AppendLine("Local path,Remote path,State,Verification,File check,Size (bytes),MD5,Verified (UTC),Detail");

        foreach (var row in Rows)
        {
            var record = row.Record;

            csv.Append(Escape(record.LocalPath)).Append(',')
                .Append(Escape(record.RemotePath)).Append(',')
                .Append(Escape(row.State)).Append(',')
                .Append(Escape(row.Verification)).Append(',')
                .Append(Escape(row.RawCheck)).Append(',')
                .Append(record.Length.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Escape(record.Md5 ?? string.Empty)).Append(',')
                .Append(Escape(record.VerifiedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty))
                .Append(',')
                .Append(Escape(record.LastError ?? string.Empty))
                .AppendLine();
        }

        await File.WriteAllTextAsync(dialog.FileName, csv.ToString(), Encoding.UTF8)
            .ConfigureAwait(true);
    }

    private async Task UpdateSummaryAsync()
    {
        var counts = await _store.CountByStateAsync().ConfigureAwait(true);

        if (counts.Count == 0)
        {
            Summary = "Nothing recorded yet.";
            return;
        }

        var verified = counts.GetValueOrDefault(TransferState.Verified);
        var skipped = counts.GetValueOrDefault(TransferState.Skipped);
        var attention = AttentionStates.Sum(counts.GetValueOrDefault);

        var summary = $"{verified + skipped:N0} on the server";

        if (verified > 0)
        {
            summary += $" ({verified:N0} hash-verified)";
        }

        if (attention > 0)
        {
            summary += $", {attention:N0} need attention";
        }

        Summary = summary + $" - showing {Rows.Count:N0}";
    }

    /// <summary>Quotes a CSV field. Paths routinely contain commas and occasionally quotes.</summary>
    private static string Escape(string value) =>
        value.Contains(',', StringComparison.Ordinal)
        || value.Contains('"', StringComparison.Ordinal)
        || value.Contains('\n', StringComparison.Ordinal)
            ? '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"'
            : value;
}
