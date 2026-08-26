using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PanoramaBridge.Core.Infrastructure;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;

namespace PanoramaBridge.App.ViewModels;

/// <summary>
/// Backs the Local Monitoring and Remote Settings tabs.
/// </summary>
/// <remarks>
/// One view model for both because they edit one <see cref="AppSettings"/> record, and splitting
/// it would mean two objects fighting over which of them owns the saved state.
/// <para>
/// The secret is deliberately not a property here. It is held by the view's password box and
/// passed explicitly when needed, so it is never part of anything that gets serialized, bound,
/// or written to a log by a careless diagnostic.
/// </para>
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>Where a Panorama API key is generated.</summary>
    public const string ApiKeyHelpUrl = "https://panoramaweb.org/login-createApiKey.view";

    private readonly ISettingsStore _store;
    private AppSettings _saved;

    public SettingsViewModel(ISettingsStore store, AppSettings initial)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _saved = (initial ?? throw new ArgumentNullException(nameof(initial)))
            .NormalizeWithdrawnValues();

        LoadFrom(_saved);
    }

    // -- Local monitoring ---------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private string _localDirectory = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private bool _includeSubdirectories = true;

    /// <summary>Extensions as the comma-separated text the user edits.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private string _extensionsText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private int _stabilitySeconds = 10;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private int _reconcileMinutes = 15;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private int _lockedFileRetryIntervalSeconds = 30;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private int _lockedFileMaxRetries = 20;


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private ConflictPolicy _conflictPolicy = ConflictPolicy.Ask;

    // -- Transfers -----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    [NotifyPropertyChangedFor(nameof(ConcurrencyAdvice))]
    private int _maxConcurrentTransfers = 3;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private bool _verifyUploads = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private bool _writeChecksumSidecars = true;

    /// <summary>
    /// Guidance shown beside the concurrency slider.
    /// </summary>
    /// <remarks>
    /// Worth saying out loud because the intuition is wrong: on a spinning disk, more concurrent
    /// transfers is slower, since several sequential reads at once become seeking.
    /// </remarks>
    public string ConcurrencyAdvice => MaxConcurrentTransfers switch
    {
        1 => "One at a time. Safest for a spinning disk or a slow network share.",
        2 or 3 or 4 => "A good default. Enough to keep a fast link busy.",
        _ => "High. Only worth it on an SSD with a fast link; a spinning disk will be slower.",
    };

    // -- Remote --------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private string _serverUrl = "https://panoramaweb.org";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    [NotifyPropertyChangedFor(nameof(IsApiKey))]
    [NotifyPropertyChangedFor(nameof(SecretLabel))]
    private AuthMode _authMode = AuthMode.ApiKey;

    /// <summary>True when the API key fields should be shown instead of user name and password.</summary>
    public bool IsApiKey => AuthMode == AuthMode.ApiKey;

    /// <summary>Label for the secret box, so it reads correctly in either mode.</summary>
    public string SecretLabel => IsApiKey ? "API key" : "Password";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private string _userName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private bool _saveCredentials = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private string _remotePath = AppSettings.MacCossFilesPath;

    /// <summary>Recent destinations, so the common one is a click rather than a recollection.</summary>
    public ObservableCollection<string> RecentRemotePaths { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private string? _trustedRootCertificatePath;

    // -- Application ---------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private bool _verboseLogging;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private bool _minimizeToTray = true;

    /// <summary>Whether anything has been edited since the last save.</summary>
    public bool HasUnsavedChanges => ToSettings() != _saved;

    /// <summary>Problems that would prevent a transfer, or empty when the settings are usable.</summary>
    public IReadOnlyList<string> Problems => ToSettings().Validate();

    /// <summary>The current edits as a settings record.</summary>
    public AppSettings ToSettings() => _saved with
    {
        LocalDirectory = LocalDirectory,
        IncludeSubdirectories = IncludeSubdirectories,
        Extensions = AppSettings.ParseExtensions(ExtensionsText),
        StabilitySeconds = StabilitySeconds,
        ReconcileMinutes = ReconcileMinutes,
        LockedFileRetryIntervalSeconds = LockedFileRetryIntervalSeconds,
        LockedFileMaxRetries = LockedFileMaxRetries,
        MaxConcurrentTransfers = MaxConcurrentTransfers,
        ConflictPolicy = ConflictPolicy,
        VerifyUploads = VerifyUploads,
        WriteChecksumSidecars = WriteChecksumSidecars,
        ServerUrl = ServerUrl.Trim(),
        AuthMode = AuthMode,
        UserName = UserName.Trim(),
        SaveCredentials = SaveCredentials,
        RemotePath = RemotePath.Trim(),
        TrustedRootCertificatePath = string.IsNullOrWhiteSpace(TrustedRootCertificatePath)
            ? null
            : TrustedRootCertificatePath,
        VerboseLogging = VerboseLogging,
        MinimizeToTray = MinimizeToTray,
    };

    /// <summary>Persists the current edits.</summary>
    public async Task<AppSettings> SaveAsync(CancellationToken cancellationToken = default)
    {
        // Also applied here, so a path typed by hand is stored in the durable form too. Done at
        // the point of saving rather than as the box is edited, which would fight the typing.
        LocalDirectory = NetworkPaths.ResolveMappedDrive(LocalDirectory);

        var settings = ToSettings().WithRecentPath(RemotePath.Trim());

        await _store.SaveAsync(settings, cancellationToken).ConfigureAwait(true);

        _saved = settings;
        LoadFrom(settings);
        OnPropertyChanged(nameof(HasUnsavedChanges));

        // Immediately, because someone turning this on is trying to capture something that is
        // happening now.
        Services.LoggingSetup.ApplyVerbosity(settings.VerboseLogging);

        return settings;
    }

    [RelayCommand]
    private void BrowseLocalDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the directory to monitor",
            InitialDirectory = Directory.Exists(LocalDirectory) ? LocalDirectory : null,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // Browsing to a network folder through This PC returns the drive letter that was
        // clicked, and a drive letter belongs to one Windows sign-in. Translating it here is the
        // only way the advice to prefer the full network path is actually followable.
        LocalDirectory = NetworkPaths.ResolveMappedDrive(dialog.FolderName);
    }

    [RelayCommand]
    private void BrowseCertificate()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose an additional trusted root certificate",
            Filter = "Certificates (*.cer;*.crt;*.pem)|*.cer;*.crt;*.pem|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog() == true)
        {
            TrustedRootCertificatePath = dialog.FileName;
        }
    }

    [RelayCommand]
    private void RestoreDefaultExtensions() =>
        ExtensionsText = new AppSettings().FormatExtensions();

    [RelayCommand]
    private static void OpenApiKeyPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ApiKeyHelpUrl) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Opening a browser is a convenience; failing to do so is not worth an error dialog.
        }
    }

    private void LoadFrom(AppSettings settings)
    {
        LocalDirectory = settings.LocalDirectory;
        IncludeSubdirectories = settings.IncludeSubdirectories;
        ExtensionsText = settings.FormatExtensions();
        StabilitySeconds = settings.StabilitySeconds;
        ReconcileMinutes = settings.ReconcileMinutes;
        LockedFileRetryIntervalSeconds = settings.LockedFileRetryIntervalSeconds;
        LockedFileMaxRetries = settings.LockedFileMaxRetries;
        MaxConcurrentTransfers = settings.MaxConcurrentTransfers;
        ConflictPolicy = settings.ConflictPolicy;
        VerifyUploads = settings.VerifyUploads;
        WriteChecksumSidecars = settings.WriteChecksumSidecars;
        ServerUrl = settings.ServerUrl;
        AuthMode = settings.AuthMode;
        UserName = settings.UserName;
        SaveCredentials = settings.SaveCredentials;
        RemotePath = settings.RemotePath;
        TrustedRootCertificatePath = settings.TrustedRootCertificatePath;
        VerboseLogging = settings.VerboseLogging;
        MinimizeToTray = settings.MinimizeToTray;

        RecentRemotePaths.Clear();
        foreach (var path in settings.RecentRemotePaths)
        {
            RecentRemotePaths.Add(path);
        }
    }
}
