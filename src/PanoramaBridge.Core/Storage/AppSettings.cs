using System.Text.Json.Serialization;

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
/// Everything the user can configure: the application's own behavior, and the configurations it
/// runs.
/// </summary>
/// <remarks>
/// <para>
/// Contains no secrets. The API key and password live in Windows Credential Manager, so this
/// file can be read, copied or attached to a support request without leaking anything.
/// </para>
/// <para>
/// The division is by what a value describes. A watched folder, a destination and a sign-in
/// describe one pairing, and there are several of those -- they live on
/// <see cref="MonitoringConfiguration"/>. The concurrency limit, the yield to instrument software
/// and the extra root certificate describe this computer, so they are here: a TLS-inspecting
/// proxy intercepts every server alike, and a spinning disk is slow for every configuration at
/// once.
/// </para>
/// </remarks>
public sealed record AppSettings
{
    /// <summary>The lab's usual destination, offered as the default.</summary>
    public const string MacCossFilesPath = "/_webdav/MacCoss/maccoss/@files/";

    /// <summary>
    /// The current settings file format.
    /// </summary>
    /// <remarks>
    /// 1 was a single configuration with its fields at the top level. 2 moved them into
    /// <see cref="Configurations"/>. See <c>JsonSettingsStore</c> for the upgrade.
    /// </remarks>
    public const int CurrentVersion = 2;

    /// <summary>
    /// One configuration, so a fresh install has something for the settings tabs to edit.
    /// </summary>
    /// <remarks>
    /// Shared rather than constructed per instance, which is safe because the record is immutable
    /// and matters because two default <see cref="AppSettings"/> have to compare equal.
    /// </remarks>
    private static readonly IReadOnlyList<MonitoringConfiguration> OneEmptyConfiguration =
        [new MonitoringConfiguration()];

    private readonly IReadOnlyList<MonitoringConfiguration> _configurations = OneEmptyConfiguration;

    private readonly IReadOnlyList<string> _recentRemotePaths = [MacCossFilesPath];

    // -- Configurations -------------------------------------------------------------------------

    /// <summary>
    /// The folder-to-destination pairings, in the order the user arranged them.
    /// </summary>
    /// <remarks>
    /// Ordered rather than keyed by name, because the name is the user's own label and renaming
    /// one must not be a schema operation. Null-coalescing on the way in for the reason given on
    /// <see cref="RecentRemotePaths"/>; an explicit empty list is allowed, and means the
    /// Configurations tab is showing its empty state.
    /// </remarks>
    public IReadOnlyList<MonitoringConfiguration> Configurations
    {
        get => _configurations;
        init => _configurations = value ?? [];
    }

    // -- Transfers ------------------------------------------------------------------------------

    /// <summary>
    /// Files uploaded at once, across every configuration.
    /// </summary>
    /// <remarks>
    /// Three or four connections saturate a gigabit link to a single server. Lower it to one or
    /// two when the watched volume is a spinning disk, where concurrent sequential reads turn
    /// into seeking.
    /// <para>
    /// Shared rather than applied per configuration, and that is the whole reason it is not a
    /// configuration setting: eight configurations each allowed three transfers would be
    /// twenty-four concurrent reads from one disk, which on a spinning volume is slower than
    /// three -- the application says so itself in <c>ConcurrencyAdvice</c>.
    /// </para>
    /// </remarks>
    public int MaxConcurrentTransfers { get; init; } = 3;

    /// <summary>
    /// Whether to stay out of the way of instrument software.
    /// </summary>
    /// <remarks>
    /// On by default, because the usual home for this application is the computer attached to a
    /// mass spectrometer. Lowers processor and disk priority so an acquisition always wins;
    /// transfers then take longer on a busy machine, which is the correct trade. Turn it off on a
    /// workstation being used for bulk uploads, where nothing else needs the machine.
    /// <para>
    /// A property of the process, not of any one configuration: priority is set once for the
    /// whole application.
    /// </para>
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

    /// <summary>Recently used destinations, most recent first.</summary>
    /// <remarks>
    /// One list across every configuration, because it exists to save typing in the destination
    /// box and a path used by one configuration is exactly the kind of thing the next one wants
    /// to start from.
    /// </remarks>
    public IReadOnlyList<string> RecentRemotePaths
    {
        get => _recentRemotePaths;
        init => _recentRemotePaths = value ?? [];
    }

    /// <summary>
    /// An extra trusted root certificate, for a site behind a TLS-inspecting proxy.
    /// </summary>
    /// <remarks>
    /// Additive: the chain is still validated. There is deliberately no setting that disables
    /// certificate checking.
    /// <para>
    /// Application-level, because a proxy that inspects TLS intercepts everything leaving the
    /// machine. Naming the certificate once per configuration would be five places to fix when
    /// the site's root is replaced.
    /// </para>
    /// </remarks>
    public string? TrustedRootCertificatePath { get; init; }

    // -- Application ---------------------------------------------------------------------------

    /// <summary>Whether to log at debug level.</summary>
    public bool VerboseLogging { get; init; }

    /// <summary>Whether closing the window leaves the application running in the tray.</summary>
    public bool MinimizeToTray { get; init; } = true;

    /// <summary>Schema marker, so a future format change can be recognized.</summary>
    [JsonPropertyName("$version")]
    public int Version { get; init; } = CurrentVersion;

    /// <summary>
    /// The configurations that are complete enough to run.
    /// </summary>
    /// <remarks>
    /// Not a stored flag any more. Each configuration has its own Run button and nothing starts
    /// by itself, so what matters about one is whether it could work rather than whether somebody
    /// ticked it. A half-filled configuration is simply skipped by anything that acts on all of
    /// them, instead of making the whole set refuse -- which is what a single invalid one used to
    /// do.
    /// </remarks>
    [JsonIgnore]
    public IEnumerable<MonitoringConfiguration> UsableConfigurations =>
        Configurations.Where(c => c.Validate().Count == 0);

    /// <summary>
    /// Value equality, including the list members.
    /// </summary>
    /// <remarks>
    /// The compiler-generated version compares <see cref="Configurations"/> and
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

        return MaxConcurrentTransfers == other.MaxConcurrentTransfers
            && YieldToInstrumentSoftware == other.YieldToInstrumentSoftware
            && RecordSha256 == other.RecordSha256
            && TrustedRootCertificatePath == other.TrustedRootCertificatePath
            && VerboseLogging == other.VerboseLogging
            && MinimizeToTray == other.MinimizeToTray
            && Version == other.Version
            && Configurations.SequenceEqual(other.Configurations)
            && RecentRemotePaths.SequenceEqual(other.RecentRemotePaths, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();

        hash.Add(MaxConcurrentTransfers);
        hash.Add(YieldToInstrumentSoftware);
        hash.Add(RecordSha256);
        hash.Add(TrustedRootCertificatePath);
        hash.Add(VerboseLogging);
        hash.Add(MinimizeToTray);
        hash.Add(Version);

        foreach (var configuration in Configurations)
        {
            hash.Add(configuration);
        }

        foreach (var path in RecentRemotePaths)
        {
            hash.Add(path, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Replaces persisted values for withdrawn settings with their safe current meaning.
    /// </summary>
    /// <remarks>
    /// Returns <c>this</c> unchanged when nothing needed normalizing, which the settings store
    /// relies on to decide whether to rewrite the file.
    /// </remarks>
    public AppSettings NormalizeWithdrawnValues()
    {
        MonitoringConfiguration[]? normalized = null;

        for (var i = 0; i < Configurations.Count; i++)
        {
            var configuration = Configurations[i].NormalizeWithdrawnValues();

            if (ReferenceEquals(configuration, Configurations[i]))
            {
                continue;
            }

            normalized ??= Configurations.ToArray();
            normalized[i] = configuration;
        }

        return normalized is null ? this : this with { Configurations = normalized };
    }

    /// <summary>
    /// Parses a list of extensions from the comma-separated form the UI shows, normalizing each
    /// entry to a lower-case leading-dot extension.
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

    /// <summary>
    /// Returns these settings with <paramref name="path"/> promoted to the front of the recent
    /// list, keeping the lab's default available.
    /// </summary>
    /// <remarks>
    /// Records the path only. Which destination a configuration uses is that configuration's own
    /// <see cref="MonitoringConfiguration.RemotePath"/>; before configurations existed this method
    /// set both, and doing that now would silently re-point whichever one happened to be first.
    /// </remarks>
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

        return this with { RecentRemotePaths = recent.Take(keep).ToArray() };
    }

    /// <summary>
    /// Problems that would stop a transfer, phrased for the person who has to fix them.
    /// </summary>
    /// <remarks>
    /// Only what is true of the whole file. Whether any one configuration can run is that
    /// configuration's own question, asked by <see cref="MonitoringConfiguration.Validate"/> when
    /// its Run button is pressed -- because a configuration nobody has filled in yet must not be
    /// able to stop the others, and as a whole-settings check it could.
    /// </remarks>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (Configurations.Count == 0)
        {
            problems.Add(
                "Add a configuration: a folder to monitor and a Panorama folder to send it to.");
        }

        if (ValidateConcurrency(MaxConcurrentTransfers) is { } limit)
        {
            problems.Add(limit);
        }

        return problems;
    }

    /// <summary>
    /// What is wrong with a concurrency limit, or null when nothing is.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Validate"/> so that starting one configuration can ask this
    /// without also asking whether any configurations exist. That question has no meaning to a
    /// caller holding the configuration it wants to start, and asking it told such a caller to go
    /// and add one.
    /// </remarks>
    public static string? ValidateConcurrency(int concurrentTransfers) =>
        concurrentTransfers is < 1 or > 8
            ? "Concurrent transfers must be between 1 and 8."
            : null;
}
