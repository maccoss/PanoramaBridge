using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PanoramaBridge.Core.Transfer;

namespace PanoramaBridge.Core.Storage;

/// <summary>Loads and saves <see cref="AppSettings"/>.</summary>
public interface ISettingsStore
{
    /// <summary>Reads the settings, returning defaults when none have been saved.</summary>
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes the settings.</summary>
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>
/// Settings held as JSON in a single file.
/// </summary>
/// <remarks>
/// <para>
/// Written atomically -- to a temporary file, then moved over the original -- so a crash or a
/// power cut mid-write cannot leave a half-written file that fails to parse on next launch.
/// </para>
/// <para>
/// JSON rather than SQLite here on purpose: this data is small, changes rarely, and being
/// human-readable means it can be inspected, hand-edited or attached to a support request. The
/// upload ledger is the opposite on all three counts, which is why it lives in a database.
/// </para>
/// </remarks>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly string _path;
    private readonly ILogger<JsonSettingsStore> _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonSettingsStore(string path, ILogger<JsonSettingsStore>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = path;
        _log = log ?? NullLogger<JsonSettingsStore>.Instance;
    }

    /// <inheritdoc />
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }

        try
        {
            var json = await ReadPatientlyAsync(cancellationToken).ConfigureAwait(false);
            var version = ReadVersion(json);

            var loaded = version < AppSettings.CurrentVersion
                ? UpgradeSingleConfiguration(json)
                : JsonSerializer.Deserialize<AppSettings>(json, Options);

            var settings = loaded ?? new AppSettings();
            var normalized = settings.NormalizeWithdrawnValues();

            // Writing the result back is how an upgraded file stops being upgraded on every
            // launch, and how a withdrawn setting stops being carried. Neither applies to a file
            // written by a newer build: this one cannot represent everything in it, so stamping
            // its own version on the part it understood would hand that build back a file quietly
            // missing settings. A rollback leaves the file alone instead.
            if (settings.Version <= AppSettings.CurrentVersion
                && (version < AppSettings.CurrentVersion || !ReferenceEquals(settings, normalized)))
            {
                _log.LogInformation("Rewriting {Path} in the current settings format.", _path);

                try
                {
                    await SaveAsync(normalized, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The parsed settings still describe the safe behavior. A read-only settings
                    // directory must not make them look corrupt or discard unrelated values.
                    _log.LogWarning(ex, "Could not persist normalized settings to {Path}.", _path);
                }
            }

            return normalized;
        }
        catch (JsonException ex)
        {
            // The content is bad. Falling back to defaults beats refusing to start, and the file
            // is kept so it can be looked at rather than silently discarded.
            _log.LogError(ex, "Could not read settings from {Path}; falling back to defaults.", _path);
            TryPreserveCorruptFile();
            return new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file could not be read *this time*, which is not the same as its contents being
            // bad -- and the difference matters, because the answer to a bad file is to move it
            // aside and start from defaults. Doing that to a file that was merely locked for a
            // moment by antivirus or a backup agent would take the monitored folder, the
            // destination and the sign-in with it, on an instrument, for the sake of a lock that
            // had already gone.
            //
            // So nothing is renamed and nothing is discarded here. Defaults are still returned,
            // because refusing to start is worse, but the message says the settings could not be
            // read rather than that they were bad -- and the next launch, or the next save, finds
            // the file exactly as it was.
            _log.LogError(
                ex,
                "Could not open {Path} after {Attempts} attempts; it is in use by something else. "
                + "Starting with default settings this session; the file has been left alone.",
                _path,
                ReadAttempts);

            return new AppSettings();
        }
    }

    /// <summary>How many times a locked settings file is re-read before giving up.</summary>
    /// <remarks>
    /// Three attempts over about half a second. Long enough to outlast a virus scanner opening the
    /// file as it is written, short enough that nobody watching the window would notice; a lock
    /// held longer than this is not transient and waiting further would only delay saying so.
    /// </remarks>
    private const int ReadAttempts = 3;

    private static readonly TimeSpan BetweenReadAttempts = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Reads the file, giving a lock a moment to clear.
    /// </summary>
    /// <remarks>
    /// Shared as ReadWrite rather than taking the default: the point is to get past another
    /// process holding the file, so asking for exclusive use would fail against exactly the case
    /// this exists for.
    /// </remarks>
    private async Task<string> ReadPatientlyAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    _path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                using var reader = new StreamReader(stream);

                return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                && attempt < ReadAttempts)
            {
                _log.LogDebug(
                    "{Path} is in use; attempt {Attempt} of {Total}.",
                    _path,
                    attempt,
                    ReadAttempts);

                await Task.Delay(BetweenReadAttempts, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var temporary = _path + ".tmp";

            try
            {
                await using (var stream = File.Create(temporary))
                {
                    await JsonSerializer
                        .SerializeAsync(stream, settings, Options, cancellationToken)
                        .ConfigureAwait(false);
                }

                // Move over the original only once the new file is complete on disk.
                File.Move(temporary, _path, overwrite: true);
            }
            catch
            {
                // The move is what fails when the settings file is locked, and it fails after the
                // temporary file exists. Left behind, one accumulates beside the settings for
                // every save that ever lost that race -- and the next reader has to work out
                // which of the two files is the real one.
                TryDelete(temporary);
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// The <c>$version</c> in the file, or 1 when it says nothing.
    /// </summary>
    /// <remarks>
    /// Read from the raw JSON rather than from a deserialized <see cref="AppSettings"/>, because
    /// the type no longer has the properties a version 1 file carries. Reading such a file as the
    /// current shape would quietly discard the monitored folder, the destination and the sign-in,
    /// and the first anyone would know of it is that transfers had stopped.
    /// </remarks>
    private static int ReadVersion(string json)
    {
        using var document = JsonDocument.Parse(json, DocumentOptions);

        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("$version", out var version)
            && version.ValueKind == JsonValueKind.Number
            && version.TryGetInt32(out var number)
                ? number
                : 1;
    }

    /// <summary>
    /// Reads a settings file written before configurations existed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every version 1 file describes exactly one pairing, with its fields at the top level. It
    /// becomes configuration one, named for the folder it watches, and keeps an empty account --
    /// which is how it inherits the credential already in Windows Credential Manager. That is the
    /// whole of the credential migration; see <see cref="Security.ICredentialStore"/>.
    /// </para>
    /// <para>
    /// This is a read, not a write. The rewritten file is saved by the caller only once the old
    /// one has parsed, and <see cref="SaveAsync"/> writes through a temporary file, so a crash at
    /// any point leaves the version 1 file intact and still readable by the build that wrote it.
    /// </para>
    /// </remarks>
    private static AppSettings UpgradeSingleConfiguration(string json)
    {
        // Both shapes come out of the same text, and the overlap is deliberate: every property
        // that stayed at the application level is read exactly as before, and only the ones that
        // moved are read through the legacy view.
        var application = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        var legacy = JsonSerializer.Deserialize<LegacySettings>(json, Options) ?? new LegacySettings();

        using var document = JsonDocument.Parse(json, DocumentOptions);
        var root = document.RootElement;

        return application with
        {
            Version = AppSettings.CurrentVersion,
            Configurations =
            [
                legacy.ToConfiguration(
                    Mentions(root, nameof(LegacySettings.Extensions)),
                    Mentions(root, nameof(LegacySettings.ExcludedExtensions))),
            ],
        };
    }

    /// <summary>Whether the file names a property at all, whatever it holds.</summary>
    /// <remarks>
    /// Absent and <c>null</c> are different answers and have to stay different. An absent
    /// extension list means the file predates the setting, so the defaults apply; a hand-typed
    /// <c>null</c> is a mistake the settings screen is meant to report, and quietly turning it
    /// into the defaults would transfer file types the person editing the file had just tried to
    /// stop transferring. Deserializing gives null for both, so presence is read from the text.
    /// <para>
    /// Compared without case, because the deserializer is configured that way: a file saying
    /// <c>extensions</c> is read, and would otherwise be judged absent here.
    /// </para>
    /// </remarks>
    private static bool Mentions(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A version 1 settings file, as far as the properties that moved into a configuration.
    /// </summary>
    /// <remarks>
    /// Every default here has to match what version 1 defaulted to, because a file written before
    /// one of these settings existed simply omits it -- so the default is what that installation
    /// was actually doing, and getting one wrong changes a machine's behavior on update.
    /// </remarks>
    private sealed record LegacySettings
    {
        public string LocalDirectory { get; init; } = string.Empty;

        public bool IncludeSubdirectories { get; init; } = true;

        /// <summary>Nullable so that "absent" can be told from "explicitly empty".</summary>
        public IReadOnlyList<string>? Extensions { get; init; }

        /// <inheritdoc cref="Extensions" />
        public IReadOnlyList<string>? ExcludedExtensions { get; init; }

        public int StabilitySeconds { get; init; } = 10;

        public int ReconcileMinutes { get; init; } = 15;

        public int LockedFileRetryIntervalSeconds { get; init; } = 30;

        public int LockedFileMaxRetries { get; init; } = 20;

        public ConflictPolicy ConflictPolicy { get; init; } = ConflictPolicy.Ask;

        public string ServerUrl { get; init; } = "https://panoramaweb.org";

        public AuthMode AuthMode { get; init; } = AuthMode.ApiKey;

        public string UserName { get; init; } = string.Empty;

        public bool SaveCredentials { get; init; } = true;

        public string RemotePath { get; init; } = AppSettings.MacCossFilesPath;

        public bool VerifyUploads { get; init; } = true;

        public bool WriteChecksumSidecars { get; init; } = true;

        /// <param name="extensionsGiven">
        /// Whether the file named <see cref="Extensions"/>. See <see cref="Mentions"/> for why
        /// this cannot be inferred from the value.
        /// </param>
        /// <param name="exclusionsGiven">
        /// Whether the file named <see cref="ExcludedExtensions"/>.
        /// </param>
        public MonitoringConfiguration ToConfiguration(bool extensionsGiven, bool exclusionsGiven)
        {
            var configuration = new MonitoringConfiguration
            {
                Name = MonitoringConfiguration.SuggestNameFor(LocalDirectory),

                // Enabled, because this configuration is what the machine was already doing. An
                // update that left it switched off would stop transfers silently.
                Enabled = true,

                // No created time. The file never recorded when monitoring was set up, and there
                // is nothing honest to put here, so the column stays blank rather than claiming
                // the configuration was created by its own upgrade.
                CreatedUtc = default,

                // Empty, so this reads the credential where it has always been.
                Account = string.Empty,

                LocalDirectory = LocalDirectory,
                IncludeSubdirectories = IncludeSubdirectories,
                StabilitySeconds = StabilitySeconds,
                ReconcileMinutes = ReconcileMinutes,
                LockedFileRetryIntervalSeconds = LockedFileRetryIntervalSeconds,
                LockedFileMaxRetries = LockedFileMaxRetries,
                ConflictPolicy = ConflictPolicy,
                ServerUrl = ServerUrl,
                AuthMode = AuthMode,
                UserName = UserName,
                SaveCredentials = SaveCredentials,
                RemotePath = RemotePath,
                VerifyUploads = VerifyUploads,
                WriteChecksumSidecars = WriteChecksumSidecars,
            };

            // Left at the defaults unless the file said something, because an empty list for
            // Extensions transfers nothing at all and an empty ExcludedExtensions re-arms
            // uploading the chromatogram caches -- in both cases the opposite of what an absent
            // setting meant on the machine being upgraded. What the file did say is carried
            // through as it is, null included: the property coalesces that to empty, which is
            // what the settings screen then reports as a problem.
            if (extensionsGiven)
            {
                configuration = configuration with { Extensions = Extensions! };
            }

            if (exclusionsGiven)
            {
                configuration = configuration with { ExcludedExtensions = ExcludedExtensions! };
            }

            return configuration;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleaning up after a failure must not replace it with a different one.
        }
    }

    private void TryPreserveCorruptFile()
    {
        try
        {
            var kept = _path + ".corrupt";
            File.Move(_path, kept, overwrite: true);
            _log.LogInformation("The unreadable settings file was kept as {Path}.", kept);
        }
        catch (IOException)
        {
            // Nothing more to be done; defaults are already in use.
        }
    }
}
