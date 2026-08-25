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
        && Record.ConflictKind == ConflictKind.LocalFileDamaged;

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

    /// <summary>
    /// Which files are picked, by path, independently of what is on screen.
    /// </summary>
    /// <remarks>
    /// Not a flag on the rows. Rows are rebuilt by every refresh -- which every resolve command
    /// and every keystroke in the search box causes -- and a row filtered out of view has no flag
    /// to carry anything. Since no ticks means "every held file", a tick lost while the user
    /// narrowed the list would silently widen the next decision from one file to all of them.
    /// </remarks>
    private readonly HashSet<string> _picked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tells the rest of the application that a held file is no longer held.
    /// </summary>
    /// <remarks>
    /// The decision is written straight to the ledger, so nothing else hears about it: the
    /// progress aggregator went on counting the file under "needs attention" for the life of the
    /// process. Clear five hundred conflicts and the status bar still asked about five hundred,
    /// disagreeing with the tab the user had just used to settle them.
    /// </remarks>
    private readonly Action<TransferProgress>? _announce;

    /// <summary>Drops a file from the progress view, for one about to be sent afresh.</summary>
    private readonly Action<string>? _forget;

    public UploadsViewModel(
        IStateStore store,
        Func<IWebDavClient?>? client = null,
        Action<TransferProgress>? announce = null,
        Action<string>? forget = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _client = client ?? (static () => null);
        _announce = announce;
        _forget = forget;
    }

    /// <summary>Reports a resolved file, so the status bar stops asking about it.</summary>
    private void Announce(UploadRecord record, TransferState state, string phase) =>
        _announce?.Invoke(new TransferProgress(
            record.LocalPath,
            record.RemotePath,
            state,
            phase,
            0,
            record.Length));

    /// <summary>
    /// The records a decision applies to: those picked, or every held file when none is picked.
    /// </summary>
    /// <remarks>
    /// When nothing is ticked this asks the ledger rather than reading the rows on screen. The
    /// list is capped at five thousand and narrowed by the filter and the search box, so "all of
    /// them" read from the screen would have meant "all of the ones you happen to be looking at"
    /// -- while the button said otherwise. On an instrument with a long history the conflicts
    /// outside that window are exactly the old ones somebody is trying to clear.
    /// </remarks>
    private async Task<IReadOnlyList<UploadRecord>> TargetsAsync()
    {
        // Picked files are fetched by name rather than by pulling every conflict on the machine
        // into memory to filter it down. On the long-history instrument this whole method exists
        // for, that read was the largest allocation the tab made, once per button press.
        if (_picked.Count > 0)
        {
            var byPath = await _store
                .GetManyAsync([.. _picked])
                .ConfigureAwait(true);

            var still = byPath.Values
                .Where(r => r.State == TransferState.Conflict)
                .ToArray();

            // A pick only means anything while its file is still held. Nothing pruned these when
            // a file was settled, and the tick box is disabled the moment a row stops being
            // resolvable, so it could not be cleared by hand either -- one settled pick made
            // every later decision target "the held files that are picked", of which there were
            // none, and the buttons went quietly dead for the rest of the session.
            _picked.IntersectWith(still.Select(r => r.LocalPath));

            if (still.Length > 0)
            {
                return still;
            }
        }

        // Nothing picked, or nothing picked is still held: all of them.
        return await _store
            .GetByStateAsync([TransferState.Conflict], limit: int.MaxValue)
            .ConfigureAwait(true);
    }

    /// <summary>True when there is anything at all to decide about.</summary>
    /// <remarks>
    /// Counted from the ledger, not from the rows. The rows are capped and narrowed by the filter
    /// and the search box, so on a machine whose conflicts are all older than the newest few
    /// thousand entries the banner hid itself -- taking the only buttons that could clear them
    /// with it, in exactly the case the buttons were widened to handle.
    /// </remarks>
    public bool HasConflicts => _heldCount > 0;

    private int _heldCount;

    /// <summary>Rows currently shown.</summary>
    public ObservableCollection<UploadRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    [ObservableProperty]
    private string _summary = "Nothing recorded yet.";

    [ObservableProperty]
    private UploadFilter _filter = UploadFilter.All;

    /// <summary>What stopped a decision being carried out, for the view to show.</summary>
    /// <remarks>
    /// Cleared on every reload, so a message never outlives the situation that produced it: an
    /// offline warning left standing beside a list that has since changed is worse than none.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResolveProblem))]
    private string _resolveProblem = string.Empty;

    /// <summary>True when there is something worth a line of red text.</summary>
    public bool HasResolveProblem => ResolveProblem.Length > 0;

    /// <summary>
    /// True when Replace has been pressed once and is waiting to be confirmed.
    /// </summary>
    /// <remarks>
    /// What makes the confirmation safe is <c>_armed</c>, which holds exactly the files the
    /// first press counted, so a second press cannot act on anything else however long it waits.
    /// This flag is additionally cleared by the other buttons and by any reload -- but note that
    /// ticking a row does not clear it, because ticking no longer refreshes anything. Safe, but
    /// not for the reason an earlier version of this remark claimed.
    /// </remarks>
    [ObservableProperty]
    private bool _overwriteArmed;

    /// <summary>Exactly the files the pending confirmation counted.</summary>
    private IReadOnlyList<UploadRecord> _armed = [];

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
        ResolveProblem = string.Empty;

        // A confirmation counted a specific set of files. If the list is being rebuilt, that
        // count no longer describes anything, so the confirmation goes with it.
        OverwriteArmed = false;
        _armed = [];

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
                var row = new UploadRowViewModel(record)
                {
                    IsSelected = _picked.Contains(record.LocalPath),
                };

                // The row reports back, so a tick outlives the row that made it.
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(UploadRowViewModel.IsSelected))
                    {
                        return;
                    }

                    if (row.IsSelected)
                    {
                        _picked.Add(row.Record.LocalPath);
                    }
                    else
                    {
                        _picked.Remove(row.Record.LocalPath);
                    }
                };

                Rows.Add(row);
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

    /// <summary>
    /// Replaces the remote copy with the local one, after asking a second time.
    /// </summary>
    /// <remarks>
    /// The only action here that destroys something, and the one whose scope is easiest to get
    /// wrong: leaving everything unticked means every held file, which is usually what somebody
    /// wants and occasionally very much not. So the first press says how many and what will
    /// happen, and the second carries it out. Two presses of one button rather than a dialog,
    /// because a dialog in a view model cannot be tested and this is worth testing.
    /// </remarks>
    [RelayCommand]
    private async Task ResolveOverwriteAsync()
    {
        // Confirming does not look at the ledger again. It acts on exactly the list the first
        // press counted -- the count is the whole point of asking, and a batch landing between
        // the two presses would otherwise be replaced without ever having been counted. Looking
        // again also meant a file the engine had picked up in between made this branch conclude
        // there was nothing to do at all, and the confirmation quietly evaporated.
        if (OverwriteArmed)
        {
            var confirmed = _armed;

            _armed = [];
            OverwriteArmed = false;

            var applied = 0;

            foreach (var record in confirmed)
            {
                var wrote = await _store
                    .ResolveConflictAsync(record.LocalPath, ConflictResolution.Overwrite)
                    .ConfigureAwait(true);

                applied += wrote;

                if (wrote > 0)
                {
                    // Retracted, not restated as queued. The sweep will report it from the start,
                    // and a queued row nothing ever removes keeps the refresh timer awake for
                    // ever.
                    _forget?.Invoke(record.LocalPath);
                }
            }

            await RefreshAsync().ConfigureAwait(true);

            ReportShortfall(applied, confirmed.Count);
            return;
        }

        var targets = await TargetsAsync().ConfigureAwait(true);

        // Deliberately not offered for a damaged local file: that would push a short acquisition
        // over a good remote copy.
        var eligible = targets
            .Where(r => r.ConflictKind != ConflictKind.LocalFileDamaged)
            .ToArray();

        if (eligible.Length == 0)
        {
            await RefreshAsync().ConfigureAwait(true);

            if (targets.Count > 0)
            {
                ResolveProblem =
                    "Nothing was replaced. Every file picked is held because its own contents are "
                    + "damaged, and replacing a good copy on the server with a short one is what "
                    + "that check exists to prevent. Keep is the choice that applies.";
            }

            return;
        }

        _armed = eligible;
        OverwriteArmed = true;

        ResolveProblem =
            $"This will replace {eligible.Length} file(s) on the server with the local copies, "
            + "and what is there now will be gone. Press Replace again to go ahead, or any "
            + "other button to stop.";
    }

    /// <summary>
    /// Says so when a decision did not reach every file it was aimed at.
    /// </summary>
    /// <remarks>
    /// The store refuses to write over a row the engine has moved on to since the list was drawn,
    /// which is right -- but silently. Without this the user sees no error and reasonably believes
    /// every conflict is settled, when some are still held and will be offered again.
    /// </remarks>
    private void ReportShortfall(int applied, int attempted)
    {
        if (applied >= attempted)
        {
            return;
        }

        // Deliberately does not name a cause. Zero rows changed means the row is no longer
        // held, which is usually a transfer having picked it up -- but it is also what a second
        // window, a double press, or a deleted row produces, and those need different remedies.
        // Sending somebody to look for a transfer that never ran is worse than saying less.
        ResolveProblem =
            $"{attempted - applied} of {attempted} file(s) were not changed, because they are no "
            + "longer held: something else settled them while this list was on screen. Refresh to "
            + "see where they stand.";
    }

    /// <summary>Keeps what is on the server and stops offering the local file.</summary>
    [RelayCommand]
    private async Task ResolveKeepAsync()
    {
        OverwriteArmed = false;

        var targets = await TargetsAsync().ConfigureAwait(true);
        var applied = 0;

        foreach (var record in targets)
        {
            var wrote = await _store
                .ResolveConflictAsync(record.LocalPath, ConflictResolution.Keep)
                .ConfigureAwait(true);

            applied += wrote;

            if (wrote > 0)
            {
                Announce(record, TransferState.Declined, "Kept what is on the server");
            }
        }

        await RefreshAsync().ConfigureAwait(true);

        ReportShortfall(applied, targets.Count);
    }

    /// <summary>
    /// Sends the local file alongside the remote one, under the first free name.
    /// </summary>
    /// <remarks>
    /// The names come from <see cref="RenamePlanner"/> in Core. They were worked out here at
    /// first, which put transfer logic in a view model, duplicated the engine's own renaming, and
    /// could not be tested without a dispatcher.
    /// </remarks>
    [RelayCommand]
    private async Task ResolveRenameAsync()
    {
        OverwriteArmed = false;

        var client = _client();

        if (client is null)
        {
            ResolveProblem =
                "A free name has to be checked against the server, and there is no connection at "
                + "the moment. Test the connection on the Server tab, then try again.";
            return;
        }

        var targets = await TargetsAsync().ConfigureAwait(true);

        var plan = await new RenamePlanner(client)
            .PlanAsync(targets.Where(r => r.ConflictKind != ConflictKind.LocalFileDamaged))
            .ConfigureAwait(true);

        if (!plan.IsUsable)
        {
            ResolveProblem = plan.Problem!;
            return;
        }

        var applied = 0;

        foreach (var (record, name) in plan.Proposals)
        {
            var wrote = await _store
                .ResolveConflictAsync(record.LocalPath, ConflictResolution.Rename, name)
                .ConfigureAwait(true);

            applied += wrote;

            if (wrote > 0)
            {
                _forget?.Invoke(record.LocalPath);
            }
        }

        await RefreshAsync().ConfigureAwait(true);

        ReportShortfall(applied, plan.Proposals.Count);

        if (plan.Proposals.Count == 0 && targets.Count > 0)
        {
            // Two ways to get here, and they need opposite remedies. Blaming truncation for a
            // malformed destination sends somebody to look at an acquisition that is fine.
            var damaged = targets.Count(r => r.ConflictKind == ConflictKind.LocalFileDamaged);

            ResolveProblem = damaged == targets.Count
                ? "Nothing was renamed. Every file picked is held because its own contents are "
                  + "damaged, and sending a short acquisition under any name is not the answer. "
                  + "Keep is the choice that applies."
                : "Nothing was renamed. No new name could be worked out for the files picked, "
                  + "which usually means their recorded destination cannot be read. The "
                  + "application log records the path for each one.";
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

        // Taken from the same scan the summary needs. Asking twice per reload meant two GROUP BY
        // passes over the whole table for every keystroke in the search box.
        _heldCount = counts.GetValueOrDefault(TransferState.Conflict);
        OnPropertyChanged(nameof(HasConflicts));

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
