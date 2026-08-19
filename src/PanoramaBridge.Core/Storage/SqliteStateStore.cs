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
    private const int CurrentSchemaVersion = 1;

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

        await ExecuteWriteAsync(
            """
            INSERT INTO uploads
              (local_path, remote_path, size, mtime_utc, md5, sha256,
               state, verify_method, verified_utc, attempts, last_error, is_dataset)
            VALUES
              ($path, $remote, $size, $mtime, $md5, $sha256,
               $state, $verify, $verified, $attempts, $error, $dataset)
            ON CONFLICT(local_path) DO UPDATE SET
              remote_path = $remote, size = $size, mtime_utc = $mtime,
              md5 = $md5, sha256 = $sha256, state = $state, verify_method = $verify,
              verified_utc = $verified, attempts = $attempts, last_error = $error,
              is_dataset = $dataset;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$path", record.LocalPath);
                command.Parameters.AddWithValue("$remote", record.RemotePath);
                command.Parameters.AddWithValue("$size", record.Length);
                command.Parameters.AddWithValue("$mtime", record.LastWriteUnixMs);
                command.Parameters.AddWithValue("$md5", record.Md5 ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$sha256", record.Sha256 ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$state", (int)record.State);
                command.Parameters.AddWithValue("$verify", (int)record.VerifyMethod);
                command.Parameters.AddWithValue(
                    "$verified",
                    record.VerifiedUtc?.ToUnixTimeMilliseconds() ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$attempts", record.Attempts);
                command.Parameters.AddWithValue("$error", record.LastError ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$dataset", record.IsDataset ? 1 : 0);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task SetStateAsync(
        string localPath,
        TransferState state,
        string? lastError = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        // Attempts increments only when an upload actually starts, so the count reflects
        // transfers tried rather than state changes made.
        return ExecuteWriteAsync(
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
            cancellationToken);
    }

    /// <inheritdoc />
    public Task MarkVerifiedAsync(
        string localPath,
        VerifyMethod method,
        DateTimeOffset verifiedUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        return ExecuteWriteAsync(
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
            cancellationToken);
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
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.IsDBNull(0)
            || reader.IsDBNull(1))
        {
            return null;
        }

        return new ContentHashes(reader.GetString(0), reader.GetString(1));
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
                command.Parameters.AddWithValue("$sha256", hashes.Sha256);
            },
            cancellationToken);

    private const string SelectColumns =
        """
        SELECT local_path, remote_path, size, mtime_utc, md5, sha256,
               state, verify_method, verified_utc, attempts, last_error, is_dataset
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
        IsDataset: reader.GetInt32(11) != 0);

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task ExecuteWriteAsync(
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
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
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
              is_dataset    INTEGER NOT NULL DEFAULT 0
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
            command.CommandText = "INSERT INTO schema_version (version) VALUES ($v);";
            command.Parameters.AddWithValue("$v", CurrentSchemaVersion);
            command.ExecuteNonQuery();
        }
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
