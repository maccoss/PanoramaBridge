namespace PanoramaBridge.Core.Infrastructure;

/// <summary>
/// Resolves every on-disk location the application uses.
/// </summary>
/// <remarks>
/// Everything lives under <c>%LOCALAPPDATA%\PanoramaBridge</c>. Local, deliberately, not
/// Roaming: the SQLite state database must never be synced across a domain profile.
/// <para>
/// The Python version wrote its log to a <em>relative</em> path, so a frozen executable
/// dropped <c>panoramabridge.log</c> into whatever directory it happened to be launched
/// from, and split its state between <c>~/.panoramabridge/config.json</c> and
/// <c>~/.panoramabridge_history.pkl</c>. One resolver keeps that from happening again.
/// </para>
/// </remarks>
public sealed class AppPaths
{
    public const string AppFolderName = "PanoramaBridge";

    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolderName);

        LogDirectory = Path.Combine(Root, "logs");
        SettingsFile = Path.Combine(Root, "settings.json");
        StateDatabase = Path.Combine(Root, "state.db");
    }

    /// <summary>The application data root.</summary>
    public string Root { get; }

    /// <summary>Directory holding rolling log files.</summary>
    public string LogDirectory { get; }

    /// <summary>Path template passed to the rolling file sink.</summary>
    public string LogFileTemplate => Path.Combine(LogDirectory, "panoramabridge-.log");

    /// <summary>User settings. Never contains credentials.</summary>
    public string SettingsFile { get; }

    /// <summary>Upload ledger, hash cache and queue state.</summary>
    public string StateDatabase { get; }

    /// <summary>Creates the directories that must exist before anything else runs.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
    }
}
