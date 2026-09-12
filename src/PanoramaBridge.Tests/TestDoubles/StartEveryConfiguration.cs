using PanoramaBridge.App.Services;
using PanoramaBridge.Core.Storage;

namespace PanoramaBridge.Tests.TestDoubles;

/// <summary>
/// Starting every configuration at once, which the service no longer offers.
/// </summary>
/// <remarks>
/// Each configuration has its own Run button now, so the service starts one at a time and there
/// is no whole-set start to call. Most of these tests are about what happens once several are
/// running rather than about how they were started, so they say this instead of each spelling out
/// the same loop.
/// </remarks>
internal static class StartEveryConfiguration
{
    public static async Task StartEveryConfigurationAsync(
        this TransferService service,
        AppSettings settings,
        string? secret,
        MonitoringConfiguration? edited = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(settings);

        foreach (var configuration in settings.Configurations)
        {
            await service
                .StartConfigurationAsync(settings, configuration, secret, edited)
                .ConfigureAwait(false);
        }
    }
}
