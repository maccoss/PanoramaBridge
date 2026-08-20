using System.Reflection;
using System.Runtime.InteropServices;

namespace PanoramaBridge.Core.Infrastructure;

/// <summary>
/// Identity of the running build. The version is read from the assembly rather than
/// hardcoded -- the Python About dialog said "Version 1.0" while the package said 0.1.9rc4.
/// </summary>
public static class AppInfo
{
    /// <summary>Product name shown in the UI and used for the app data folder.</summary>
    public const string ProductName = "PanoramaBridge";

    /// <summary>Velopack package identifier. Must stay stable across releases.</summary>
    public const string PackageId = "MacCossLab.PanoramaBridge";

    /// <summary>Repository the update feed is served from.</summary>
    public const string RepositoryUrl = "https://github.com/maccoss/PanoramaBridge";

    /// <summary>
    /// Informational version, e.g. <c>1.4.0</c>. Any build metadata suffix that MSBuild
    /// appends (<c>+abc1234</c>) is trimmed off.
    /// </summary>
    public static string InformationalVersion { get; } = ResolveInformationalVersion();

    /// <summary>Parsed <see cref="InformationalVersion"/>, for comparison against an update floor.</summary>
    public static Version Version { get; } = ResolveVersion();

    /// <summary>Runtime identifier of this build, e.g. <c>win-x64</c>.</summary>
    public static string RuntimeIdentifier { get; } =
        $"win-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";

    /// <summary>
    /// Sent on every HTTP request. Lets Panorama administrators see which versions the lab is
    /// actually running, straight from the server access log, with no extra infrastructure.
    /// </summary>
    public static string UserAgent { get; } =
        $"{ProductName}/{InformationalVersion} ({RuntimeIdentifier})";

    private static string ResolveInformationalVersion()
    {
        var raw = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        var plus = raw.IndexOf('+');
        return plus >= 0 ? raw[..plus] : raw;
    }

    private static Version ResolveVersion()
    {
        var text = InformationalVersion;

        // Strip any prerelease suffix (1.2.0-beta.1) before parsing.
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            text = text[..dash];
        }

        return Version.TryParse(text, out var parsed) ? parsed : new Version(0, 0, 0);
    }
}
