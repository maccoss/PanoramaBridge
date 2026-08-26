using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PanoramaBridge.Core.Storage;

namespace PanoramaBridge.App.ViewModels;

/// <summary>One row of the upload ledger, as shown in the audit view.</summary>
public sealed class UploadRowViewModel
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
        // Told apart here too, not only in the live progress. This tab reads the ledger, so it
        // is the view that still has the row after a restart -- and sending somebody from here to
        // the conflict setting for a damaged file points them at a control that cannot move it.
        TransferState.Conflict => Record.ConflictKind == ConflictKind.LocalFileDamaged
            ? "Held - damaged"
            : "Held",
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

    public UploadsViewModel(IStateStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Rows currently shown.</summary>
    public ObservableCollection<UploadRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    [ObservableProperty]
    private string _summary = "Nothing recorded yet.";

    [ObservableProperty]
    private UploadFilter _filter = UploadFilter.All;

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

            await UpdateSummaryAsync().ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
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
