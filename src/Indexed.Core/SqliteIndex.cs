using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Indexed.Core;

/// <summary>
/// Per-repository SQLite+FTS5 index wrapper. Owns one writer connection and
/// a small pool of reader connections; all DDL, migrations, and file UPSERTs
/// go through the writer; candidate queries go through readers.
/// </summary>
/// <remarks>
/// <para>
/// On first <see cref="OpenOrCreate"/>, if the file is missing or the on-disk
/// schema does not match <see cref="SqliteSchema.Version"/>, the existing
/// <c>index.db</c> (and any WAL sidecars) are deleted and a fresh schema is
/// written. Rebuilds are therefore safe at any time — callers treat a new
/// <see cref="SqliteIndex"/> as "authoritative but empty" and re-enqueue the
/// full file set for indexing.
/// </para>
/// <para>
/// Concurrency model (proposal §10): one writer, many readers. Writers hold
/// the SQLite write lock for the duration of a <see cref="BeginWrite"/> scope;
/// readers run concurrently against the WAL snapshot. A <see cref="SemaphoreSlim"/>
/// serializes writers across managed threads so a single <c>SqliteIndex</c>
/// instance is safe to share across the indexer worker and HTTP request
/// handlers.
/// </para>
/// <para>
/// Lifetime: the instance takes ownership of the DB file and all its
/// connections. <see cref="DisposeAsync"/> closes both the writer and the
/// reader pool, after which subsequent method calls throw
/// <see cref="ObjectDisposedException"/>.
/// </para>
/// </remarks>
public sealed class SqliteIndex : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _writer;
    private readonly SemaphoreSlim _writerLock = new(initialCount: 1, maxCount: 1);
    private readonly List<SqliteConnection> _readers = new();
    private readonly SemaphoreSlim _readerLock = new(initialCount: 1, maxCount: 1);
    private bool _disposed;

    private SqliteIndex(string dbPath, SqliteConnection writer)
    {
        _dbPath = dbPath;
        _writer = writer;
    }

    /// <summary>Absolute path to the backing <c>index.db</c> file.</summary>
    public string DbPath => _dbPath;

    /// <summary>Schema version the backing DB was opened at.</summary>
    public int SchemaVersion { get; private set; }

    /// <summary>
    /// Open or create the index at <paramref name="dbPath"/>. Deletes and
    /// recreates the DB when the on-disk schema version does not match
    /// <see cref="SqliteSchema.Version"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The parent directory must exist. WAL mode is enabled unconditionally;
    /// <c>synchronous=NORMAL</c> trades a small durability window for
    /// significant write throughput on the indexer batch path (acceptable —
    /// the index is a derived artifact, safely rebuildable from source).
    /// </para>
    /// </remarks>
    public static SqliteIndex OpenOrCreate(string dbPath)
    {
        if (string.IsNullOrEmpty(dbPath)) throw new ArgumentException("dbPath is required", nameof(dbPath));
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var needsCreate = !File.Exists(dbPath);
        var writer = OpenWriter(dbPath);

        try
        {
            ApplyConnectionPragmas(writer);

            if (!needsCreate && !SchemaMatches(writer, out _))
            {
                writer.Dispose();
                DeleteDbFiles(dbPath);
                needsCreate = true;
                writer = OpenWriter(dbPath);
                ApplyConnectionPragmas(writer);
            }

            if (needsCreate)
            {
                CreateSchema(writer);
            }

            if (!SchemaMatches(writer, out var version))
            {
                throw new InvalidOperationException(
                    "index.db was just created but schema version check still failed — likely an FTS5 tokenizer support issue.");
            }

            var index = new SqliteIndex(dbPath, writer) { SchemaVersion = version };
            return index;
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    /// <summary>Read a <c>meta</c> KV. Returns <c>null</c> when absent.</summary>
    public string? GetMeta(string key)
    {
        ThrowIfDisposed();
        using var cmd = _writer.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        var result = cmd.ExecuteScalar();
        return result is string s ? s : null;
    }

    /// <summary>Upsert a <c>meta</c> KV. Null <paramref name="value"/> deletes the row.</summary>
    public void SetMeta(string key, string? value)
    {
        ThrowIfDisposed();
        using var cmd = _writer.CreateCommand();
        if (value is null)
        {
            cmd.CommandText = "DELETE FROM meta WHERE key = $k;";
            cmd.Parameters.AddWithValue("$k", key);
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO meta(key, value) VALUES($k, $v)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
        }
        cmd.ExecuteNonQuery();
    }

    /// <summary>Total number of rows in <c>files</c>.</summary>
    public long GetFileCount()
    {
        ThrowIfDisposed();
        using var cmd = _writer.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM files;";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>
    /// Enter an exclusive writer scope. Use <c>await using</c>; the
    /// underlying <see cref="SqliteTransaction"/> is committed on dispose
    /// unless the caller marked the scope as failed.
    /// </summary>
    public async ValueTask<WriterScope> BeginWriteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _writerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tx = _writer.BeginTransaction();
            return new WriterScope(this, tx);
        }
        catch
        {
            _writerLock.Release();
            throw;
        }
    }

    /// <summary>
    /// Acquire a reader connection. Each call returns a short-lived
    /// <see cref="ReaderLease"/> that MUST be disposed; connections are
    /// reused across queries.
    /// </summary>
    public async ValueTask<ReaderLease> RentReaderAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _readerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_readers.Count > 0)
            {
                var pooled = _readers[^1];
                _readers.RemoveAt(_readers.Count - 1);
                return new ReaderLease(this, pooled);
            }
        }
        finally
        {
            _readerLock.Release();
        }

        var conn = OpenReader(_dbPath);
        ApplyConnectionPragmas(conn);
        return new ReaderLease(this, conn);
    }

    internal void ReleaseWriterLock() => _writerLock.Release();

    internal void ReturnReader(SqliteConnection conn)
    {
        if (_disposed)
        {
            conn.Dispose();
            return;
        }
        _readerLock.Wait();
        try
        {
            _readers.Add(conn);
        }
        finally
        {
            _readerLock.Release();
        }
    }

    /// <summary>
    /// Remove the file row and both FTS rows for <paramref name="fileId"/>.
    /// Caller supplies the writer scope.
    /// </summary>
    public static void DeleteFile(WriterScope scope, long fileId)
    {
        if (scope is null) throw new ArgumentNullException(nameof(scope));
        var conn = scope.Connection;

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = scope.Transaction;
            cmd.CommandText = "DELETE FROM code_fts WHERE rowid = $id;";
            cmd.Parameters.AddWithValue("$id", fileId);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = scope.Transaction;
            cmd.CommandText = "DELETE FROM prose_fts WHERE file_id = $id;";
            cmd.Parameters.AddWithValue("$id", fileId);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = scope.Transaction;
            cmd.CommandText = "DELETE FROM files WHERE file_id = $id;";
            cmd.Parameters.AddWithValue("$id", fileId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Upsert the file row keyed by <paramref name="path"/> and replace its
    /// <c>code_fts</c> content. Returns the assigned <c>file_id</c>.
    /// </summary>
    public static long UpsertFile(
        WriterScope scope,
        string path,
        long mtimeUtc,
        long sizeBytes,
        ReadOnlySpan<byte> sha256,
        string? language,
        long indexedAt,
        string content)
    {
        if (scope is null) throw new ArgumentNullException(nameof(scope));
        var conn = scope.Connection;

        long fileId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = scope.Transaction;
            cmd.CommandText = """
                INSERT INTO files(path, mtime_utc, size_bytes, sha256, language, indexed_at)
                VALUES($path, $mtime, $size, $sha, $lang, $at)
                ON CONFLICT(path) DO UPDATE SET
                    mtime_utc = excluded.mtime_utc,
                    size_bytes = excluded.size_bytes,
                    sha256 = excluded.sha256,
                    language = excluded.language,
                    indexed_at = excluded.indexed_at
                RETURNING file_id;
                """;
            cmd.Parameters.AddWithValue("$path", path);
            cmd.Parameters.AddWithValue("$mtime", mtimeUtc);
            cmd.Parameters.AddWithValue("$size", sizeBytes);
            cmd.Parameters.Add("$sha", SqliteType.Blob).Value = sha256.ToArray();
            cmd.Parameters.AddWithValue("$lang", (object?)language ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", indexedAt);
            fileId = Convert.ToInt64(cmd.ExecuteScalar());
        }

        // Replace the code_fts row. FTS5 external-content is not used here — we
        // INSERT the raw content into the virtual table and rely on the
        // rowid=file_id coupling to recover the text during query.
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = scope.Transaction;
            cmd.CommandText = "DELETE FROM code_fts WHERE rowid = $id;";
            cmd.Parameters.AddWithValue("$id", fileId);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = scope.Transaction;
            cmd.CommandText = "INSERT INTO code_fts(rowid, content) VALUES($id, $content);";
            cmd.Parameters.AddWithValue("$id", fileId);
            cmd.Parameters.AddWithValue("$content", content);
            cmd.ExecuteNonQuery();
        }

        return fileId;
    }

    /// <summary>
    /// Look up the <c>sha256</c> currently recorded for <paramref name="path"/>.
    /// Returns an empty array when absent.
    /// </summary>
    public byte[] TryGetShaByPath(string path)
    {
        ThrowIfDisposed();
        using var cmd = _writer.CreateCommand();
        cmd.CommandText = "SELECT sha256 FROM files WHERE path = $p;";
        cmd.Parameters.AddWithValue("$p", path);
        var result = cmd.ExecuteScalar();
        return result is byte[] bytes ? bytes : Array.Empty<byte>();
    }

    /// <summary>
    /// Run an FTS5 <c>MATCH</c> query against <c>code_fts</c> and return the
    /// candidate <c>file_id</c>s. Returns every row when
    /// <paramref name="matchExpression"/> is <c>null</c> or empty — callers
    /// use that path for the "no trigrams extractable" fallback.
    /// </summary>
    public async ValueTask<IReadOnlyList<long>> QueryCodeCandidatesAsync(
        string? matchExpression,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var lease = await RentReaderAsync(cancellationToken).ConfigureAwait(false);
        using var cmd = lease.Connection.CreateCommand();
        if (string.IsNullOrEmpty(matchExpression))
        {
            cmd.CommandText = "SELECT file_id FROM files ORDER BY file_id;";
        }
        else
        {
            cmd.CommandText = "SELECT rowid FROM code_fts WHERE code_fts MATCH $q ORDER BY rowid;";
            cmd.Parameters.AddWithValue("$q", matchExpression);
        }

        var list = new List<long>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(reader.GetInt64(0));
        return list;
    }

    /// <summary>
    /// Fetch <c>(path, content)</c> for the given <paramref name="fileIds"/>.
    /// Order matches <paramref name="fileIds"/>.
    /// </summary>
    public async ValueTask<IReadOnlyList<FileRow>> GetFilesAsync(
        IReadOnlyList<long> fileIds,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (fileIds.Count == 0) return Array.Empty<FileRow>();

        await using var lease = await RentReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new Dictionary<long, FileRow>(fileIds.Count);

        // Batch in chunks to stay well within SQLite's default 999-parameter cap.
        const int BatchSize = 500;
        for (var offset = 0; offset < fileIds.Count; offset += BatchSize)
        {
            var count = Math.Min(BatchSize, fileIds.Count - offset);
            using var cmd = lease.Connection.CreateCommand();
            var parms = new string[count];
            for (var i = 0; i < count; i++)
            {
                parms[i] = $"$p{i}";
                cmd.Parameters.AddWithValue(parms[i], fileIds[offset + i]);
            }
            cmd.CommandText =
                $"SELECT f.file_id, f.path, c.content FROM files f JOIN code_fts c ON c.rowid = f.file_id WHERE f.file_id IN ({string.Join(',', parms)});";

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetInt64(0);
                var path = reader.GetString(1);
                var content = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                rows[id] = new FileRow(id, path, content);
            }
        }

        var result = new List<FileRow>(fileIds.Count);
        foreach (var id in fileIds)
        {
            if (rows.TryGetValue(id, out var row)) result.Add(row);
        }
        return result;
    }

    /// <summary>
    /// Return every <c>(path, sha256)</c> pair from the <c>files</c> table in
    /// a single query. Used by the reconciliation pass to diff the index
    /// contents against git's file set without N individual round-trips.
    /// </summary>
    public async ValueTask<IReadOnlyDictionary<string, byte[]>> GetAllPathsWithShaAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var lease = await RentReaderAsync(cancellationToken).ConfigureAwait(false);
        using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = "SELECT path, sha256 FROM files ORDER BY path;";
        var dict = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var path = reader.GetString(0);
            var sha = reader.IsDBNull(1) ? Array.Empty<byte>() : (byte[])reader[1];
            dict[path] = sha;
        }
        return dict;
    }

    /// <summary>
    /// Return the <c>file_id</c> for the given <paramref name="path"/>, or
    /// <c>null</c> when no such file is indexed.
    /// </summary>
    /// <remarks>
    /// Used by <c>FileDeleted</c> processing in the incremental indexer to
    /// resolve the ID before calling <see cref="DeleteFile"/>.
    /// </remarks>
    public long? LookupFileIdByPath(string path)
    {
        ThrowIfDisposed();
        using var cmd = _writer.CreateCommand();
        cmd.CommandText = "SELECT file_id FROM files WHERE path = $p;";
        cmd.Parameters.AddWithValue("$p", path);
        var result = cmd.ExecuteScalar();
        return result is long id ? id : null;
    }

    /// <summary>
    /// Batch-delete multiple files in one transaction. Removes from
    /// <c>code_fts</c>, <c>prose_fts</c>, and <c>files</c> for each ID.
    /// </summary>
    /// <remarks>
    /// Used when a branch switch removes many files at once. Each call to
    /// <see cref="DeleteFile"/> issues three statements; this method batches
    /// them for efficiency but delegates per-row to the same logic.
    /// </remarks>
    public static void BulkDeleteFiles(WriterScope scope, IReadOnlyList<long> fileIds)
    {
        if (scope is null) throw new ArgumentNullException(nameof(scope));
        if (fileIds is null) throw new ArgumentNullException(nameof(fileIds));
        foreach (var id in fileIds)
            DeleteFile(scope, id);
    }

    /// <summary>Enumerate every <c>(file_id, path)</c> in stable order.</summary>
    public async ValueTask<IReadOnlyList<(long FileId, string Path)>> ListFilesAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var lease = await RentReaderAsync(cancellationToken).ConfigureAwait(false);
        using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = "SELECT file_id, path FROM files ORDER BY path;";
        var list = new List<(long, string)>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add((reader.GetInt64(0), reader.GetString(1)));
        return list;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { await _writer.DisposeAsync().ConfigureAwait(false); } catch { }

        SqliteConnection[] readers;
        await _readerLock.WaitAsync().ConfigureAwait(false);
        try
        {
            readers = _readers.ToArray();
            _readers.Clear();
        }
        finally
        {
            _readerLock.Release();
        }
        foreach (var r in readers)
        {
            try { await r.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        _writerLock.Dispose();
        _readerLock.Dispose();

        // Help the caller-initiated WAL checkpoint on close by nulling out any
        // finalizer-retained references — the wrapper has no unmanaged state
        // of its own beyond the SQLite handles already disposed above.
    }

    // ----- helpers -----

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SqliteIndex));
    }

    private static SqliteConnection OpenWriter(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        };
        var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        return conn;
    }

    private static SqliteConnection OpenReader(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        };
        var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        return conn;
    }

    private static void ApplyConnectionPragmas(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA temp_store=MEMORY;
            PRAGMA mmap_size=268435456;
            """;
        cmd.ExecuteNonQuery();
    }

    private static bool SchemaMatches(SqliteConnection conn, out int version)
    {
        version = 0;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = $k;";
            cmd.Parameters.AddWithValue("$k", SqliteSchema.MetaKey_SchemaVersion);
            var raw = cmd.ExecuteScalar() as string;
            if (raw is null) return false;
            if (!int.TryParse(raw, out version)) return false;
            return version == SqliteSchema.Version;
        }
        catch (SqliteException)
        {
            // meta table doesn't exist yet
            return false;
        }
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = SqliteSchema.Ddl;
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO meta(key, value) VALUES($k, $v)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            cmd.Parameters.AddWithValue("$k", SqliteSchema.MetaKey_SchemaVersion);
            cmd.Parameters.AddWithValue("$v", SqliteSchema.Version.ToString());
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void DeleteDbFiles(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var p = dbPath + suffix;
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
        }
    }
}

/// <summary>Row projection returned by <see cref="SqliteIndex.GetFilesAsync"/>.</summary>
public sealed record FileRow(long FileId, string Path, string Content);

/// <summary>
/// Exclusive writer scope; all work happens on one transaction that commits
/// on dispose unless <see cref="Fail"/> was called.
/// </summary>
public sealed class WriterScope : IAsyncDisposable
{
    private readonly SqliteIndex _owner;
    private SqliteTransaction? _transaction;
    private bool _failed;

    internal WriterScope(SqliteIndex owner, SqliteTransaction transaction)
    {
        _owner = owner;
        _transaction = transaction;
    }

    /// <summary>Underlying writer connection. Valid until disposal.</summary>
    internal SqliteConnection Connection => _transaction?.Connection
        ?? throw new ObjectDisposedException(nameof(WriterScope));

    /// <summary>Underlying transaction; always non-null while the scope is open.</summary>
    internal SqliteTransaction Transaction => _transaction
        ?? throw new ObjectDisposedException(nameof(WriterScope));

    /// <summary>Mark the scope so that dispose rolls back instead of committing.</summary>
    public void Fail() => _failed = true;

    public async ValueTask DisposeAsync()
    {
        var tx = _transaction;
        _transaction = null;
        if (tx is null)
        {
            _owner.ReleaseWriterLock();
            return;
        }

        try
        {
            if (_failed) tx.Rollback();
            else tx.Commit();
        }
        finally
        {
            await tx.DisposeAsync().ConfigureAwait(false);
            _owner.ReleaseWriterLock();
        }
    }
}

/// <summary>
/// Short-lived reader-connection lease. The connection returns to the pool on
/// dispose.
/// </summary>
public sealed class ReaderLease : IAsyncDisposable
{
    private readonly SqliteIndex _owner;
    private SqliteConnection? _conn;

    internal ReaderLease(SqliteIndex owner, SqliteConnection conn)
    {
        _owner = owner;
        _conn = conn;
    }

    internal SqliteConnection Connection => _conn
        ?? throw new ObjectDisposedException(nameof(ReaderLease));

    public ValueTask DisposeAsync()
    {
        var conn = _conn;
        _conn = null;
        if (conn is not null) _owner.ReturnReader(conn);
        return ValueTask.CompletedTask;
    }
}
