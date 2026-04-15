using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Indexed.Core;

/// <summary>
/// Single-threaded background worker that drains <see cref="DebouncingEventQueue"/>
/// and applies incremental index updates: per-file upserts, diff-tree-based HEAD
/// patching, and periodic reconciliation.
/// </summary>
/// <remarks>
/// <para>
/// Architecture: one long-running <see cref="Task"/> drains batches from the
/// queue. Each batch is processed inside a single <see cref="WriterScope"/>
/// (one SQLite transaction). This is the <strong>only writer</strong> to
/// <see cref="SqliteIndex"/> after startup — no concurrent indexing. Matches
/// proposal §10 (single-threaded writer).
/// </para>
/// <para>
/// Event processing:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="FileChanged"/> — read, SHA-compare, upsert if changed.</description></item>
///   <item><description><see cref="FileDeleted"/> — look up file_id, delete if present.</description></item>
///   <item><description><see cref="HeadMoved"/> — <c>git diff-tree</c>, apply diff, update <c>indexed_head</c>.</description></item>
///   <item><description><see cref="ReconciliationRequested"/> — path-set diff against git, emit corrective events.</description></item>
/// </list>
/// </remarks>
public sealed class IncrementalIndexer : IAsyncDisposable
{
    private readonly GitRepository _repo;
    private readonly SqliteIndex _index;
    private readonly DebouncingEventQueue _queue;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<Regex> _excludeRegexes;
    private readonly CancellationTokenSource _cts = new();
    private Task? _workerTask;
    private bool _disposed;

    /// <summary>Maximum file size for indexing (same as FullScanIndexer).</summary>
    private const long MaxIndexableFileBytes = 50L * 1024 * 1024;

    /// <summary>
    /// <c>true</c> after the worker loop exits due to an unhandled exception.
    /// Read by <c>DaemonHost.BuildStatus</c> to surface a degraded state.
    /// </summary>
    public bool IsFaulted { get; private set; }

    /// <summary>
    /// Fires after each batch commit so DaemonHost can reset the idle timer.
    /// </summary>
    public event Action? BatchCommitted;

    /// <summary>
    /// Create an incremental indexer.
    /// </summary>
    public IncrementalIndexer(
        GitRepository repo,
        SqliteIndex index,
        DebouncingEventQueue queue,
        IReadOnlyList<string>? excludeGlobs = null,
        ILogger? logger = null)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _logger = logger ?? NullLogger.Instance;
        _excludeRegexes = CompileExcludes(excludeGlobs);
    }

    /// <summary>
    /// Start the background worker task.
    /// </summary>
    public void Start()
    {
        if (_workerTask is not null) return;
        _workerTask = Task.Run(() => WorkerLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Signal shutdown and wait for the worker to drain the current batch.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _queue.Complete();
        if (_workerTask is not null)
        {
            try { await _workerTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogError(ex, "incremental indexer worker failed"); }
        }
        _cts.Dispose();
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("incremental indexer worker started");

        while (!ct.IsCancellationRequested)
        {
            IReadOnlyList<IndexEvent> batch;
            try
            {
                batch = await _queue.DequeueAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (batch.Count == 0) break; // queue completed

            try
            {
                await ProcessBatchAsync(batch, ct).ConfigureAwait(false);
                BatchCommitted?.Invoke();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "error processing batch of {Count} events", batch.Count);
                // Continue — don't crash the worker loop on transient errors.
            }
        }

        if (ct.IsCancellationRequested)
            _logger.LogInformation("incremental indexer worker stopped (shutdown)");
        else
        {
            IsFaulted = true;
            _logger.LogWarning("incremental indexer worker stopped unexpectedly");
        }
    }

    private async Task ProcessBatchAsync(IReadOnlyList<IndexEvent> batch, CancellationToken ct)
    {
        // Collect file-level work items from all events in the batch.
        var toUpsert = new List<string>();
        var toDelete = new List<string>();

        foreach (var evt in batch)
        {
            switch (evt)
            {
                case HeadMoved hm:
                    ExpandHeadMoved(hm, toUpsert, toDelete);
                    break;

                case ReconciliationRequested:
                    await ExpandReconciliationAsync(toUpsert, toDelete, ct).ConfigureAwait(false);
                    break;

                case FileChanged fc:
                    if (!IsExcluded(fc.RelativePath))
                        toUpsert.Add(fc.RelativePath);
                    break;

                case FileDeleted fd:
                    toDelete.Add(fd.RelativePath);
                    break;
            }
        }

        // Deduplicate: if a path appears in both lists, delete wins (then
        // upsert re-adds if the file actually exists on disk).
        var deleteSet = new HashSet<string>(toDelete, StringComparer.Ordinal);

        // Process deletes.
        if (deleteSet.Count > 0)
        {
            await using var scope = await _index.BeginWriteAsync(ct).ConfigureAwait(false);
            try
            {
                var deletedIds = new List<long>();
                foreach (var path in deleteSet)
                {
                    var id = _index.LookupFileIdByPath(path);
                    if (id.HasValue) deletedIds.Add(id.Value);
                }
                if (deletedIds.Count > 0)
                {
                    SqliteIndex.BulkDeleteFiles(scope, deletedIds);
                    _logger.LogDebug("deleted {Count} files from index", deletedIds.Count);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                scope.Fail();
                _logger.LogError(ex, "failed during delete batch — rolling back");
                throw;
            }
        }

        // Process upserts.
        var upserted = 0;
        var unchanged = 0;
        var skipped = 0;

        if (toUpsert.Count > 0)
        {
            await using var scope = await _index.BeginWriteAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var relPath in toUpsert)
                {
                    ct.ThrowIfCancellationRequested();

                    if (IsExcluded(relPath) || IsBinary(relPath))
                    {
                        skipped++;
                        continue;
                    }

                    var full = Path.Combine(_repo.RepoRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
                    byte[] bytes;
                    long mtimeUtc;
                    try
                    {
                        var info = new FileInfo(full);
                        if (!info.Exists) { skipped++; continue; }
                        if (info.Length > MaxIndexableFileBytes) { skipped++; continue; }
                        mtimeUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeSeconds();
                        bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
                        // Re-check after read: the file may have grown between stat and read.
                        if (bytes.LongLength > MaxIndexableFileBytes)
                        {
                            _logger.LogDebug("skip {Path}: grew past size cap after read ({Size} bytes)", relPath, bytes.LongLength);
                            skipped++;
                            continue;
                        }
                    }
                    catch (IOException ex)
                    {
                        _logger.LogDebug(ex, "skip {Path}: {Message}", relPath, ex.Message);
                        skipped++;
                        continue;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        _logger.LogDebug(ex, "skip {Path}: {Message}", relPath, ex.Message);
                        skipped++;
                        continue;
                    }

                    var sha = SHA256.HashData(bytes);
                    var prior = _index.TryGetShaByPath(relPath);
                    if (prior.Length == sha.Length && prior.AsSpan().SequenceEqual(sha))
                    {
                        unchanged++;
                        continue;
                    }

                    var content = TextDecoder.Decode(bytes);
                    var language = LanguageGuess.FromPath(relPath);

                    SqliteIndex.UpsertFile(
                        scope: scope,
                        path: relPath,
                        mtimeUtc: mtimeUtc,
                        sizeBytes: bytes.LongLength,
                        sha256: sha,
                        language: language,
                        indexedAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        content: content);

                    upserted++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                scope.Fail();
                _logger.LogError(ex, "failed during upsert batch — rolling back");
                throw;
            }
        }

        // Update indexed_head inside a dedicated writer scope so the write
        // is properly serialized with other writer activity.
        string? newHead = null;
        foreach (var evt in batch)
        {
            if (evt is HeadMoved hm) newHead = hm.NewHead;
        }
        if (newHead is not null)
        {
            await using var metaScope = await _index.BeginWriteAsync(ct).ConfigureAwait(false);
            _index.SetMeta(SqliteSchema.MetaKey_IndexedHead, newHead);
        }

        if (upserted > 0 || deleteSet.Count > 0)
        {
            _logger.LogInformation(
                "batch complete: upserted={Upserted} deleted={Deleted} unchanged={Unchanged} skipped={Skipped}",
                upserted, deleteSet.Count, unchanged, skipped);
        }
    }

    private void ExpandHeadMoved(HeadMoved hm, List<string> toUpsert, List<string> toDelete)
    {
        IReadOnlyList<DiffTreeEntry> diff;
        try
        {
            diff = _repo.GetDiffTree(hm.OldHead, hm.NewHead);
        }
        catch (GitProcessException ex)
        {
            _logger.LogWarning(ex,
                "GetDiffTree({Old}, {New}) failed — queuing full reconciliation",
                hm.OldHead, hm.NewHead);
            _queue.Enqueue(new ReconciliationRequested());
            return;
        }

        foreach (var entry in diff)
        {
            switch (entry.Status)
            {
                case DiffStatus.Added:
                case DiffStatus.Modified:
                    toUpsert.Add(entry.Path);
                    break;

                case DiffStatus.Deleted:
                    toDelete.Add(entry.Path);
                    break;

                case DiffStatus.Renamed:
                    if (entry.OldPath is not null) toDelete.Add(entry.OldPath);
                    toUpsert.Add(entry.Path);
                    break;

                case DiffStatus.Copied:
                    toUpsert.Add(entry.Path);
                    break;
            }
        }

        _logger.LogDebug("HeadMoved {Old}->{New}: {Count} diff entries",
            hm.OldHead[..Math.Min(7, hm.OldHead.Length)],
            hm.NewHead[..Math.Min(7, hm.NewHead.Length)],
            diff.Count);
    }

    private async Task ExpandReconciliationAsync(
        List<string> toUpsert, List<string> toDelete, CancellationToken ct)
    {
        _logger.LogDebug("running reconciliation");

        // A: what git knows about.
        var gitFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in _repo.EnumerateFiles())
            gitFiles.Add(p);

        // B: what the index has.
        var indexed = await _index.GetAllPathsWithShaAsync(ct).ConfigureAwait(false);

        // A \ B: files missing from index.
        foreach (var p in gitFiles)
        {
            if (!indexed.ContainsKey(p))
                toUpsert.Add(p);
        }

        // B \ A: files in index but not in git.
        foreach (var p in indexed.Keys)
        {
            if (!gitFiles.Contains(p))
                toDelete.Add(p);
        }

        // Check HEAD drift.
        var currentHead = SafeGetHead();
        var indexedHead = _index.GetMeta(SqliteSchema.MetaKey_IndexedHead);
        if (!string.IsNullOrEmpty(currentHead)
            && !string.IsNullOrEmpty(indexedHead)
            && !string.Equals(currentHead, indexedHead, StringComparison.Ordinal))
        {
            // Feed through HeadMoved — this will expand into file-level events.
            ExpandHeadMoved(new HeadMoved(indexedHead, currentHead), toUpsert, toDelete);
        }

        _logger.LogDebug("reconciliation: {Upsert} to add/update, {Delete} to remove",
            toUpsert.Count, toDelete.Count);
    }

    private string SafeGetHead()
    {
        try { return _repo.GetHeadSha(); }
        catch { return string.Empty; }
    }

    private bool IsBinary(string relPath)
    {
        return _repo.IsLikelyBinary(relPath);
    }

    private bool IsExcluded(string relPath)
    {
        if (_excludeRegexes.Count == 0) return false;
        var norm = relPath.Replace('\\', '/');
        foreach (var rx in _excludeRegexes)
        {
            if (rx.IsMatch(norm)) return true;
        }
        return false;
    }

    private static IReadOnlyList<Regex> CompileExcludes(IReadOnlyList<string>? globs)
    {
        if (globs is null || globs.Count == 0) return Array.Empty<Regex>();
        var list = new Regex[globs.Count];
        for (var i = 0; i < globs.Count; i++) list[i] = PathGlob.Compile(globs[i]);
        return list;
    }
}
