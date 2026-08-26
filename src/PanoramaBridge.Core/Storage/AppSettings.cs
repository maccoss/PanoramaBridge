using System.Text.Json.Serialization;
using PanoramaBridge.Core.Transfer;

namespace PanoramaBridge.Core.Storage;

/// <summary>How the application authenticates to Panorama.</summary>
public enum AuthMode
{
    /// <summary>A LabKey API key. Revocable, role-restrictable, and expires server-side.</summary>
    ApiKey = 0,

    /// <summary>A Panorama account user name and password.</summary>
    UserNameAndPassword = 1,
}

/// <summary>
/// Everything the user can configure.
/// </summary>
/// <remarks>
/// Contains no secrets. The API key and password live in Windows Credential Manager, so this
/// file can be read, copied or attached to a support request without leaking anything.
/// </remarks>
public sealed record AppSettings
{
    /// <summary>The lab's usual destination, offered as the default.</summary>
    public const string MacCossFilesPath = "/_webdav/MacCoss/maccoss/@files/";

    // -- Local monitoring ---------------------------------------------------------------------

    /// <summary>Directory watched for new acquisitions.</summary>
    public string LocalDirectory { get; init; } = string.Empty;

    /// <summary>Whether to watch subdirectories as well.</summary>
    public bool IncludeSubdirectories { get; init; } = true;

    /// <summary>File extensions to transfer, with leading dots.</summary>
    public IReadOnlyList<string> Extensions { get; init; } =
        [".raw", ".d", ".wiff", ".wiff2", ".mzml", ".mzxml", ".sld", ".csv"];

    /// <summary>
    /// How long a file must be unchanged before it is considered finished.
    /// </summary>
    /// <remarks>
    /// Ten seconds by default. This setting existed in the Python UI but nothing ever read it;
    /// the stability window was hardcoded to one second.
    /// </remarks>
    public int StabilitySeconds { get; init; } = 10;

    /// <summary>
    /// How often to re-walk the watched tree.
    /// </summary>
    /// <remarks>
    /// Always on, not optional. File system notifications are a hint, not a guarantee -- they
    /// are dropped on buffer overflow and are unreliable over SMB and in WSL2 -- so a periodic
    /// sweep is the actual safety net rather than a checkbox someone has to know to tick.
    /// </remarks>
    public int ReconcileMinutes { get; init; } = 15;

    // -- Locked files -------------------------------------------------------------------------

    /// <summary>
    /// How often to look again at a file another process is holding open.
    /// </summary>
    /// <remarks>
    /// Thirty seconds. There was once a companion setting that waited half an hour before the
    /// first re-check, on the reasoning that an instrument holds its output open for the whole
    /// run. It was removed: there is no way to learn that a file has been released except by
    /// looking, so not looking simply means the file sits there after it finishes. What the long
    /// wait saved was two file opens per thirty seconds.
    /// </remarks>
    public int LockedFileRetryIntervalSeconds { get; init; } = 30;

    /// <summary>
    /// How many consecutive checks may find a file in use before it stops being watched closely.
    /// </summary>
    /// <remarks>
    /// Not an abandonment. The file goes back to the periodic folder check, which offers it again
    /// on its next pass, so a run lasting all afternoon is still transferred when it finishes.
    /// </remarks>
    public int LockedFileMaxRetries { get; init; } = 20;

    // -- Transfers ----------------------------------------------------------------------------

    /// <summary>
    /// Files uploaded at once.
    /// </summary>
    /// <remarks>
    /// Three or four connections saturate a gigabit link to a single server. Lower it to one or
    /// two when the watched volume is a spinning disk, where concurrent sequential reads turn
    /// into seeking.
    /// </remarks>
    public int MaxConcurrentTransfers { get; init; } = 3;

    /// <summary>What to do when a file we did not upload already occupies a destination.</summary>
    public ConflictPolicy ConflictPolicy { get; init; } = ConflictPolicy.Ask;

    /// <summary>
    /// Whether folders such as a Bruker <c>.d</c> are sent as one archive. Off by default.
    /// </summary>
    /// <remarks>
    /// Opt-in because it cannot be verified here. This lab runs Thermo instruments only, so every
    /// real directory acquisition runs somewhere nobody here can reproduce — and the paths
    /// around it have proved the least reliable part of the application. Someone who has this
    /// kind of instrument can turn it on knowingly; nobody gets it by default.
    /// </remarks>
    public bool FolderAcquisitions { get; init; }

    /// <summary>Whether to confirm every upload against the server's own hash.</summary>
    public bool VerifyUploads { get; init; } = true;

    /// <summary>
    /// Whether to write a <c>.md5</c> file beside each uploaded file on the server.
    /// </summary>
    /// <remarks>
    /// On by default. It is the only record of the file's checksum that travels with the data:
    /// the upload ledger lives on one instrument computer, and Panorama stamps an uploaded file
    /// with the time it arrived rather than the time the instrument wrote it, so the acquisition
    /// date survives only if something writes it down.
    /// </remarks>
    public bool WriteChecksumSidecars { get; init; } = true;

    /// <summary>
    /// Whether to stay out of the way of instrument software.
    /// </summary>
    /// <remarks>
    /// On by default, because the usual home for this application is the computer attached to a
    /// mass spectrometer. Lowers processor and disk priority so an acquisition always wins;
    /// transfers then take longer on a busy machine, which is the correct trade. Turn it off on a
    /// workstation being used for bulk uploads, where nothing else needs the machine.
    /// </remarks>
    public bool YieldToInstrumentSoftware { get; init; } = true;

    /// <summary>
    /// Whether to record a SHA-256 alongside the MD5.
    /// </summary>
    /// <remarks>
    /// Off by default. MD5 is what Panorama reports, so it is the only hash that can be checked
    /// against what the server actually stored; a second digest doubles the processor cost of
    /// every transfer for a value nothing verifies. Worth enabling only where a stronger
    /// provenance record is specifically wanted.
    /// </remarks>
    public bool RecordSha256 { get; init; }

    // -- Remote -------------------------------------------------------------------------------

    /// <summary>Panorama server address.</summary>
    public string ServerUrl { get; init; } = "https://panoramaweb.org";

    /// <summary>Which credential type to use.</summary>
    public AuthMode AuthMode { get; init; } = AuthMode.ApiKey;

    /// <summary>Account name, when using a password. Never the secret itself.</summary>
    public string UserName { get; init; } = string.Empty;

    /// <summary>Whether the credential is kept in Windows Credential Manager between sessions.</summary>
    public bool SaveCredentials { get; init; } = true;

    /// <summary>Remote folder uploads are mirrored into.</summary>
    public string RemotePath { get; init; } = MacCossFilesPath;

    /// <summary>Recently used destinations, most recent first.</summary>
    public IReadOnlyList<string> RecentRemotePaths { get; init; } = [MacCossFilesPath];

    /// <summary>
    /// An extra trusted root certificate, for a site behind a TLS-inspecting proxy.
    /// </summary>
    /// <remarks>
    /// Additive: the chain is still validated. There is deliberately no setting that disables
    /// certificate checking.
    /// </remarks>
    public string? TrustedRootCertificatePath { get; init; }

    // -- Application ---------------------------------------------------------------------------

    /// <summary>Whether to log at debug level.</summary>
    public bool VerboseLogging { get; init; }

    /// <summary>Whether closing the window leaves the application running in the tray.</summary>
    public bool MinimizeToTray { get; init; } = true;


    /// <summary>Schema marker, so a future format change can be recognised.</summary>
    [JsonPropertyName("$version")]
    public int Version { get; init; } = 1;

    /// <summary>
    /// Value equality, including the list members.
    /// </summary>
    /// <remarks>
    /// The compiler-generated version compares <see cref="Extensions"/> and
    /// <see cref="RecentRemotePaths"/> by reference, so two settings objects holding identical
    /// values would compare unequal. That would quietly break every "have the settings changed?"
    /// check in the UI, prompting to save when nothing was edited.
    /// </remarks>
    public bool Equals(AppSettings? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return LocalDirectory == other.LocalDirectory
            && IncludeSubdirectories == other.IncludeSubdirectories
            && StabilitySeconds == other.StabilitySeconds
            && ReconcileMinutes == other.ReconcileMinutes
            && LockedFileRetryIntervalSeconds == other.LockedFileRetryIntervalSeconds
            && LockedFileMaxRetries == other.LockedFileMaxRetries
            && MaxConcurrentTransfers == other.MaxConcurrentTransfers
            && ConflictPolicy == other.ConflictPolicy
            && FolderAcquisitions == other.FolderAcquisitions
            && VerifyUploads == other.VerifyUploads
            && WriteChecksumSidecars == other.WriteChecksumSidecars
            && YieldToInstrumentSoftware == other.YieldToInstrumentSoftware
            && RecordSha256 == other.RecordSha256
            && ServerUrl == other.ServerUrl
            && AuthMode == other.AuthMode
            && UserName == other.UserName
            && SaveCredentials == other.SaveCredentials
            && RemotePath == other.RemotePath
            && TrustedRootCertificatePath == other.TrustedRootCertificatePath
            && VerboseLogging == other.VerboseLogging
            && MinimizeToTray == other.MinimizeToTray
            && Version == other.Version
            && Extensions.SequenceEqual(other.Extensions, StringComparer.Ordinal)
            && RecentRemotePaths.SequenceEqual(other.RecentRemotePaths, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();

        hash.Add(LocalDirectory);
        hash.Add(IncludeSubdirectories);
        hash.Add(StabilitySeconds);
        hash.Add(ReconcileMinutes);
        hash.Add(MaxConcurrentTransfers);
        hash.Add(ConflictPolicy);
        hash.Add(FolderAcquisitions);
        hash.Add(VerifyUploads);
        hash.Add(WriteChecksumSidecars);
        hash.Add(YieldToInstrumentSoftware);
        hash.Add(RecordSha256);
        hash.Add(ServerUrl);
        hash.Add(AuthMode);
        hash.Add(UserName);
        hash.Add(RemotePath);
        hash.Add(Version);

        foreach (var extension in Extensions)
        {
            hash.Add(extension, StringComparer.Ordinal);
        }

        foreach (var path in RecentRemotePaths)
        {
            hash.Add(path, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Parses <see cref="Extensions"/> from the comma-separated form the UI shows, normalising
    /// each entry to a lower-case leading-dot extension.
    /// </summary>
    public static IReadOnlyList<string> ParseExtensions(string commaSeparated)
    {
        ArgumentNullException.ThrowIfNull(commaSeparated);

        return commaSeparated
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .Select(e => e.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Renders <see cref="Extensions"/> for display in a single text box.</summary>
    public string FormatExtensions() => string.Join(", ", Extensions);

    /// <summary>
    /// Returns these settings with <paramref name="path"/> promoted to the front of the recent
    /// list, keeping the lab's default available.
    /// </summary>
    public AppSettings WithRecentPath(string path, int keep = 8)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return this;
        }

        var recent = new List<string> { path };
        recent.AddRange(RecentRemotePaths.Where(p =>
            !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));

        if (!recent.Contains(MacCossFilesPath, StringComparer.OrdinalIgnoreCase))
        {
            recent.Add(MacCossFilesPath);
        }

        return this with { RemotePath = path, RecentRemotePaths = recent.Take(keep).ToArray() };
    }

    /// <summary>
    /// Problems that would stop a transfer, phrased for the person who has to fix them.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(LocalDirectory))
        {
            problems.Add("Choose a directory to monitor on the Local Monitoring tab.");
        }
        else if (!Directory.Exists(LocalDirectory))
        {
            problems.Add($"The monitored directory does not exist: {LocalDirectory}");
        }

        if (Extensions.Count == 0)
        {
            problems.Add("List at least one file extension to transfer.");
        }

        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var server)
            || (server.Scheme != Uri.UriSchemeHttps && server.Scheme != Uri.UriSchemeHttp))
        {
            problems.Add($"The server address is not a valid URL: {ServerUrl}");
        }

        if (string.IsNullOrWhiteSpace(RemotePath))
        {
            problems.Add("Choose a remote folder to upload into on the Remote Settings tab.");
        }

        if (AuthMode == AuthMode.UserNameAndPassword && string.IsNullOrWhiteSpace(UserName))
        {
            problems.Add("Enter your Panorama user name, or switch to an API key.");
        }

        if (MaxConcurrentTransfers is < 1 or > 8)
        {
            problems.Add("Concurrent transfers must be between 1 and 8.");
        }

        return problems;
    }
}
