using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using PanoramaBridge.Core.Storage;
using PanoramaBridge.Core.Transfer;

namespace PanoramaBridge.Tests.Storage;

/// <summary>
/// What happens to settings and ledger rows written by a version whose features have since been
/// withdrawn.
/// </summary>
/// <remarks>
/// Removing a feature looked like the safe half of this work, and it was not: settings and rows
/// already on disk outlive the code that wrote them. These are the ways the withdrawal would
/// otherwise have hurt somebody upgrading.
/// </remarks>
public sealed class UpgradeFromWithdrawnFeaturesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pb-upgrade-").FullName;

    [Fact]
    public void A_settings_file_naming_the_withdrawn_rename_policy_still_loads()
    {
        // Settings are JSON with a string enum converter, so a file saying "Rename" throws while
        // parsing -- and the store's answer to an unreadable file is to move it aside and start
        // from defaults. Deleting the member would have cost anyone who chose it their server,
        // their monitored folder and every other setting, on the first launch after updating.
        var json = """{"ConflictPolicy":"Rename","LocalDirectory":"/lab/instrument-data"}""";

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };

        var settings = JsonSerializer.Deserialize<AppSettings>(json, options);

        settings.ShouldNotBeNull();
        settings!.LocalDirectory.ShouldBe("/lab/instrument-data", "the rest of the file must survive");
    }

    [Theory]
    [InlineData("state = 10", "keep the copy on the server")]
    [InlineData("rename_to = 'run (2).raw'", "under a different name")]
    public async Task A_row_this_build_cannot_read_becomes_a_visible_conflict(
        string legacy, string expected)
    {
        // Both rows were written by a version between v26.3.0 and v26.4.6 and mean something this
        // build has no way to act on. Left alone, the first is listed by no filter at all -- the
        // All filter builds its WHERE from the enum's values -- while the sweep, seeing states and
        // destinations it does not recognise, offers both files again: under Overwrite that
        // destroys the copy a rename existed to preserve.
        var path = Path.Combine(_dir, $"ledger-{legacy.GetHashCode():x}.db");

        await using (var old = new SqliteStateStore(path))
        {
            await old.SaveAsync(Row(@"C:\data\run.raw"));
        }

        // Put the row into its old shape and wind the schema back, so opening it again is a real
        // upgrade rather than a no-op on an already-migrated file.
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText =
                $"UPDATE uploads SET {legacy}; DELETE FROM schema_version; "
                + "INSERT INTO schema_version (version) VALUES (4);";

            await command.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();

        await using var store = new SqliteStateStore(path);

        var row = await store.GetAsync(@"C:\data\run.raw");
        row!.State.ShouldBe(TransferState.Conflict);
        row.LastError!.ShouldContain(expected);
    }

    private static UploadRecord Row(string local) =>
        new(
            LocalPath: local,
            RemotePath: "/_webdav/uploads/run.raw",
            Length: 4,
            LastWriteUnixMs: 1,
            Md5: null,
            Sha256: null,
            State: TransferState.Verified,
            VerifyMethod: VerifyMethod.ServerMd5,
            VerifiedUtc: DateTimeOffset.UtcNow,
            Attempts: 1,
            LastError: null);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
