using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PanoramaBridge.Core.Infrastructure;

/// <summary>
/// Turns a mapped drive letter back into the network path it stands for.
/// </summary>
/// <remarks>
/// <para>
/// A drive mapping belongs to one Windows sign-in. A path like <c>Y:\Instruments</c> resolves
/// for the person who created the mapping and for nobody else -- not for a service, not for a
/// scheduled task, and not for the same person after the mapping is dropped. Stored as
/// <c>\\server\share\Instruments</c> it resolves for all of them.
/// </para>
/// <para>
/// This matters because the folder picker returns whatever the user clicked, and someone
/// browsing to a network folder through This PC clicks the drive letter. Telling them in a hint
/// that the full path is preferred does not help if the only way to choose one is to type it,
/// so the drive letter is translated for them instead.
/// </para>
/// </remarks>
public static class NetworkPaths
{
    /// <summary>The drive is not a network mapping. Ordinary for C:.</summary>
    private const int ErrorNotConnected = 2250;

    private const int NoError = 0;
    private const int ErrorMoreData = 234;

    /// <summary>
    /// Returns <paramref name="path"/> with a mapped drive letter replaced by its network path.
    /// </summary>
    /// <remarks>
    /// Anything that is not a mapped drive -- a local disk, a path that is already a network
    /// path, a relative path, an empty string -- is returned exactly as given. This is a
    /// convenience, so it never fails: if the mapping cannot be resolved for any reason the
    /// original path is still perfectly usable by whoever is signed in now.
    /// </remarks>
    public static string ResolveMappedDrive(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !OperatingSystem.IsWindows())
        {
            return path ?? string.Empty;
        }

        // Already a network path, or not rooted at a drive letter at all.
        if (path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.Length < 2
            || path[1] != ':')
        {
            return path;
        }

        var share = TryGetNetworkPath(path[..2]);

        if (share is null)
        {
            return path;
        }

        var remainder = path[2..].TrimStart('\\', '/');

        return remainder.Length == 0
            ? share
            : $"{share.TrimEnd('\\')}\\{remainder}";
    }

    /// <summary>True when the path is a drive letter that stands for a network share.</summary>
    public static bool IsMappedDrive(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && OperatingSystem.IsWindows()
        && path.Length >= 2
        && path[1] == ':'
        && TryGetNetworkPath(path[..2]) is not null;

    [SupportedOSPlatform("windows")]
    private static string? TryGetNetworkPath(string drive)
    {
        var length = 512;
        var buffer = new StringBuilder(length);

        var result = WNetGetConnectionW(drive, buffer, ref length);

        if (result == ErrorMoreData)
        {
            buffer = new StringBuilder(length);
            result = WNetGetConnectionW(drive, buffer, ref length);
        }

        return result == NoError && buffer.Length > 0 ? buffer.ToString() : null;
    }

    /// <remarks>
    /// Returns <see cref="ErrorNotConnected"/> for a local disk, which is the common answer and
    /// is not an error worth reporting.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetGetConnectionW", SetLastError = true)]
    private static extern int WNetGetConnectionW(
        string localName,
        StringBuilder remoteName,
        ref int length);
}
