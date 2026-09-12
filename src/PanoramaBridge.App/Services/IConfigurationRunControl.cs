using PanoramaBridge.Core.Storage;

namespace PanoramaBridge.App.Services;

/// <summary>
/// Starting and stopping one configuration, as the Configurations list needs it.
/// </summary>
/// <remarks>
/// Narrow on purpose. The list needs three answers and nothing else about transfers, and a list
/// that could reach the whole transfer service would be a list that could be tested only with a
/// server behind it.
/// </remarks>
public interface IConfigurationRunControl
{
    /// <summary>Whether this configuration is being watched right now.</summary>
    bool IsRunning(MonitoringConfiguration configuration);

    /// <summary>Starts watching it. Throws with a sentence to show when it cannot.</summary>
    Task StartAsync(MonitoringConfiguration configuration);

    /// <summary>Stops watching it. Does nothing when it was not running.</summary>
    Task StopAsync(MonitoringConfiguration configuration);
}
