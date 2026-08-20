using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;

namespace PanoramaBridge.App.ViewModels;

/// <summary>
/// The Transfer Status tab.
/// </summary>
/// <remarks>
/// Draws from the aggregator on a timer rather than subscribing to the engine directly. Five
/// times a second is fast enough to look live and slow enough that the grid is never the
/// bottleneck, however many files are moving.
/// </remarks>
public sealed partial class TransferStatusViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// How often the grid is refreshed. Chosen to look continuous to a person while bounding
    /// the work: at one-mebibyte reporting granularity the engine can raise thousands of
    /// updates a second, and none of them individually matter.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Rows kept on screen. Older finished rows are dropped once past this, because a grid
    /// holding an unbounded session history is a slow memory leak on a machine that uploads for
    /// weeks without restarting.
    /// </summary>
    private const int MaxRows = 5000;

    private readonly TransferProgressAggregator _aggregator;
    private readonly Dictionary<string, TransferRowViewModel> _byPath =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly DispatcherTimer _timer;

    public TransferStatusViewModel(TransferProgressAggregator aggregator)
    {
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = RefreshInterval,
        };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    /// <summary>Rows bound to the grid. Only ever mutated on the UI thread.</summary>
    public ObservableCollection<TransferRowViewModel> Rows { get; } = [];

    /// <summary>One line summarising everything in flight, for the status bar.</summary>
    [ObservableProperty]
    private string _summary = "Idle";

    /// <summary>Overall progress of the work in flight, for the taskbar indicator.</summary>
    [ObservableProperty]
    private double? _overallProgress;

    /// <summary>How many rows need a person to do something.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttentionItems))]
    private int _attentionCount;

    /// <summary>True when at least one row failed, conflicted or was superseded.</summary>
    public bool HasAttentionItems => AttentionCount > 0;

    /// <summary>Drains whatever changed and applies it to the grid.</summary>
    public void Refresh()
    {
        foreach (var progress in _aggregator.DrainChanged())
        {
            if (_byPath.TryGetValue(progress.LocalPath, out var row))
            {
                // Update in place: replacing the row would reset selection and scroll position,
                // and virtualization recycles the container anyway.
                row.Apply(progress);
                continue;
            }

            row = new TransferRowViewModel(progress);
            _byPath[progress.LocalPath] = row;

            // Newest at the top, which is what someone watching a live transfer expects.
            Rows.Insert(0, row);
        }

        TrimOldRows();

        var totals = _aggregator.Totals();
        Summary = totals.Describe();
        OverallProgress = totals.Fraction;
        AttentionCount = totals.NeedsAttention;
    }

    /// <summary>Forgets rows that finished cleanly, keeping anything unresolved.</summary>
    [RelayCommand]
    private void ClearCompleted()
    {
        foreach (var path in _aggregator.ClearFinished())
        {
            if (_byPath.Remove(path, out var row))
            {
                Rows.Remove(row);
            }
        }

        Refresh();
    }

    /// <summary>Reveals a row's file in Explorer.</summary>
    [RelayCommand]
    private static void OpenContainingFolder(TransferRowViewModel? row)
    {
        if (row is null || !File.Exists(row.LocalPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.LocalPath}\"")
        {
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// Drops the oldest finished rows once the grid is over its cap.
    /// </summary>
    /// <remarks>
    /// Only finished rows are eligible. Anything still moving, or needing a decision, stays
    /// however long the list gets -- silently discarding a failure would be worse than a long
    /// list.
    /// </remarks>
    private void TrimOldRows()
    {
        if (Rows.Count <= MaxRows)
        {
            return;
        }

        for (var i = Rows.Count - 1; i >= 0 && Rows.Count > MaxRows; i--)
        {
            var row = Rows[i];

            if (row.State is TransferState.Verified or TransferState.Skipped)
            {
                Rows.RemoveAt(i);
                _byPath.Remove(row.LocalPath);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _timer.Stop();
}
