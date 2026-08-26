using Microsoft.Data.Sqlite;
using PanoramaBridge.Core.Hashing;

namespace PanoramaBridge.Core.Storage;

/// <summary>
/// SQLite-backed upload ledger and hash cache.
/// </summary>
/// <remarks>
/// <para>
/// SQLite rather than a JSON file because the access pattern rules JSON out. The Python version
/// rewrote its entire history on every successful upload, which is quadratic over a session and
/// loses everything on a crash mid-write, and it capped the hash cache at a thousand entries
/// precisely because the cache lived inside a file it had to rewrite. Here writes are
/// incremental, lookups are indexed, and there is no size cliff.
/// </para>
/// <para>
/// WAL mode lets several upload workers write while a reader is running. <c>synchronous=NORMAL</c>
/// is the right trade for this data: losing the last few ledger rows to a power cut costs a
/// re-verification, not a lost file, and the files themselves are on the server already.
/// </para>
/// </remarks>
public sealed class SqliteStateStore : IStateStore, IAsyncDisposable, IDisposable
{
    private const int CurrentSchemaVersion = 5;

    /// <summary>
    /// The state value v26.3.0—v26.4.6 wrote for "a person chose to keep the copy on the
    /// server", which no longer exists.
    /// </summary>
    /// <remarks>
    /// Named, and asserted against the live enum below, because the statement that converts these
    /// rows runs on every open and is not guarded by the schema version — it cannot be, since a
    /// rolled-back build can write one of these rows after the version is already current. A bare
    /// 10 in a SQL string is invisible to the compiler, so the next state anybody adds takes the
    /// value and every row in it is silently rewritten, on every launch, for ever. There is no
    /// test that would fail, because the rule lives in a string.
    /// </remarks>
    private const int WithdrawnKeptOnServerState = 10;

    static SqliteStateStore()
    {
        if (Enum.IsDefined(typeof(TransferState), WithdrawnKeptOnServerState))
        {
            throw new InvalidOperationException(
                $"TransferState now defines {WithdrawnKeptOnServerState}, which the conversion "
                + "in Initialize treats as a withdrawn state and rewrites on every open. Give the "
                + "new state a different value, or retire that conversion.");
        }
    }

    private readonly string _connectionString;

    /// <summary>
    /// Serializes writes. SQLite handles concurrent access itself, but funnelling writes avoids
    /// spending the workers' time in lock retries under WAL.
    /// </summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Held open for the lifetime of the store so an in-memory database is not discarded
    /// between operations, and so the file is opened once rather than per call.
    /// </summary>
    private readonly SqliteConnection _keepAlive;

    public SqliteStateStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        };

        _connectionString = builder.ToString();

        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();

        Initialize();
    }

    /// <summary>An in-memory store, for tests.</summary>
    public static SqliteStateStore InMemory() =>
        new($"file:pb-{Guid.NewGuid():n}?mode=memory&cache=shared");

    /// <inheritdoc />
    public async Task<UploadRecord?> GetAsync(
        string localPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} FROM uploads WHERE local_path = $path LIMIT 1;";
        command.Parameters.AddWithValue("$path", localPath);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc />
    public async Task SaveAsync(UploadRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        // resolution/rename_to are cleared on every save so a row this build fully rewrites
        // cannot still carry a stale value a rolled-back build would act on. conflict_kind is
        // live again: it records why a row is held, which the sweep needs to know before
        // releasing it under a policy.
        await ExecuteWriteAsync(
            """
            INSERT INTO uploads
              (local_path, remote_path, size, mtime_utc, md5, sha256,
               state, verify_method, verified_utc, attempts, last_error, is_dataset,
               raw_check, conflict_kind)
            VALUES
              ($path, $remote, $size, $mtime, $md5, $sha256,
               $state, $verify, $verified, $attempts, $error, 0, $rawcheck, $kind)
            ON CONFLICT(local_path) DO UPDATE SET
              local_path = $path, remote_path = $remote, size = $size, mtime_utc = $mtime,
              md5 = $md5, sha256 = $sha256, state = $state, verify_method = $verify,
              verified_utc = $verified, attempts = $attempts, last_error = $error,
              is_dataset = 0, raw_check = $rawcheck, conflict_kind = $kind,
              resolution = 0, rename_to = NULL;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$path", record.LocalPath);
                command.Parameters.AddWithValue("$remote", record.RemotePath);
                command.Parameters.AddWithValue("$size", record.Length);
                command.Parameters.AddWithValue("$mtime", record.LastWriteUnixMs);
                command.Parameters.AddWithValue("$md5", record.Md5 ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$sha256", record.Sha256 ?? (object)DBNull.Value);
                command.Parameters.AddWithValue(
                    "$rawcheck", record.RawCheck ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$kind", (int)record.ConflictKind);
                command.Parameters.AddWithValue("$state", (int)record.State);
                command.Parameters.AddWithValue("$verify", (int)record.VerifyMethod);
                command.Parameters.AddWithValue(
                    "$verified",
                    record.VerifiedUtc?.ToUnixTimeMilliseconds() ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$attempts", record.Attempts);
                command.Parameters.AddWithValue("$error", record.LastError ?? (object)DBNull.Value);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public async Task SetStateAsync(
        string localPath,
        TransferState state,
        string? lastError = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        // Attempts increments only when an upload actually starts, so the count reflects
        // transfers tried rather than state changes made.
        var changed = await ExecuteWriteAsync(
            """
            UPDATE uploads
               SET state = $state,
                   last_error = $error,
                   attempts = attempts + CASE WHEN $state = $uploading THEN 1 ELSE 0 END
             WHERE local_path = $path;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$path", localPath);
                command.Parameters.AddWithValue("$state", (int)state);
                command.Parameters.AddWithValue("$uploading", (int)TransferState.Uploading);
                command.Parameters.AddWithValue("$error", lastError ?? (object)DBNull.Value);
            },
            cancellationToken).ConfigureAwait(false);

        EnsureExistingRow(changed, localPath);
    }

    /// <inheritdoc />
    public async Task MarkVerifiedAsync(
        string localPath,
        VerifyMethod method,
        DateTimeOffset verifiedUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        var changed = await ExecuteWriteAsync(
            """
            UPDATE uploads
               SET state = $state, verify_method = $verify, verified_utc = $verified,
                   last_error = NULL
             WHERE local_path = $path;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$path", localPath);
                command.Parameters.AddWithValue("$state", (int)TransferState.Verified);
                command.Parameters.AddWithValue("$verify", (int)method);
                command.Parameters.AddWithValue("$verified", verifiedUtc.ToUnixTimeMilliseconds());
            },
            cancellationToken).ConfigureAwait(false);

        EnsureExistingRow(changed, localPath);
    }

    /// <summary>
    /// Largest number of paths asked about in one statement.
    /// </summary>
    /// <remarks>
    /// Well inside SQLite's parameter limit, which is 999 on builds older than 3.32. Bigger
    /// batches stop paying: the cost is dominated by the round trip, not by the parameter count.
    /// </remarks>
    private const int LookupBatchSize = 500;

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, UploadRecord>> GetManyAsync(
        IReadOnlyCollection<string> localPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localPaths);

        // OrdinalIgnoreCase to match the column's NOCASE collation, so a caller that looks up
        // the path it passed in finds the row whatever case the ledger recorded.
        var found = new Dictionary<string, UploadRecord>(StringComparer.OrdinalIgnoreCase);

        if (localPaths.Count == 0)
        {
            return found;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (var batch in localPaths.Chunk(LookupBatchSize))
        {
            await using var command = connection.CreateCommand();

            var placeholders = string.Join(", ", batch.Select((_, i) => $"$p{i}"));
            command.CommandText = $"{SelectColumns} FROM uploads WHERE local_path IN ({placeholders});";

            for (var i = 0; i < batch.Length; i++)
            {
                command.Parameters.AddWithValue($"$p{i}", batch[i]);
            }

            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var record = Read(reader);
                found[record.LocalPath] = record;
            }
        }

        return found;
    }

    /// <inheritdoc />
    public async Task<UploadRecord?> FindByContentAsync(
        long length,
        string md5,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(md5);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // Indexed on (size, md5). Size is included so the common case never has to compare
        // hash strings at all.
        command.CommandText = $"""
            {SelectColumns} FROM uploads
             WHERE size = $size AND md5 = $md5
               AND state IN ($verified, $skipped)
               AND verify_method = $serverMd5
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("$size", length);
        command.Parameters.AddWithValue("$md5", md5.ToLowerInvariant());
        command.Parameters.AddWithValue("$verified", (int)TransferState.Verified);
        command.Parameters.AddWithValue("$skipped", (int)TransferState.Skipped);
        command.Parameters.AddWithValue("$serverMd5", (int)VerifyMethod.ServerMd5);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<UploadRecord>> GetInterruptedAsync(
        CancellationToken cancellationToken = default) =>
        GetByStateAsync(
            [TransferState.Uploading, TransferState.Uploaded, TransferState.Queued],
            limit: int.MaxValue,
            cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<UploadRecord>> GetByStateAsync(
        IReadOnlyCollection<TransferState> states,
        int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(states);

        if (states.Count == 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var placeholders = string.Join(", ", states.Select((_, i) => $"$s{i}"));
        command.CommandText =
            $"{SelectColumns} FROM uploads WHERE state IN ({placeholders}) ORDER BY rowid DESC LIMIT $limit;";

        var index = 0;
        foreach (var state in states)
        {
            command.Parameters.AddWithValue($"$s{index++}", (int)state);
        }

        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<UploadRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Read(reader));
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<TransferState, int>> CountByStateAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT state, COUNT(*) FROM uploads GROUP BY state;";

        var counts = new Dictionary<TransferState, int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts[(TransferState)reader.GetInt32(0)] = reader.GetInt32(1);
        }

        return counts;
    }

    /// <inheritdoc />
    public async Task<ContentHashes?> GetCachedHashesAsync(
        LocalFileStamp stamp,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // Keyed on the stamp, so a modified file is a miss rather than a stale hit. One
        // canonical key format, unlike the Python version, which built the key two different
        // ways in two places and therefore never hit its own cache.
        command.CommandText =
            """
            SELECT md5, sha256 FROM hash_cache
             WHERE local_path = $path AND size = $size AND mtime_utc = $mtime
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("$path", stamp.Path);
        command.Parameters.AddWithValue("$size", stamp.Length);
        command.Parameters.AddWithValue("$mtime", stamp.LastWriteUnixMs);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        // Only the MD5 is required. SHA-256 is optional, and treating its absence as a cache
        // miss would mean never hitting the cache at all -- which is exactly the defect that
        // made the previous implementation re-hash multi-gigabyte files on every pass.
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
        {
            return null;
        }

        return new ContentHashes(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    /// <inheritdoc />
    public Task SaveCachedHashesAsync(
        LocalFileStamp stamp,
        ContentHashes hashes,
        CancellationToken cancellationToken = default) =>
        ExecuteWriteAsync(
            """
            INSERT INTO hash_cache (local_path, size, mtime_utc, md5, sha256)
            VALUES ($path, $size, $mtime, $md5, $sha256)
            ON CONFLICT(local_path, size, mtime_utc) DO UPDATE SET md5 = $md5, sha256 = $sha256;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$path", stamp.Path);
                command.Parameters.AddWithValue("$size", stamp.Length);
                command.Parameters.AddWithValue("$mtime", stamp.LastWriteUnixMs);
                command.Parameters.AddWithValue("$md5", hashes.Md5);

                // DBNull, not a CLR null: ADO.NET rejects the latter, and with SHA-256 now
                // optional this parameter is null on every ordinary transfer.
                command.Parameters.AddWithValue("$sha256", hashes.Sha256 ?? (object)DBNull.Value);
            },
            cancellationToken);

    private const string SelectColumns =
        """
         SELECT local_path, remote_path, size, mtime_utc, md5, sha256,
             state, verify_method, verified_utc, attempts, last_error, raw_check,
             conflict_kind
        """;

    private static UploadRecord Read(SqliteDataReader reader) => new(
        LocalPath: reader.GetString(0),
        RemotePath: reader.GetString(1),
        Length: reader.GetInt64(2),
        LastWriteUnixMs: reader.GetInt64(3),
        Md5: reader.IsDBNull(4) ? null : reader.GetString(4),
        Sha256: reader.IsDBNull(5) ? null : reader.GetString(5),
        State: (TransferState)reader.GetInt32(6),
        VerifyMethod: (VerifyMethod)reader.GetInt32(7),
        VerifiedUtc: reader.IsDBNull(8)
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
        Attempts: reader.GetInt32(9),
        LastError: reader.IsDBNull(10) ? null : reader.GetString(10),
        RawCheck: reader.IsDBNull(11) ? null : reader.GetString(11),
        ConflictKind: (ConflictKind)reader.GetInt32(12));

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>Writes, and reports how many rows it actually changed.</summary>
    private async Task<int> ExecuteWriteCountingAsync(
        string sql,
        Action<SqliteCommand> bind,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            bind(command);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <remarks>
    /// Defers to the counting form rather than repeating it. Two copies of the write-lock, open,
    /// bind and execute sequence would have to be kept in step, and a busy retry or a transaction
    /// added to one of them would silently apply to only half the store's writes.
    /// </remarks>
    private Task<int> ExecuteWriteAsync(
        string sql,
        Action<SqliteCommand> bind,
        CancellationToken cancellationToken) =>
        ExecuteWriteCountingAsync(sql, bind, cancellationToken);

    private static void EnsureExistingRow(int changed, string localPath)
    {
        if (changed == 0)
        {
            throw new InvalidOperationException(
                $"Cannot transition an unknown upload record: {localPath}. Save the row first.");
        }
    }

    private void Initialize()
    {
        using var command = _keepAlive.CreateCommand();

        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS uploads (
              local_path    TEXT    PRIMARY KEY COLLATE NOCASE,
              remote_path   TEXT    NOT NULL,
              size          INTEGER NOT NULL,
              mtime_utc     INTEGER NOT NULL,
              md5           TEXT,
              sha256        TEXT,
              state         INTEGER NOT NULL,
              verify_method INTEGER NOT NULL DEFAULT 0,
              verified_utc  INTEGER,
              attempts      INTEGER NOT NULL DEFAULT 0,
              last_error    TEXT,
              is_dataset    INTEGER NOT NULL DEFAULT 0,
              raw_check     TEXT,
              resolution    INTEGER NOT NULL DEFAULT 0,
              rename_to     TEXT,
              conflict_kind INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS ix_uploads_state   ON uploads(state);
            CREATE INDEX IF NOT EXISTS ix_uploads_remote  ON uploads(remote_path);
            CREATE INDEX IF NOT EXISTS ix_uploads_content ON uploads(size, md5);

            CREATE TABLE IF NOT EXISTS hash_cache (
              local_path TEXT    NOT NULL COLLATE NOCASE,
              size       INTEGER NOT NULL,
              mtime_utc  INTEGER NOT NULL,
              md5        TEXT,
              sha256     TEXT,
              PRIMARY KEY (local_path, size, mtime_utc)
            );

            CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);
            """;
        command.ExecuteNonQuery();

        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        var version = Convert.ToInt32(command.ExecuteScalar());

        if (version < CurrentSchemaVersion)
        {
            // The statements above only create tables that are absent, so a database written by
            // an older build keeps its old shape and a new column never arrives. Until now
            // nothing needed one, and the version stamp was recorded without ever being acted
            // on. Migrations are additive and each is guarded, so running twice is harmless.
            Migrate(command, from: version);

            command.CommandText = "INSERT INTO schema_version (version) VALUES ($v);";
            command.Parameters.AddWithValue("$v", CurrentSchemaVersion);
            command.ExecuteNonQuery();
        }

        // After Migrate, which is where rename_to is added: the schema block above runs before
        // it, and on a database written before schema 3 the column is not there yet, so creating
        // an index on it would throw on open.
        //
        // Partial, so it holds only rows a withdrawn build left a rename on — none, on every
        // ledger this was measured against. Both halves of the conversion's OR have to be indexed
        // or SQLite cannot use the OR-by-union plan and reads the whole table instead, on every
        // launch, to find nothing.
        command.CommandText =
            """
            CREATE INDEX IF NOT EXISTS ix_uploads_renamed ON uploads(rename_to)
                WHERE rename_to IS NOT NULL;
            """;

        command.ExecuteNonQuery();

        // Every open, not a one-shot migration.
        //
        // This started life inside Migrate, guarded by the schema stamp — and the stamp cannot
        // be trusted to mean the data is clean, because rollback is supported: roll back to
        // v26.4.x, press Keep on a conflict (writing state 10) or send a file alongside (writing
        // rename_to), and come forward again. The stamp already says 5, the guard skips, and the
        // row is misread for ever — invisible to every filter, re-offered by every sweep.
        //
        // So it is idempotent instead of guarded. Converting sets the state and clears rename_to,
        // so a converted row can never match again.
        //
        // Both halves of the OR are indexed, which they have to be: this runs synchronously in the
        // constructor on every start, and an instrument ledger holds hundreds of thousands of
        // rows. ix_uploads_state covers the state; the partial index below covers rename_to, and
        // being partial it holds only the rows that have one — on the ledgers this was measured
        // against, none. Without it SQLite cannot use the OR-by-union plan and falls back to
        // reading the whole table, every launch, to find nothing.
        //
        // Deliberately stamps no conflict_kind. A value meaning "carried over from the withdrawn
        // feature" was written here and then removed: v26.4.x reads this column and switches on
        // it, so a value those builds do not define was swept up by their bulk actions — the
        // conversion defeating the rollback it exists to survive. These become ordinary held
        // conflicts, which the default of holding them already handles.
        command.Parameters.Clear();
        command.CommandText =
            """
            UPDATE uploads
               SET state = $conflict,
                   rename_to = NULL,

                   -- Cleared with rename_to, never on its own. v26.4.x asks only whether
                   -- resolution is set before acting, and clearing the target while leaving the
                   -- intention gave a rolled-back build a pending rename with nowhere to go: it
                   -- resolves to the file's original name and replaces the copy the rename
                   -- existed to preserve. SaveAsync clears both together for this reason.
                   resolution = 0,
                   last_error = CASE
                       WHEN rename_to IS NOT NULL
                           THEN 'This file was sent under a different name by an earlier '
                                || 'version. Sending files under a new name has been removed, '
                                || 'so it is held for a decision.'
                       ELSE 'An earlier version recorded that you chose to keep the copy on '
                            || 'the server. That choice is no longer stored, so it is held '
                            || 'for a decision.'
                   END
             WHERE state = $withdrawn OR rename_to IS NOT NULL;
            """;

        command.Parameters.AddWithValue("$conflict", (int)TransferState.Conflict);
        command.Parameters.AddWithValue("$withdrawn", WithdrawnKeptOnServerState);
        command.ExecuteNonQuery();
    }

    /// <summary>Brings an existing database up to the current shape.</summary>
    /// <remarks>
    /// Additive only: no column is dropped or retyped, so a database stays readable by the build
    /// that wrote it. That matters because an update can be rolled back, and a ledger the previous
    /// version cannot open would take the upload history with it.
    /// </remarks>
    private static void Migrate(SqliteCommand command, int from)
    {
        if (from < 2 && !ColumnExists(command, "uploads", "raw_check"))
        {
            command.CommandText = "ALTER TABLE uploads ADD COLUMN raw_check TEXT;";
            command.ExecuteNonQuery();
        }

        if (from < 3 && !ColumnExists(command, "uploads", "resolution"))
        {
            // NOT NULL with a default is safe in ALTER TABLE ADD COLUMN: existing rows take the
            // default, which is None, which is what an un-decided row means.
            command.CommandText =
                "ALTER TABLE uploads ADD COLUMN resolution INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();
        }

        if (from < 3 && !ColumnExists(command, "uploads", "rename_to"))
        {
            command.CommandText = "ALTER TABLE uploads ADD COLUMN rename_to TEXT;";
            command.ExecuteNonQuery();
        }

        if (from < 4 && !ColumnExists(command, "uploads", "conflict_kind"))
        {
            // Existing rows take Unknown. A conflict recorded by an older build therefore offers
            // every choice, which is the pre-existing behaviour rather than a new risk: those
            // rows were held before any of this existed.
            command.CommandText =
                "ALTER TABLE uploads ADD COLUMN conflict_kind INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();
        }

    }

    private static bool ColumnExists(SqliteCommand command, string table, string column)
    {
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _keepAlive.Dispose();
        _writeLock.Dispose();
        SqliteConnection.ClearPool(_keepAlive);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _keepAlive.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
