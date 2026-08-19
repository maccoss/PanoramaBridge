using Microsoft.Extensions.Logging;
using PanoramaBridge.Core.Infrastructure;
using PanoramaBridge.Core.Updates;
using Velopack;
using Velopack.Sources;

namespace PanoramaBridge.App.Services;

/// <summary>Where the update workflow currently stands.</summary>
public enum UpdateStage
{
    /// <summary>Nothing has been attempted yet.</summary>
    Idle,

    /// <summary>Running from a development build or an unmanaged copy; updates do not apply.</summary>
    NotInstalled,

    /// <summary>A check is in flight.</summary>
    Checking,

    /// <summary>This build is current.</summary>
    UpToDate,

    /// <summary>An update is being fetched in the background.</summary>
    Downloading,

    /// <summary>An update is staged and will apply on the next restart.</summary>
    ReadyToApply,

    /// <summary>The last check or download failed. The application keeps running.</summary>
    Failed,
}

/// <summary>Everything the UI needs to render the update banner.</summary>
/// <param name="Stage">Where the update workflow stands.</param>
/// <param name="AvailableVersion">Version staged or offered, when there is one.</param>
/// <param name="DownloadPercent">Download progress, 0-100, while downloading.</param>
/// <param name="Policy">Result of evaluating the published minimum-version floor.</param>
/// <param name="Error">Human-readable reason the last attempt failed.</param>
public sealed record UpdateStatus(
    UpdateStage Stage,
    string? AvailableVersion = null,
    int DownloadPercent = 0,
    VersionPolicyResult? Policy = null,
    string? Error = null)
{
    /// <summary>
    /// True when this build is below the published floor. New uploads must not start;
    /// transfers already in flight are allowed to finish.
    /// </summary>
    public bool UploadsBlocked => Policy?.UploadsBlocked == true;

    /// <summary>True when a restart would move the user onto a newer build.</summary>
    public bool RestartWouldUpdate => Stage == UpdateStage.ReadyToApply;
}

/// <summary>
/// Checks for updates, stages them, and evaluates the published minimum-version floor.
/// </summary>
/// <remarks>
/// Two deliberate rules govern this class.
/// <para>
/// It never restarts the application by itself. PanoramaBridge routinely holds multi-hour
/// uploads of multi-gigabyte acquisitions, and rebooting out from under one would be worse
/// than running a slightly old build. The service stages the update and reports
/// ReadyToApply; deciding when to act on that belongs to the caller, which knows whether the
/// transfer queue is idle.
/// </para>
/// <para>
/// It never throws out of a check. A failed update check is reported, logged, and otherwise
/// ignored, so an unreachable GitHub can never stop an instrument PC from uploading.
/// </para>
/// </remarks>
public sealed class UpdateService
{
    private readonly UpdateManager? _manager;
    private readonly VersionPolicyClient _policyClient;
    private readonly ILogger<UpdateService> _log;

    private UpdateInfo? _staged;

    public UpdateService(
        VersionPolicyClient policyClient,
        ILogger<UpdateService> log,
        string? explicitChannel = null)
    {
        _policyClient = policyClient ?? throw new ArgumentNullException(nameof(policyClient));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        try
        {
            var source = new GithubSource(AppInfo.RepositoryUrl, accessToken: null, prerelease: false);
            var options = new UpdateOptions { ExplicitChannel = explicitChannel };
            _manager = new UpdateManager(source, options);
        }
        catch (Exception ex)
        {
            // A missing or malformed Velopack install must not stop the app from running.
            _log.LogWarning(ex, "Update manager could not be initialised; updates are disabled.");
            _manager = null;
        }
    }

    /// <summary>Raised whenever the status changes. Fires on a background thread.</summary>
    public event Action<UpdateStatus>? StatusChanged;

    /// <summary>Latest known status.</summary>
    public UpdateStatus Status { get; private set; } = new(UpdateStage.Idle);

    /// <summary>
    /// True when this build is managed by Velopack. False under "dotnet run", in tests, and
    /// for a hand-copied build directory.
    /// </summary>
    public bool IsManagedInstall => _manager?.IsInstalled == true;

    /// <summary>
    /// Evaluates the version floor, checks for a newer release, and stages it if there is one.
    /// Never throws except on caller-requested cancellation.
    /// </summary>
    public async Task<UpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        // The floor is evaluated even for unmanaged builds: someone running a hand-copied
        // build still needs to be told their version is too old to upload with.
        var policy = await EvaluatePolicyAsync(cancellationToken).ConfigureAwait(false);

        if (_manager is null || !_manager.IsInstalled)
        {
            _log.LogDebug("Not a managed install; skipping the update check.");
            return Publish(new UpdateStatus(UpdateStage.NotInstalled, Policy: policy));
        }

        Publish(new UpdateStatus(UpdateStage.Checking, Policy: policy));

        UpdateInfo? update;
        try
        {
            update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Update check failed.");
            return Publish(new UpdateStatus(UpdateStage.Failed, Policy: policy, Error: ex.Message));
        }

        if (update is null)
        {
            _log.LogInformation("PanoramaBridge {Version} is up to date.", AppInfo.InformationalVersion);
            return Publish(new UpdateStatus(UpdateStage.UpToDate, Policy: policy));
        }

        var version = update.TargetFullRelease.Version.ToString();
        _log.LogInformation("Update {Version} available; downloading in the background.", version);

        Publish(new UpdateStatus(UpdateStage.Downloading, version, 0, policy));

        try
        {
            await _manager
                .DownloadUpdatesAsync(
                    update,
                    percent => Publish(new UpdateStatus(UpdateStage.Downloading, version, percent, policy)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Downloading update {Version} failed.", version);
            return Publish(new UpdateStatus(UpdateStage.Failed, version, 0, policy, ex.Message));
        }

        _staged = update;
        _log.LogInformation("Update {Version} staged; it will apply on the next restart.", version);
        return Publish(new UpdateStatus(UpdateStage.ReadyToApply, version, 100, policy));
    }

    /// <summary>
    /// Applies the staged update and restarts the application.
    /// </summary>
    /// <remarks>
    /// Only call this once the transfer queue is idle. This method does not, and cannot,
    /// determine that for itself.
    /// </remarks>
    /// <returns>False when there was nothing staged to apply.</returns>
    public bool ApplyAndRestart()
    {
        if (_manager is null || _staged is null)
        {
            return false;
        }

        _log.LogInformation(
            "Applying update {Version} and restarting.",
            _staged.TargetFullRelease.Version);

        _manager.ApplyUpdatesAndRestart(_staged.TargetFullRelease);
        return true;
    }

    private async Task<VersionPolicyResult> EvaluatePolicyAsync(CancellationToken cancellationToken)
    {
        var policy = await _policyClient
            .EvaluateAsync(AppInfo.Version, cancellationToken)
            .ConfigureAwait(false);

        if (policy.UploadsBlocked)
        {
            _log.LogWarning(
                "This build ({Current}) is below the minimum supported version ({Minimum}). "
                + "New uploads are blocked until it is updated.",
                AppInfo.InformationalVersion,
                policy.MinimumSupportedVersion);
        }

        return policy;
    }

    private UpdateStatus Publish(UpdateStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
        return status;
    }
}
