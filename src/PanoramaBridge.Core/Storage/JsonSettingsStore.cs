using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
            await using var stream = File.OpenRead(_path);
            var settings = await JsonSerializer
                .DeserializeAsync<AppSettings>(stream, Options, cancellationToken)
                .ConfigureAwait(false);

            return settings ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Falling back to defaults beats refusing to start. The bad file is kept so it can
            // be looked at rather than silently discarded.
            _log.LogError(ex, "Could not read settings from {Path}; falling back to defaults.", _path);
            TryPreserveCorruptFile();
            return new AppSettings();
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

            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer
                    .SerializeAsync(stream, settings, Options, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Move over the original only once the new file is complete on disk.
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
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
