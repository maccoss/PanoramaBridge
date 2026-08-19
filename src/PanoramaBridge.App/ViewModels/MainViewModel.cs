using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PanoramaBridge.App.Services;
using PanoramaBridge.Core.Infrastructure;
using PanoramaBridge.Core.Updates;

namespace PanoramaBridge.App.ViewModels;

/// <summary>
/// Shell view model.
/// </summary>
/// <remarks>
/// At this stage it exists to prove the update rail end to end. The monitoring, transfer and
/// upload-ledger tabs are layered on top of this shell in later phases.
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly UpdateService _updates;
    private readonly AppPaths _paths;
    private readonly ILogger<MainViewModel> _log;
    private readonly CancellationTokenSource _shutdown = new();

    private PeriodicTimer? _updateTimer;
    private Task? _updateLoop;

    /// <summary>How often the background update check runs.</summary>
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(4);

    public MainViewModel(UpdateService updates, AppPaths paths, ILogger<MainViewModel> log)
    {
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        _updates.StatusChanged += OnUpdateStatusChanged;
        ApplyStatus(_updates.Status);
    }

    public string ProductName => AppInfo.ProductName;

    public string Version => AppInfo.InformationalVersion;

    public string RuntimeIdentifier => AppInfo.RuntimeIdentifier;

    public string DataDirectory => _paths.Root;

    public string LogDirectory => _paths.LogDirectory;

    public string InstallKind => _updates.IsManagedInstall
        ? "Managed install (updates enabled)"
        : "Unmanaged build (updates disabled)";

    /// <summary>One-line summary of the update state, shown in the shell.</summary>
    [ObservableProperty]
    private string _updateSummary = "Update status unknown.";

    /// <summary>Longer explanation, including any operator message from the version policy.</summary>
    [ObservableProperty]
    private string? _updateDetail;

    /// <summary>True while a check or download is in flight, so the button can disable itself.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    private bool _isCheckingForUpdates;

    /// <summary>True once an update is staged and a restart would move to it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyUpdateCommand))]
    private bool _isUpdateReady;

    /// <summary>
    /// True when this build is below the published minimum supported version. New uploads
    /// must not start while this is set.
    /// </summary>
    [ObservableProperty]
    private bool _uploadsBlocked;

    /// <summary>Starts the periodic background update check. Called once the window is shown.</summary>
    public void Start()
    {
        _updateLoop ??= Task.Run(() => RunUpdateLoopAsync(_shutdown.Token));
    }

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingForUpdates = true;
        try
        {
            await _updates.CheckAsync(_shutdown.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    private bool CanCheckForUpdates() => !IsCheckingForUpdates;

    [RelayCommand(CanExecute = nameof(CanApplyUpdate))]
    private void ApplyUpdate()
    {
        // Later phases gate this on an idle transfer queue. Restarting mid-upload would be
        // worse than staying a version behind.
        if (!_updates.ApplyAndRestart())
        {
            _log.LogWarning("Apply update was requested but nothing was staged.");
        }
    }

    private bool CanApplyUpdate() => IsUpdateReady;

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(_paths.Root);

    [RelayCommand]
    private void OpenLogFolder() => OpenFolder(_paths.LogDirectory);

    private void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open {Path}.", path);
        }
    }

    private async Task RunUpdateLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _updates.CheckAsync(cancellationToken).ConfigureAwait(false);

            _updateTimer = new PeriodicTimer(UpdateCheckInterval);
            while (await _updateTimer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await _updates.CheckAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "The background update loop stopped unexpectedly.");
        }
    }

    private void OnUpdateStatusChanged(UpdateStatus status)
    {
        // UpdateService raises this from a background thread.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(() => ApplyStatus(status));
            return;
        }

        ApplyStatus(status);
    }

    private void ApplyStatus(UpdateStatus status)
    {
        UpdateSummary = status.Stage switch
        {
            UpdateStage.Idle => "Update status unknown.",
            UpdateStage.NotInstalled => "Updates are not available for this build.",
            UpdateStage.Checking => "Checking for updates...",
            UpdateStage.UpToDate => $"PanoramaBridge {Version} is up to date.",
            UpdateStage.Downloading =>
                $"Downloading update {status.AvailableVersion} ({status.DownloadPercent}%)...",
            UpdateStage.ReadyToApply =>
                $"Update {status.AvailableVersion} is ready. Restart to apply it.",
            UpdateStage.Failed => "Could not check for updates.",
            _ => "Update status unknown.",
        };

        IsUpdateReady = status.RestartWouldUpdate;
        UploadsBlocked = status.UploadsBlocked;

        UpdateDetail = BuildDetail(status);
    }

    private string? BuildDetail(UpdateStatus status)
    {
        if (status.UploadsBlocked)
        {
            var floor = status.Policy?.MinimumSupportedVersion?.ToString() ?? "a newer version";
            var message = string.IsNullOrWhiteSpace(status.Policy?.Message)
                ? string.Empty
                : " " + status.Policy!.Message;

            return $"This build is older than the minimum supported version ({floor}). "
                + $"New uploads are blocked until it is updated.{message}";
        }

        if (status.Stage == UpdateStage.Failed && !string.IsNullOrWhiteSpace(status.Error))
        {
            return $"{status.Error} PanoramaBridge keeps working; it will try again later.";
        }

        if (status.Policy?.Status == VersionPolicyStatus.Unavailable)
        {
            return "The update policy could not be read, so no version floor is being enforced.";
        }

        return null;
    }

    public void Dispose()
    {
        _updates.StatusChanged -= OnUpdateStatusChanged;

        _shutdown.Cancel();
        _updateTimer?.Dispose();
        _shutdown.Dispose();
    }
}
