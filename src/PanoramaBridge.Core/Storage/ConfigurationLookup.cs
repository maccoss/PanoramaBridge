namespace PanoramaBridge.Core.Storage;

/// <summary>
/// Which configuration a transferred file belongs to.
/// </summary>
/// <remarks>
/// <para>
/// Worked out from the paths rather than stored on the row. The ledger records where a file came
/// from and where it went, and that pair is what a configuration is; adding a column would mean a
/// schema migration to store something already derivable, and would go stale the moment somebody
/// renamed a configuration or repointed its destination.
/// </para>
/// <para>
/// Both halves have to match. Two configurations may watch the same folder and send it to
/// different Panorama projects -- that is the case the lab asked for, and the local path alone
/// cannot tell them apart.
/// </para>
/// </remarks>
public static class ConfigurationLookup
{
    /// <summary>
    /// The configuration a ledger row belongs to, or null when none does.
    /// </summary>
    /// <remarks>
    /// Null is ordinary rather than an error: a row survives the configuration that wrote it, and
    /// a file transferred last year by a configuration since deleted is still a true record of an
    /// upload. The caller shows a blank rather than inventing a name for it.
    /// </remarks>
    public static MonitoringConfiguration? For(
        IReadOnlyList<MonitoringConfiguration> configurations,
        string localPath,
        string remotePath)
    {
        ArgumentNullException.ThrowIfNull(configurations);

        if (string.IsNullOrEmpty(localPath))
        {
            return null;
        }

        MonitoringConfiguration? best = null;
        var bestLength = -1;

        foreach (var configuration in configurations)
        {
            if (!IsUnder(localPath, configuration.LocalDirectory)
                || !IsUnderRemote(remotePath, configuration.RemotePath))
            {
                continue;
            }

            // The most specific local root wins. One configuration watching D:\Data and another
            // watching D:\Data\QC both contain D:\Data\QC\run.raw, and the answer a person would
            // give is the one that named the folder the file is actually in.
            if (configuration.LocalDirectory.Length > bestLength)
            {
                best = configuration;
                bestLength = configuration.LocalDirectory.Length;
            }
        }

        return best;
    }

    /// <summary>What to show in a column for this row, or empty when nothing matches.</summary>
    public static string NameFor(
        IReadOnlyList<MonitoringConfiguration> configurations,
        string localPath,
        string remotePath) =>
        For(configurations, localPath, remotePath)?.DisplayName ?? string.Empty;

    /// <summary>
    /// Whether a local file sits inside a watched directory.
    /// </summary>
    /// <remarks>
    /// Case-insensitive, because Windows is, and boundary-aware: a plain prefix test would put a
    /// file in <c>D:\DataArchive</c> inside a configuration watching <c>D:\Data</c>.
    /// </remarks>
    private static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!path.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.Length == trimmed.Length
            || path[trimmed.Length] == Path.DirectorySeparatorChar
            || path[trimmed.Length] == Path.AltDirectorySeparatorChar;
    }

    /// <summary>
    /// Whether a destination sits inside a configuration's remote folder.
    /// </summary>
    /// <remarks>
    /// Compared exactly, unlike the local half: a WebDAV server need not be case-insensitive and
    /// Panorama is not. Boundary-aware for the same reason as the local side.
    /// </remarks>
    private static bool IsUnderRemote(string path, string root)
    {
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        if (string.IsNullOrEmpty(path))
        {
            // A failure recorded before a destination was known has an empty one. It belongs to
            // whichever configuration was watching the folder, so the local half decides.
            return true;
        }

        var trimmed = root.TrimEnd('/');

        if (!path.StartsWith(trimmed, StringComparison.Ordinal))
        {
            return false;
        }

        return path.Length == trimmed.Length || path[trimmed.Length] == '/';
    }
}
