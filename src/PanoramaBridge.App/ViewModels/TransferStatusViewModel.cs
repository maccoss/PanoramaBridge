using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
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

    /// <summary>
    /// Consecutive quiet ticks tolerated before the timer stops.
    /// </summary>
    /// <remarks>
    /// A couple of ticks of grace avoids stopping and restarting the timer between two files in
    /// a batch, which would be more work than leaving it running.
    /// </remarks>
    private const int QuietTicksBeforeStopping = 5;

    private int _quietTicks;

    public TransferStatusViewModel(TransferProgressAggregator aggregator)
    {
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = RefreshInterval,
        };
        _timer.Tick += (_, _) => Refresh();

        // Deliberately not started. This application spends nearly all of its life on an
        // instrument computer with nothing to display, and a timer ticking five times a second
        // forever keeps the processor out of its deep idle states for no benefit. The aggregator
        // wakes us when there is actually something to draw.
        _aggregator.WorkAppeared += Wake;
    }

    /// <summary>
    /// Starts refreshing because work has appeared. Safe to call from a worker thread.
    /// </summary>
    private void Wake()
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(Wake, DispatcherPriority.Background);
            return;
        }

        _quietTicks = 0;

        if (!_timer.IsEnabled)
        {
            _timer.Start();
            Refresh();
        }
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
                var before = row.Band;
                row.Apply(progress);

                if (row.Band != before)
                {
                    Reband(row, before);
                }

                continue;
            }

            row = new TransferRowViewModel(progress);
            _byPath[progress.LocalPath] = row;

            Insert(row);
        }

        TrimOldRows();

        var totals = _aggregator.Totals();
        Summary = totals.Describe();
        OverallProgress = totals.Fraction;
        AttentionCount = totals.NeedsAttention;

        // Stop once there is nothing moving and nothing left to draw. Anything new restarts us
        // through WorkAppeared, so no polling is needed to notice.
        var busy = totals.Active > 0 || totals.Queued > 0 || _aggregator.HasPendingChanges;

        _quietTicks = busy ? 0 : _quietTicks + 1;

        if (_quietTicks >= QuietTicksBeforeStopping && _timer.IsEnabled)
        {
            _timer.Stop();
        }
    }

    /// <summary>
    /// Where each block of the grid starts, indexed by band.
    /// </summary>
    /// <remarks>
    /// Kept alongside <see cref="Rows"/> rather than recomputed, so a row changing state costs a
    /// single move rather than a re-sort of a grid that can hold thousands of rows and is being
    /// refreshed five times a second.
    /// </remarks>
    private readonly int[] _bandCounts = new int[4];

    private int StartOf(TransferBand band)
    {
        var start = 0;

        for (var i = 0; i < (int)band; i++)
        {
            start += _bandCounts[i];
        }

        return start;
    }

    /// <summary>
    /// Adds a row at the top of its block.
    /// </summary>
    /// <remarks>
    /// Newest first within a block, which is what someone watching a live transfer expects: the
    /// file that just started is at the top, and the one that just finished verifying is at the
    /// top of the finished block directly below it.
    /// </remarks>
    private void Insert(TransferRowViewModel row)
    {
        Rows.Insert(StartOf(row.Band), row);
        _bandCounts[(int)row.Band]++;
    }

    /// <summary>Moves a row whose state has taken it into a different block.</summary>
    private void Reband(TransferRowViewModel row, TransferBand from)
    {
        var oldIndex = Rows.IndexOf(row);

        if (oldIndex < 0)
        {
            return;
        }

        _bandCounts[(int)from]--;

        // Move interprets its target index in the list as it will be after the removal, which is
        // exactly what the counts now describe.
        Rows.Move(oldIndex, StartOf(row.Band));
        _bandCounts[(int)row.Band]++;
    }

    private void RemoveRow(int index)
    {
        var row = Rows[index];

        Rows.RemoveAt(index);
        _bandCounts[(int)row.Band]--;
        _byPath.Remove(row.LocalPath);
    }

    /// <summary>Forgets rows that finished cleanly, keeping anything unresolved.</summary>
    [RelayCommand]
    private void ClearCompleted()
    {
        foreach (var path in _aggregator.ClearFinished())
        {
            if (!_byPath.TryGetValue(path, out var row))
            {
                continue;
            }

            var index = Rows.IndexOf(row);

            if (index >= 0)
            {
                RemoveRow(index);
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
            if (Rows[i].State is TransferState.Verified or TransferState.Skipped
                or TransferState.Declined)
            {
                RemoveRow(i);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _aggregator.WorkAppeared -= Wake;
        _timer.Stop();
    }
}
