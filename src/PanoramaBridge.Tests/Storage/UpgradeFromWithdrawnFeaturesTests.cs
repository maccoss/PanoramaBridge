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

    [Theory]
    [InlineData("state = 10", "keep the copy on the server")]
    [InlineData("rename_to = 'run (2).raw'", "under a different name")]
    public async Task A_legacy_row_written_after_a_rollback_is_still_converted(
        string legacy, string expected)
    {
        // The version stamp cannot be trusted to mean the data is clean, because rollback is
        // supported: this database says 5 already -- the conversion nominally ran -- and then a
        // rolled-back v26.4.x wrote a fresh Keep or rename row. Guarded by the stamp, the
        // conversion never runs again and the row is misread for ever: invisible to every filter,
        // re-offered by every sweep. So it runs on every open, made idempotent instead of
        // guarded.
        var path = Path.Combine(_dir, $"rollback-{legacy.GetHashCode():x}.db");

        // A normal open stamps the current version. No winding back afterwards: that is the
        // difference between this test and the one above.
        await using (var current = new SqliteStateStore(path))
        {
            await current.SaveAsync(Row(@"C:\data\run.raw"));
        }

        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE uploads SET {legacy};";
            await command.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();

        await using var store = new SqliteStateStore(path);

        var row = await store.GetAsync(@"C:\data\run.raw");
        row!.State.ShouldBe(TransferState.Conflict);
        row.LastError!.ShouldContain(expected);

        // Stamped, so the sweep knows Overwrite must not release what a person once decided.
        row.ConflictKind.ShouldBe(ConflictKind.WithdrawnDecision);
    }

    [Fact]
    public async Task The_conversion_does_not_touch_a_row_twice()
    {
        // Idempotency is what replaces the version guard, so it is worth proving rather than
        // assuming: a converted row's rename_to is cleared and its state is no longer 10, so a
        // second open must find nothing to do -- including not overwriting a LastError that has
        // moved on since.
        var path = Path.Combine(_dir, "idempotent.db");

        await using (var current = new SqliteStateStore(path))
        {
            await current.SaveAsync(Row(@"C:\data\run.raw"));
        }

        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE uploads SET rename_to = 'run (2).raw';";
            await command.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();

        // First open converts; the row is then answered by policy and moves on.
        await using (var store = new SqliteStateStore(path))
        {
            (await store.GetAsync(@"C:\data\run.raw"))!.State.ShouldBe(TransferState.Conflict);

            await store.SetStateAsync(
                @"C:\data\run.raw", TransferState.Skipped, "Left alone by policy.");
        }

        SqliteConnection.ClearAllPools();

        // Second open must leave the answered row alone.
        await using (var reopened = new SqliteStateStore(path))
        {
            var row = await reopened.GetAsync(@"C:\data\run.raw");
            row!.State.ShouldBe(TransferState.Skipped);
            row.LastError.ShouldBe("Left alone by policy.");
        }
    }

    [Fact]
    public async Task Saving_a_row_overwrites_whatever_a_withdrawn_build_left_behind()
    {
        // A row can carry rename_to/resolution/conflict_kind from a build between v26.3.0 and
        // v26.4.6 without ever reaching the conversion's rewrite — this build simply saves over
        // it later, once the conflict is resolved some other way. If that save left the values
        // untouched, a later rollback to an old build would read the stale rename_to and resolve
        // the row to a destination this build already moved past.
        //
        // rename_to and resolution are retired and written as blank every time. conflict_kind is
        // not retired: it is live again, and a save writes the record's own value — which is
        // what makes the 3 planted below disappear. Asserted here rather than left implied,
        // because the name of this test used to say all three were cleared, and a reader who
        // believed that would write code relying on a save always zeroing the column.
        var path = Path.Combine(_dir, "retired-columns.db");

        await using var store = new SqliteStateStore(path);
        await store.SaveAsync(Row(@"C:\data\run.raw"));

        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE uploads SET rename_to = 'run (2).raw', resolution = 2, conflict_kind = 3;";
            await command.ExecuteNonQueryAsync();
        }

        await store.SaveAsync(Row(@"C:\data\run.raw") with { State = TransferState.Verified });

        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT rename_to, resolution, conflict_kind FROM uploads WHERE local_path = @p;";
            command.Parameters.AddWithValue("@p", @"C:\data\run.raw");

            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).ShouldBeTrue();

            reader.IsDBNull(0).ShouldBeTrue("rename_to");
            reader.GetInt32(1).ShouldBe(0, "resolution");
            reader.GetInt32(2).ShouldBe(
                (int)ConflictKind.Unknown,
                "conflict_kind carries the saved record's value, which is Unknown here");
        }
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
