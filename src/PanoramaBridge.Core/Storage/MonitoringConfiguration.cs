using System.Text.Json.Serialization;
using PanoramaBridge.Core.Transfer;

namespace PanoramaBridge.Core.Storage;

/// <summary>
/// One folder watched, and where it is sent.
/// </summary>
/// <remarks>
/// <para>
/// Everything the Local Monitoring and Remote Settings tabs edit. Several of these run at once,
/// each started and stopped on its own, which is what the lab asked for after using AutoQC.
/// </para>
/// <para>
/// What is <em>not</em> here is as deliberate as what is. The concurrency limit, the yield to
/// instrument software, the extra root certificate and the tray behavior all describe this
/// computer rather than any one pairing -- a TLS-inspecting proxy intercepts every server alike,
/// and a spinning disk is slow for every configuration at once. Those stay on
/// <see cref="AppSettings"/>, so raising or lowering them once does not mean doing it five times.
/// </para>
/// </remarks>
public sealed record MonitoringConfiguration
{
    /// <summary>
    /// Extensions the companion walk will not look past unless a user says otherwise.
    /// </summary>
    /// <remarks>
    /// Files another program derives from an acquisition and leaves beside it. They have exactly
    /// the same shape as a genuine companion -- <c>run.raw.skyd</c> is built the same way as
    /// <c>run.wiff.scan</c> -- so no rule about the shape of a name can tell them apart, and this
    /// has to be knowledge rather than logic. A default rather than a constant, because the next
    /// tool to write beside an acquisition should not need a release.
    /// <para>
    /// Nothing whose absence would be a safety failure belongs in here, because a user can empty
    /// it. <c>.tmp</c> was briefly in this list and is now one of
    /// <c>CandidateFilter.IsWorkingFile</c>'s own rules for exactly that reason.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> DefaultExcludedExtensions { get; } = [".skyd"];

    private readonly IReadOnlyList<string> _extensions =
        [".raw", ".d", ".wiff", ".wiff2", ".mzml", ".mzxml", ".sld", ".csv"];

    private readonly IReadOnlyList<string> _excludedExtensions = DefaultExcludedExtensions;

    // -- Identity -----------------------------------------------------------------------------

    /// <summary>What this pairing is called in the list.</summary>
    /// <remarks>
    /// The user's own words. Nothing keys off it, so renaming one is free -- see
    /// <see cref="Account"/> for the part that has to stay put.
    /// </remarks>
    public string Name { get; init; } = string.Empty;

    /// <summary>Whether this configuration runs when monitoring starts.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>What to call this in a list or a message when it has no name yet.</summary>
    /// <remarks>
    /// The folder, because that is what a user recognizes it by -- and it is what the migration
    /// from a single-configuration settings file names the first entry.
    /// </remarks>
    [JsonIgnore]
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Name) ? Name
        : !string.IsNullOrWhiteSpace(LocalDirectory) ? SuggestNameFor(LocalDirectory)
        : "Untitled";

    /// <summary>When it was added, for the list column.</summary>
    /// <remarks>
    /// Deliberately not defaulted to the current time. Whoever creates a configuration stamps it;
    /// a default of "now" would make two freshly constructed configurations compare unequal, and
    /// every "have the settings changed?" check in the UI is built on that comparison.
    /// </remarks>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>
    /// Which credential in Windows Credential Manager belongs to this configuration.
    /// </summary>
    /// <remarks>
    /// Stable and never shown. Credentials are keyed by server <em>and</em> account because two
    /// configurations may sign in to one server as different people, and an API key -- the
    /// recommended mode -- carries no user name to tell them apart.
    /// <para>
    /// Empty is meaningful: it resolves to the credential slot used before accounts existed. The
    /// configuration migrated from a single-configuration settings file keeps it empty and
    /// inherits the credential already on the machine, which is why that migration needs to touch
    /// Credential Manager at all.
    /// </para>
    /// </remarks>
    public string Account { get; init; } = string.Empty;

    // -- Local monitoring ---------------------------------------------------------------------

    /// <summary>Directory watched for new acquisitions.</summary>
    public string LocalDirectory { get; init; } = string.Empty;

    /// <summary>Whether to watch subdirectories as well.</summary>
    public bool IncludeSubdirectories { get; init; } = true;

    /// <summary>File extensions to transfer, with leading dots.</summary>
    /// <remarks>
    /// Null-coalescing on the way in, because a property initializer does not survive an explicit
    /// <c>null</c> in the JSON file -- and that file is meant to be hand-editable.
    /// </remarks>
    public IReadOnlyList<string> Extensions
    {
        get => _extensions;
        init => _extensions = value ?? [];
    }

    /// <summary>Extensions that are never data even when they sit on top of one that is.</summary>
    public IReadOnlyList<string> ExcludedExtensions
    {
        get => _excludedExtensions;
        init => _excludedExtensions = value ?? [];
    }

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

    /// <summary>What to do when a file we did not upload already occupies a destination.</summary>
    public ConflictPolicy ConflictPolicy { get; init; } = ConflictPolicy.Ask;

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
    public string RemotePath { get; init; } = AppSettings.MacCossFilesPath;

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

    // -- Behavior -----------------------------------------------------------------------------

    /// <summary>
    /// Value equality, including the list members.
    /// </summary>
    /// <remarks>
    /// The compiler-generated version compares the lists by reference, so two configurations
    /// holding identical values would compare unequal -- which quietly breaks every "have the
    /// settings changed?" check in the UI, prompting to save when nothing was edited.
    /// </remarks>
    public bool Equals(MonitoringConfiguration? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Name == other.Name
            && Enabled == other.Enabled
            && CreatedUtc == other.CreatedUtc
            && Account == other.Account
            && LocalDirectory == other.LocalDirectory
            && IncludeSubdirectories == other.IncludeSubdirectories
            && StabilitySeconds == other.StabilitySeconds
            && ReconcileMinutes == other.ReconcileMinutes
            && LockedFileRetryIntervalSeconds == other.LockedFileRetryIntervalSeconds
            && LockedFileMaxRetries == other.LockedFileMaxRetries
            && ConflictPolicy == other.ConflictPolicy
            && ServerUrl == other.ServerUrl
            && AuthMode == other.AuthMode
            && UserName == other.UserName
            && SaveCredentials == other.SaveCredentials
            && RemotePath == other.RemotePath
            && VerifyUploads == other.VerifyUploads
            && WriteChecksumSidecars == other.WriteChecksumSidecars
            && Extensions.SequenceEqual(other.Extensions, StringComparer.Ordinal)
            && ExcludedExtensions.SequenceEqual(other.ExcludedExtensions, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();

        hash.Add(Name);
        hash.Add(Enabled);
        hash.Add(CreatedUtc);
        hash.Add(Account);
        hash.Add(LocalDirectory);
        hash.Add(IncludeSubdirectories);
        hash.Add(StabilitySeconds);
        hash.Add(ReconcileMinutes);
        hash.Add(ConflictPolicy);
        hash.Add(ServerUrl);
        hash.Add(AuthMode);
        hash.Add(UserName);
        hash.Add(RemotePath);

        foreach (var extension in Extensions)
        {
            hash.Add(extension, StringComparer.Ordinal);
        }

        foreach (var extension in ExcludedExtensions)
        {
            hash.Add(extension, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Replaces persisted values for withdrawn settings with their safe current meaning.
    /// </summary>
    /// <remarks>
    /// <see cref="ConflictPolicy.Rename"/> remains in the enum solely so an older JSON settings
    /// file can be read. The current UI has no radio button for it and every transfer path already
    /// treats it as <see cref="ConflictPolicy.Ask"/>, so carrying it forward would make the file
    /// say something the application cannot do.
    /// </remarks>
    public MonitoringConfiguration NormalizeWithdrawnValues() =>
        ConflictPolicy == ConflictPolicy.Rename ? this with { ConflictPolicy = ConflictPolicy.Ask } : this;

    /// <summary>Renders <see cref="Extensions"/> for display in a single text box.</summary>
    public string FormatExtensions() => string.Join(", ", Extensions);

    /// <summary>Renders <see cref="ExcludedExtensions"/> for display in a single text box.</summary>
    public string FormatExcludedExtensions() => string.Join(", ", ExcludedExtensions);

    /// <summary>
    /// Problems that would stop this configuration transferring, phrased for the person who has
    /// to fix them.
    /// </summary>
    /// <param name="label">
    /// How to refer to this configuration in a message. With several of them running, "Choose a
    /// directory to monitor" does not say which one needs it.
    /// </param>
    public IReadOnlyList<string> Validate(string? label = null)
    {
        var problems = new List<string>();
        var prefix = string.IsNullOrWhiteSpace(label) ? string.Empty : $"{label}: ";

        if (string.IsNullOrWhiteSpace(LocalDirectory))
        {
            problems.Add($"{prefix}Choose a directory to monitor on the Local Monitoring tab.");
        }
        else if (!Directory.Exists(LocalDirectory))
        {
            problems.Add($"{prefix}The monitored directory does not exist: {LocalDirectory}");
        }

        if (Extensions.Count == 0)
        {
            problems.Add($"{prefix}List at least one file extension to transfer.");
        }

        // Excluding a suffix that a listed format needs is the 38 MB-of-13.7 GB truncation all
        // over again, arrived at by configuration instead of by a bug: the .wiff uploads and
        // records as verified while the spectra in the .wiff.scan stay behind, and nothing looks
        // wrong until somebody opens it in Skyline.
        if (Extensions.Any(e => e is ".wiff" or ".wiff2")
            && ExcludedExtensions.Contains(".scan", StringComparer.OrdinalIgnoreCase))
        {
            problems.Add(
                $"{prefix}Remove .scan from the never-transfer list, or stop transferring .wiff "
                + "files. A Sciex acquisition keeps its spectra in the .wiff.scan beside the "
                + ".wiff, so excluding it would upload the metadata on its own.");
        }

        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var server)
            || (server.Scheme != Uri.UriSchemeHttps && server.Scheme != Uri.UriSchemeHttp))
        {
            problems.Add($"{prefix}The server address is not a valid URL: {ServerUrl}");
        }

        if (string.IsNullOrWhiteSpace(RemotePath))
        {
            problems.Add($"{prefix}Choose a remote folder to upload into on the Remote Settings tab.");
        }

        if (AuthMode == AuthMode.UserNameAndPassword && string.IsNullOrWhiteSpace(UserName))
        {
            problems.Add($"{prefix}Enter your Panorama user name, or switch to an API key.");
        }

        return problems;
    }

    /// <summary>
    /// A name for a configuration watching <paramref name="localDirectory"/>.
    /// </summary>
    /// <remarks>
    /// The last segment of the path, which is how people refer to these folders out loud.
    /// <para>
    /// Deliberately not <see cref="Path.GetFileName(string)"/>, which has no name for a root and
    /// treats a UNC share root as one: it returns nothing at all for
    /// <c>\\fileserver\instruments</c>, even though "instruments" is exactly what the lab calls
    /// that share -- and a share root is a perfectly ordinary thing to point a configuration at.
    /// </para>
    /// </remarks>
    public static string SuggestNameFor(string localDirectory)
    {
        if (string.IsNullOrWhiteSpace(localDirectory))
        {
            return "Untitled";
        }

        var segments = localDirectory.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        return segments.Length > 0 ? segments[^1] : localDirectory;
    }
}
