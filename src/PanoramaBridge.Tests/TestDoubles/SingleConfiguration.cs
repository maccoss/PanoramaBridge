using PanoramaBridge.Core.Storage;

namespace PanoramaBridge.Tests.TestDoubles;

/// <summary>
/// Reaching the one configuration a test set up.
/// </summary>
/// <remarks>
/// Most of these tests are about something other than configurations -- a view model, a sweep, a
/// credential -- and were written when there was only ever one pairing. They still mean "the
/// settings" in that sense, so this lets them keep saying it instead of indexing a list in every
/// assertion. A test that is genuinely about several configurations should address them directly.
/// </remarks>
internal static class SingleConfiguration
{
    /// <summary>Settings holding exactly this configuration.</summary>
    public static AppSettings Holding(
        this AppSettings settings,
        MonitoringConfiguration configuration) =>
        settings with { Configurations = [configuration] };

    /// <summary>
    /// The only configuration.
    /// </summary>
    /// <remarks>
    /// Throws rather than taking the first, so a test that grows a second configuration is told
    /// that its assertion has stopped meaning what it says.
    /// </remarks>
    public static MonitoringConfiguration OnlyConfiguration(this AppSettings settings) =>
        settings.Configurations.Count == 1
            ? settings.Configurations[0]
            : throw new InvalidOperationException(
                $"Expected one configuration, found {settings.Configurations.Count}.");
}
