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

    // Dedicated reader connection for synchronous read methods (GetMeta,
    // GetFileCount, TryGetShaByPath, LookupFileIdByPath). These methods are
    // called from HTTP request threads, timer callbacks, and the indexer
    // worker — potentially concurrently with an open WriterScope. A separate
    // reader connection avoids racing commands on the writer connection.
    // Access is serialized through _syncReaderGate.
    private readonly SqliteConnection _syncReader;
    private readonly object _syncReaderGate = new();

    /// <summary>
    /// 0 = live, 1 = disposed. Flipped atomically via
    /// <see cref="Interlocked.Exchange(ref int, int)"/> in
    /// <see cref="DisposeAsync"/> so concurrent disposal callers serialize
    /// on the first winner; <see cref="ThrowIfDisposed"/> uses
    /// <see cref="Volatile.Read(ref int)"/> for ordered reads from HTTP
    /// threads after the daemon enters shutdown.
    /// </summary>
    private int _disposed;

    private SqliteIndex(string dbPath, SqliteConnection writer, SqliteConnection syncReader)
    {
        _dbPath = dbPath;
        _writer = writer;
        _syncReader = syncReader;
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
        SqliteConnection writer;
        try
        {
            writer = OpenWriter(dbPath);
        }
        catch (SqliteException)
        {
            // Corrupted DB file (not a valid SQLite database). Delete and
            // recreate from scratch — the index is a derived artifact.
            DeleteDbFiles(dbPath);
            needsCreate = true;
            writer = OpenWriter(dbPath);
        }

        try
        {
            try
            {
                ApplyConnectionPragmas(writer);
            }
            catch (SqliteException)
            {
                // Corrupt DB passes Open (SQLite opens lazily) but fails on
                // first PRAGMA. Delete, recreate.
                writer.Dispose();
                DeleteDbFiles(dbPath);
                needsCreate = true;
                writer = OpenWriter(dbPath);
                ApplyConnectionPragmas(writer);
            }

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

            // Open a dedicated reader connection for synchronous read methods
            // that may be called concurrently with the writer. Uses private
            // cache mode (not shared) so that WAL-mode concurrent reads are
            // not blocked by an active writer transaction on the same table.
            var syncReader = OpenSyncReader(dbPath);
            try
            {
                ApplyConnectionPragmas(syncReader);
            }
            catch
            {
                syncReader.Dispose();
                throw;
            }

            var index = new SqliteIndex(dbPath, writer, syncReader) { SchemaVersion = version };
            return index;
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    /// <summary>Read a <c>meta</c> KV. Returns <c>null</c> when absent.</summary>
    /// <remarks>Thread-safe: uses the dedicated sync reader connection.</remarks>
    public string? GetMeta(string key)
    {
        ThrowIfDisposed();
        lock (_syncReaderGate)
        {
            using var cmd = _syncReader.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = $k;";
            cmd.Parameters.AddWithValue("$k", key);
            var result = cmd.ExecuteScalar();
            return result is string s ? s : null;
        }
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
    /// <remarks>Thread-safe: uses the dedicated sync reader connection.</remarks>
    public long GetFileCount()
    {
        ThrowIfDisposed();
        lock (_syncReaderGate)
        {
            using var cmd = _syncReader.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM files;";
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
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
        try
        {
            ApplyConnectionPragmas(conn);
        }
        catch
        {
            conn.Dispose();
            throw;
        }
        return new ReaderLease(this, conn);
    }

    internal void ReleaseWriterLock() => _writerLock.Release();

    internal void ReturnReader(SqliteConnection conn)
    {
        if (Volatile.Read(ref _disposed) != 0)
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
    /// Upsert the file row keyed by <paramref name="path"/> and replace the
    /// corresponding <c>code_fts</c> posting list. Returns the assigned
    /// <c>file_id</c>.
    /// </summary>
    /// <param name="scope">Open writer scope. Required.</param>
    /// <param name="path">Repository-relative POSIX path.</param>
    /// <param name="mtimeUtc">Stat'd mtime, seconds since Unix epoch, UTC.</param>
    /// <param name="sizeBytes">File length in bytes at index time.</param>
    /// <param name="sha256">Raw 32-byte content hash.</param>
    /// <param name="language">Best-guess language slug, or <c>null</c>.</param>
    /// <param name="indexedAt">Wall-clock timestamp, seconds since Unix epoch, UTC.</param>
    /// <param name="textForTokenization">
    /// Decoded file text fed to the FTS5 trigram tokenizer. Under schema v2
    /// <c>code_fts</c> is <em>contentless</em> (<c>content = ''</c>): this
    /// parameter is tokenized at write time to build the posting list, and
    /// then discarded — only trigram postings are persisted. Snippet
    /// rehydration at query time re-reads the file from disk via
    /// <see cref="FileContentProvider"/>. Callers should pass the raw
    /// decoded text (not a pre-chunked subset).
    /// </param>
    public static long UpsertFile(
        WriterScope scope,
        string path,
        long mtimeUtc,
        long sizeBytes,
        ReadOnlySpan<byte> sha256,
        string? language,
        long indexedAt,
        string textForTokenization)
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

        // Replace the code_fts posting list. The column name "content" below
        // is the FTS5 virtual-table column alias, not a stored value — under
        // the contentless v2 schema (content = '') FTS5 tokenizes the text
        // into trigrams and stores only the inverted index. The raw string is
        // never persisted; CodeQueryExecutor rehydrates snippets from disk.
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
            cmd.Parameters.AddWithValue("$content", textForTokenization);
            cmd.ExecuteNonQuery();
        }

        return fileId;
    }

    /// <summary>
    /// Look up the <c>sha256</c> currently recorded for <paramref name="path"/>.
    /// Returns an empty array when absent.
    /// </summary>
    /// <remarks>Thread-safe: uses the dedicated sync reader connection.</remarks>
    public byte[] TryGetShaByPath(string path)
    {
        ThrowIfDisposed();
        lock (_syncReaderGate)
        {
            using var cmd = _syncReader.CreateCommand();
            cmd.CommandText = "SELECT sha256 FROM files WHERE path = $p;";
            cmd.Parameters.AddWithValue("$p", path);
            var result = cmd.ExecuteScalar();
            return result is byte[] bytes ? bytes : Array.Empty<byte>();
        }
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
    /// Fetch <c>(file_id, path, sha256)</c> rows for the given
    /// <paramref name="fileIds"/>. Order matches <paramref name="fileIds"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// As of schema version 2 this method does not return file content —
    /// <see cref="FileRow"/> carries only the identity/path/hash tuple.
    /// Callers that need content (e.g. <see cref="CodeQueryExecutor"/>)
    /// read it from the working tree via <see cref="FileContentProvider"/>.
    /// The rationale — dropping the stored content cuts the index size by
    /// roughly the source tree size — lives in
    /// <c>Indexed-Size-Reduction-SafeNearTerm-Plan.md §Workstream C</c>.
    /// </para>
    /// </remarks>
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
                $"SELECT file_id, path, sha256 FROM files WHERE file_id IN ({string.Join(',', parms)});";

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetInt64(0);
                var path = reader.GetString(1);
                var sha = reader.IsDBNull(2) ? Array.Empty<byte>() : (byte[])reader[2];
                rows[id] = new FileRow(id, path, sha);
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
    /// <para>
    /// Thread-safe: uses the dedicated sync reader connection. Reads committed
    /// state; callers inside a <see cref="WriterScope"/> should be aware that
    /// uncommitted deletes from the same scope are not visible to this reader.
    /// In practice the incremental indexer calls this <em>before</em> issuing
    /// deletes, so the lookup is always against committed state.
    /// </para>
    /// </remarks>
    public long? LookupFileIdByPath(string path)
    {
        ThrowIfDisposed();
        lock (_syncReaderGate)
        {
            using var cmd = _syncReader.CreateCommand();
            cmd.CommandText = "SELECT file_id FROM files WHERE path = $p;";
            cmd.Parameters.AddWithValue("$p", path);
            var result = cmd.ExecuteScalar();
            return result is long id ? id : null;
        }
    }

    /// <summary>
    /// Batch-delete multiple files in one transaction. Removes from
    /// <c>code_fts</c>, <c>prose_fts</c>, and <c>files</c> for each ID.
    /// </summary>
    /// <remarks>
    /// Used when a branch switch removes many files at once. For small counts
    /// (&lt;= 4) delegates to <see cref="DeleteFile"/>; larger batches use
    /// parameterized <c>DELETE ... WHERE IN</c> clauses chunked at 500
    /// parameters to stay under SQLite's default
    /// <c>SQLITE_MAX_VARIABLE_NUMBER</c> (999).
    /// </remarks>
    public static void BulkDeleteFiles(WriterScope scope, IReadOnlyList<long> fileIds)
    {
        if (scope is null) throw new ArgumentNullException(nameof(scope));
        if (fileIds is null) throw new ArgumentNullException(nameof(fileIds));
        if (fileIds.Count == 0) return;

        // Small batches fall back to per-file delete for simplicity.
        if (fileIds.Count <= 4)
        {
            foreach (var id in fileIds)
                DeleteFile(scope, id);
            return;
        }

        // Batch with parameterized IN clauses, chunked to stay under SQLite's
        // default SQLITE_MAX_VARIABLE_NUMBER (999).
        const int chunkSize = 500;
        var conn = scope.Connection;

        for (var offset = 0; offset < fileIds.Count; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, fileIds.Count - offset);
            var paramNames = new string[count];
            for (var i = 0; i < count; i++)
                paramNames[i] = $"$id{i}";
            var inClause = string.Join(',', paramNames);

            void BindAndExecute(string sql)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = scope.Transaction;
                cmd.CommandText = sql;
                for (var i = 0; i < count; i++)
                    cmd.Parameters.AddWithValue(paramNames[i], fileIds[offset + i]);
                cmd.ExecuteNonQuery();
            }

            BindAndExecute($"DELETE FROM code_fts WHERE rowid IN ({inClause});");
            BindAndExecute($"DELETE FROM prose_fts WHERE file_id IN ({inClause});");
            BindAndExecute($"DELETE FROM files WHERE file_id IN ({inClause});");
        }
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

    /// <summary>
    /// Run a bounded FTS5 segment merge against <c>code_fts</c> and
    /// <c>prose_fts</c>. Each call consumes at most <paramref name="pageBudget"/>
    /// pages of work per table, so a single call is guaranteed to release the
    /// writer lock within a small, bounded time window. Repeat calls
    /// approximate a full <c>optimize</c> without the stall.
    /// </summary>
    /// <param name="pageBudget">
    /// Upper bound on the number of FTS5 data pages the merger may touch per
    /// table. Must be positive. Typical values: 256 for idle-time work, 1024
    /// for a final merge on shutdown.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the wait for the writer lock. Merge statements themselves run
    /// synchronously once the transaction is open; SQLite does not honor
    /// cancellation mid-statement.
    /// </param>
    /// <remarks>
    /// <para>
    /// Acquires the standard <see cref="BeginWriteAsync"/> scope so merges
    /// serialize correctly with <see cref="Indexed.Core.IncrementalIndexer"/>
    /// commits. Commits on success; on exception the transaction is rolled
    /// back via <see cref="WriterScope.Fail"/> before the scope disposes.
    /// </para>
    /// <para>
    /// The FTS5 merge command is
    /// <c>INSERT INTO &lt;fts&gt;(&lt;fts&gt;, rank) VALUES('merge', N)</c>
    /// where <c>rank</c> is the built-in FTS5 virtual column and the positive
    /// <c>N</c> caps the per-call page budget (see
    /// <see href="https://sqlite.org/fts5.html#the_merge_command"/>). A
    /// negative <c>N</c> (unbounded merge) is intentionally not supported
    /// here — the whole point of this method is to bound writer-lock hold
    /// time.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Background optimizer tick: a bounded merge to reclaim segment bloat.
    /// await index.RunFts5MergeAsync(pageBudget: 512, ct).ConfigureAwait(false);
    /// </code>
    /// </example>
    public async ValueTask RunFts5MergeAsync(int pageBudget, CancellationToken cancellationToken = default)
    {
        if (pageBudget <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageBudget), pageBudget, "pageBudget must be positive");

        ThrowIfDisposed();
        await using var scope = await BeginWriteAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var conn = scope.Connection;

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = scope.Transaction;
                cmd.CommandText = "INSERT INTO code_fts(code_fts, rank) VALUES('merge', $n);";
                cmd.Parameters.AddWithValue("$n", pageBudget);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = scope.Transaction;
                cmd.CommandText = "INSERT INTO prose_fts(prose_fts, rank) VALUES('merge', $n);";
                cmd.Parameters.AddWithValue("$n", pageBudget);
                cmd.ExecuteNonQuery();
            }
        }
        catch
        {
            scope.Fail();
            throw;
        }
    }

    /// <summary>
    /// Default deadline for the WAL-checkpoint-on-close step inside
    /// <see cref="DisposeAsync"/>. The checkpoint is best-effort: if an
    /// in-flight reader holds the WAL from truncating within this window,
    /// close proceeds anyway — the next daemon start naturally truncates
    /// the WAL on its first write.
    /// </summary>
    public static TimeSpan DefaultShutdownTimeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Close the writer, sync reader, and every pooled reader. Idempotent
    /// under concurrent callers. Uses <see cref="DefaultShutdownTimeout"/>
    /// as the deadline for the WAL checkpoint.
    /// </summary>
    public ValueTask DisposeAsync() => DisposeAsync(DefaultShutdownTimeout);

    /// <summary>
    /// Variant of <see cref="DisposeAsync()"/> with an explicit
    /// WAL-checkpoint deadline. Primarily for tests and callers with
    /// bespoke shutdown budgets; production code should use the
    /// parameterless form.
    /// </summary>
    public async ValueTask DisposeAsync(TimeSpan shutdownTimeout)
    {
        // Atomic idempotency — the first dispose wins; racers bail out.
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        // WAL checkpoint: flush the WAL to the main DB file before closing.
        // This bounds the -wal file size across daemon restarts and gives
        // the next open a clean start. The checkpoint runs on a background
        // thread and we only wait shutdownTimeout for it — a pathological
        // contending reader cannot wedge shutdown indefinitely. Any
        // un-checkpointed WAL is flushed on next open and does not risk
        // data loss (the index is a derived artifact in any case).
        try
        {
            var checkpointTask = Task.Run(() =>
            {
                using var cmd = _writer.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cmd.ExecuteNonQuery();
            });
            var completed = await Task.WhenAny(checkpointTask, Task.Delay(shutdownTimeout))
                .ConfigureAwait(false);
            // Swallow faults from the checkpoint itself — best-effort.
            if (completed == checkpointTask)
                try { await checkpointTask.ConfigureAwait(false); } catch { }
        }
        catch { /* best-effort — the index is a derived artifact */ }

        try { await _writer.DisposeAsync().ConfigureAwait(false); } catch { }
        try { await _syncReader.DisposeAsync().ConfigureAwait(false); } catch { }

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
    }

    // ----- helpers -----

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SqliteIndex));
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

    /// <summary>
    /// Open a reader with <see cref="SqliteCacheMode.Private"/> so that WAL
    /// concurrent reads are not blocked by an active writer transaction.
    /// Used exclusively for the <see cref="_syncReader"/> connection which
    /// must tolerate being called while a <see cref="WriterScope"/> is open.
    /// </summary>
    private static SqliteConnection OpenSyncReader(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        return conn;
    }

    private static void ApplyConnectionPragmas(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        // PRAGMA page_size must run before any writes; SQLite silently ignores
        // it on an existing DB. Left here so that cold rebuilds (initial create
        // or PR-3 schema bump) pick up the larger page size, which packs FTS5
        // postings slightly better on the kinds of content we index.
        cmd.CommandText = """
            PRAGMA page_size=8192;
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

/// <summary>
/// Row projection returned by <see cref="SqliteIndex.GetFilesAsync"/>. Under
/// schema version 2 the FTS5 index is contentless; callers must read file
/// text from the working tree at query time via
/// <see cref="FileContentProvider"/>. The <see cref="Sha256"/> hash is the
/// at-index-time content hash — consumers can use it to detect staleness
/// when the on-disk file differs from what was indexed.
/// </summary>
/// <param name="FileId">Primary key from the <c>files</c> table.</param>
/// <param name="Path">Repository-relative POSIX path.</param>
/// <param name="Sha256">At-index-time SHA-256 of the file content.</param>
public sealed record FileRow(long FileId, string Path, byte[] Sha256);

/// <summary>
/// Exclusive writer scope; all work happens on one transaction that commits
/// on dispose unless <see cref="Fail"/> was called.
/// </summary>
public sealed class WriterScope : IAsyncDisposable
{
    private readonly SqliteIndex _owner;
    private SqliteTransaction? _transaction;
    private bool _failed;

    /// <summary>
    /// 0 = live, 1 = dispose started/completed. Flipped atomically so that
    /// a redundant <c>DisposeAsync</c> (e.g. explicit <c>using</c> plus a
    /// finally-block call in a caller, or a caller that awaits twice) is a
    /// no-op instead of releasing the writer semaphore a second time —
    /// that would let a second writer acquire the lock while a legitimately
    /// live scope still holds it.
    /// </summary>
    private int _disposed;

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
        // Idempotent dispose. The first caller performs the commit/rollback
        // and releases the writer semaphore exactly once; subsequent callers
        // fall through without touching the lock. `_transaction` nulling
        // alone is not sufficient — a naive second call would still invoke
        // ReleaseWriterLock() via the null-transaction branch.
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

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
