using System.IO;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PanoramaBridge.App.Services;
using PanoramaBridge.Core.Infrastructure;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Updates;

namespace PanoramaBridge.App.ViewModels;

/// <summary>
/// The shell: the tabs, the command bar, the update banner and the status line.
/// </summary>
/// <remarks>
/// The secret is never a property here. The view's password box holds it and hands it over
/// through <see cref="SecretProvider"/> only at the moment a command needs it, so it is never
/// part of anything bound, serialized, or picked up by a diagnostic that dumps the view model.
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(4);

    private readonly TransferService _transfers;
    private readonly UpdateService _updates;
    private readonly ICredentialStoreAccessor _credentials;
    private readonly ResourceGovernor _governor;
    private readonly AppPaths _paths;
    private readonly ILogger<MainViewModel> _log;
    private readonly CancellationTokenSource _shutdown = new();

    private Task? _updateLoop;
    private PeriodicTimer? _updateTimer;

    public MainViewModel(
        SettingsViewModel settings,
        TransferStatusViewModel transferStatus,
        UploadsViewModel uploads,
        TransferService transfers,
        UpdateService updates,
        ICredentialStoreAccessor credentials,
        ResourceGovernor governor,
        AppPaths paths,
        ILogger<MainViewModel> log)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        TransferStatus = transferStatus ?? throw new ArgumentNullException(nameof(transferStatus));
        Uploads = uploads ?? throw new ArgumentNullException(nameof(uploads));
        _transfers = transfers ?? throw new ArgumentNullException(nameof(transfers));
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        _updates.StatusChanged += OnUpdateStatusChanged;
        _transfers.RunStateChanged += OnRunStateChanged;

        ApplyUpdateStatus(_updates.Status);
    }

    public SettingsViewModel Settings { get; }

    public TransferStatusViewModel TransferStatus { get; }

    public UploadsViewModel Uploads { get; }

    /// <summary>
    /// Supplies the secret from the view's password box on demand.
    /// </summary>
    /// <remarks>
    /// A callback rather than a bound property so the secret lives only in the control that
    /// collected it, for as long as the command needs it.
    /// </remarks>
    public Func<string?>? SecretProvider { get; set; }

    public string ProductName => AppInfo.ProductName;

    public string Version => AppInfo.InformationalVersion;

    public string WindowTitle => $"{AppInfo.ProductName} {AppInfo.InformationalVersion}";

    // -- Command bar ---------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UploadNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusLine = "Ready.";

    [ObservableProperty]
    private string? _connectionDetail;

    [ObservableProperty]
    private bool _connectionFailed;

    // -- Update banner -------------------------------------------------------------------------

    [ObservableProperty]
    private string _updateSummary = string.Empty;

    [ObservableProperty]
    private string? _updateDetail;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyUpdateCommand))]
    private bool _isUpdateReady;

    /// <summary>
    /// True when this build is below the published minimum. No new upload may start.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UploadNowCommand))]
    private bool _uploadsBlocked;

    /// <summary>Whether to show the informational update strip.</summary>
    public bool ShowUpdateBanner => IsUpdateReady && !UploadsBlocked;

    /// <summary>Starts the background update loop once there is a window to report into.</summary>
    public void Start()
    {
        _updateLoop ??= Task.Run(() => RunUpdateLoopAsync(_shutdown.Token));
        _ = Uploads.RefreshAsync();
        _ = TrimAfterStartupAsync();
    }

    /// <summary>
    /// Hands back the memory startup needed, once it is no longer needed.
    /// </summary>
    /// <remarks>
    /// Building the window costs well over a hundred megabytes of working set, almost none of
    /// which an idle monitor needs afterwards. Measured on this build, one trim takes the process
    /// from 138 MB to about 14 MB and it stays there, with processor use remaining at a tenth of
    /// one percent of a core.
    /// <para>
    /// A one-shot delay rather than a recurring timer, deliberately: a timer that fires forever
    /// to check whether anything needs doing is exactly the kind of idle cost this is trying to
    /// avoid. Later trims happen when a transfer run finishes, which is the only other point at
    /// which the working set grows.
    /// </para>
    /// </remarks>
    private async Task TrimAfterStartupAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), _shutdown.Token).ConfigureAwait(false);
            _governor.ReleaseIdleMemory();
        }
        catch (OperationCanceledException)
        {
            // Closed before the delay elapsed.
        }
    }

    /// <summary>
    /// Scans the monitored directory and uploads whatever needs uploading.
    /// </summary>
    /// <remarks>
    /// Blocked while the version floor is unmet, so a build known to mishandle data cannot start
    /// new work. Anything already in flight is allowed to finish.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanUploadNow))]
    private async Task UploadNowAsync()
    {
        var settings = await Settings.SaveAsync(_shutdown.Token).ConfigureAwait(true);

        var problems = settings.Validate();
        if (problems.Count > 0)
        {
            StatusLine = problems[0];
            ConnectionFailed = true;
            return;
        }

        IsBusy = true;
        ConnectionFailed = false;
        StatusLine = "Looking for files to transfer...";

        try
        {
            RememberCredential(settings);

            var summary = await _transfers
                .ScanAndUploadAsync(settings, SecretProvider?.Invoke(), _shutdown.Token)
                .ConfigureAwait(true);

            StatusLine = Describe(summary);
            await Uploads.RefreshAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusLine = "Stopped.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "The transfer run failed.");
            StatusLine = ex.Message;
            ConnectionFailed = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanUploadNow() => !IsBusy && !UploadsBlocked;

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        StatusLine = "Stopping...";
        _transfers.Cancel();
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        var settings = Settings.ToSettings();

        StatusLine = "Checking the connection...";
        ConnectionFailed = false;
        ConnectionDetail = null;

        var result = await _transfers
            .TestConnectionAsync(settings, SecretProvider?.Invoke(), _shutdown.Token)
            .ConfigureAwait(true);

        StatusLine = result.Summary;
        ConnectionDetail = result.Detail;

        // A read-only destination is a failure worth flagging now rather than at the end of a
        // long transfer.
        ConnectionFailed = !result.Succeeded || !result.CanUploadToDestination;

        if (result.Succeeded)
        {
            RememberCredential(settings);
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        await Settings.SaveAsync(_shutdown.Token).ConfigureAwait(true);
        StatusLine = "Settings saved.";
    }

    [RelayCommand(CanExecute = nameof(IsUpdateReady))]
    private void ApplyUpdate()
    {
        if (_transfers.IsRunning)
        {
            // Restarting mid-transfer would be worse than staying a version behind.
            StatusLine = "Waiting for the current transfer to finish before updating.";
            return;
        }

        if (!_updates.ApplyAndRestart())
        {
            StatusLine = "No update is staged.";
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        StatusLine = "Checking for updates...";
        await _updates.CheckAsync(_shutdown.Token).ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenLogFolder() => OpenFolder(_paths.LogDirectory);

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(_paths.Root);

    [RelayCommand]
    private static void ShowAbout() =>
        MessageBox.Show(
            $"{AppInfo.ProductName} {AppInfo.InformationalVersion}\n"
            + $"{AppInfo.RuntimeIdentifier}\n\n"
            + "MacCoss Lab, University of Washington",
            $"About {AppInfo.ProductName}",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    /// <summary>Stores or clears the credential according to the user's choice.</summary>
    private void RememberCredential(AppSettings settings)
    {
        var secret = SecretProvider?.Invoke();

        if (!settings.SaveCredentials)
        {
            // Unticking the box has to actually remove what was stored earlier, not merely stop
            // adding to it.
            _credentials.Forget(settings.ServerUrl);
            return;
        }

        if (!string.IsNullOrWhiteSpace(secret))
        {
            _credentials.Remember(
                settings.ServerUrl,
                settings.AuthMode == AuthMode.ApiKey ? "apikey" : settings.UserName,
                secret);
        }
    }

    private static string Describe(Core.Transfer.TransferSummary summary)
    {
        if (summary.Total == 0)
        {
            return "Nothing needed transferring.";
        }

        var parts = new List<string>();

        if (summary.Uploaded > 0)
        {
            parts.Add($"{summary.Uploaded} uploaded");
        }

        if (summary.Skipped > 0)
        {
            parts.Add($"{summary.Skipped} already there");
        }

        if (summary.Conflicts > 0)
        {
            parts.Add($"{summary.Conflicts} need a decision");
        }

        if (summary.Failed > 0)
        {
            parts.Add($"{summary.Failed} failed");
        }

        return string.Join(", ", parts) + $" in {summary.Elapsed.TotalSeconds:F0}s.";
    }

    private void OnRunStateChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(() => IsBusy = _transfers.IsRunning);
            return;
        }

        IsBusy = _transfers.IsRunning;
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
            // Shutting down.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "The background update loop stopped unexpectedly.");
        }
    }

    private void OnUpdateStatusChanged(UpdateStatus status)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(() => ApplyUpdateStatus(status));
            return;
        }

        ApplyUpdateStatus(status);
    }

    private void ApplyUpdateStatus(UpdateStatus status)
    {
        IsUpdateReady = status.RestartWouldUpdate;
        UploadsBlocked = status.UploadsBlocked;

        UpdateSummary = status.Stage switch
        {
            UpdateStage.ReadyToApply =>
                $"Version {status.AvailableVersion} is ready. Restart to apply it.",
            UpdateStage.Downloading =>
                $"Downloading version {status.AvailableVersion} ({status.DownloadPercent}%)...",
            UpdateStage.UpToDate => $"{AppInfo.InformationalVersion} is up to date.",
            UpdateStage.NotInstalled => "Updates are not available for this build.",
            UpdateStage.Failed => "Could not check for updates.",
            _ => string.Empty,
        };

        UpdateDetail = status.UploadsBlocked
            ? $"This build is older than the minimum supported version "
              + $"({status.Policy?.MinimumSupportedVersion}). New uploads are blocked until it is "
              + $"updated. {status.Policy?.Message}".TrimEnd()
            : null;

        OnPropertyChanged(nameof(ShowUpdateBanner));
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open {Path}.", path);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _updates.StatusChanged -= OnUpdateStatusChanged;
        _transfers.RunStateChanged -= OnRunStateChanged;

        _shutdown.Cancel();
        _updateTimer?.Dispose();
        _shutdown.Dispose();

        TransferStatus.Dispose();
    }
}

/// <summary>
/// Narrow view of the credential store for the shell.
/// </summary>
/// <remarks>
/// Deliberately smaller than the full store interface: the shell needs to remember and forget,
/// and nothing more. Reading a secret back out is the transfer service's business.
/// </remarks>
public interface ICredentialStoreAccessor
{
    void Remember(string serverUrl, string userName, string secret);

    void Forget(string serverUrl);
}
