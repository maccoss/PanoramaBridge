using PanoramaBridge.App.ViewModels;
using PanoramaBridge.Core.Storage;

namespace PanoramaBridge.App.Services;

/// <summary>
/// Gives the Configurations list a Run button, by supplying what the transfer service needs.
/// </summary>
/// <remarks>
/// An adapter rather than the service implementing the interface itself. Starting a configuration
/// needs the application settings and the secret from the password box; the service takes both as
/// arguments and deliberately holds neither, and making it hold them so a list could call it with
/// one argument would be the tail wagging the dog.
/// </remarks>
public sealed class ConfigurationRunControl : IConfigurationRunControl
{
    private readonly TransferService _transfers;
    private readonly SettingsViewModel _settings;

    public ConfigurationRunControl(TransferService transfers, SettingsViewModel settings)
    {
        _transfers = transfers ?? throw new ArgumentNullException(nameof(transfers));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Supplies the secret from the view's password box on demand.
    /// </summary>
    /// <remarks>
    /// Set by the window, the same way <see cref="MainViewModel.SecretProvider"/> is, so the
    /// secret is never a property of anything that gets serialized, bound or logged.
    /// </remarks>
    public Func<string?>? SecretProvider { get; set; }

    /// <summary>Records or clears the credential as the person asked. Set by the window.</summary>
    public Action<AppSettings>? RememberCredential { get; set; }

    /// <inheritdoc />
    public bool IsRunning(MonitoringConfiguration configuration) =>
        _transfers.IsConfigurationRunning(configuration);

    /// <inheritdoc />
    public Task StartAsync(MonitoringConfiguration configuration)
    {
        var settings = _settings.ToSettings();

        // Before starting, because a configuration that cannot sign in is about to say so and the
        // credential the person just typed is what would have let it.
        RememberCredential?.Invoke(settings);

        return _transfers.StartConfigurationAsync(
            settings, configuration, SecretProvider?.Invoke(), _settings.Edited);
    }

    /// <inheritdoc />
    public Task StopAsync(MonitoringConfiguration configuration) =>
        _transfers.StopConfigurationAsync(configuration);

    /// <inheritdoc />
    public Task<int> ReconcileAsync() => _transfers.ReconcileAsync(_settings.ToSettings());
}
