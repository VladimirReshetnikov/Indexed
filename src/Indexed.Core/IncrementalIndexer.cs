using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Extractors;
using Indexed.Targets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Indexed.Core;

/// <summary>
/// Single-threaded background worker that drains <see cref="DebouncingEventQueue"/>
/// and applies incremental index updates: per-file upserts, optional
/// revision-diff expansion, and periodic reconciliation.
/// </summary>
/// <remarks>
/// <para>
/// Architecture: one long-running <see cref="Task"/> drains batches from the
/// queue. Each batch is processed inside one or more <see cref="WriterScope"/>s.
/// <see cref="SqliteIndex"/> serializes all writers via
/// <see cref="SqliteIndex.BeginWriteAsync"/>, so concurrent writers from
/// other subsystems (full-scan at startup, FTS merge in
/// <see cref="IndexOptimizer"/>) are safe but cannot overlap.
/// </para>
/// <para>
/// Event processing:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="FileChanged"/> — resolve the logical path through the target, read, SHA-compare, upsert if changed.</description></item>
///   <item><description><see cref="FileDeleted"/> — look up <c>file_id</c> by logical path, delete if present.</description></item>
///   <item><description><see cref="HeadMoved"/> — when the target also implements <see cref="IRevisionDiffTarget"/>, expand the revision diff and advance <c>indexed_head</c>.</description></item>
///   <item><description><see cref="ReconciliationRequested"/> — diff the target's live logical-path set against the index contents to catch missed watcher events or daemon downtime.</description></item>
/// </list>
/// </remarks>
public sealed class IncrementalIndexer : IAsyncDisposable
{
    private readonly IIndexTarget _target;
    private readonly SqliteIndex _index;
    private readonly DebouncingEventQueue _queue;
    private readonly ILogger _logger;
    private readonly ExcludeFilter _excludeFilter;
    private readonly ExtractorRegistry _extractorRegistry;
    private readonly IRevisionDiffTarget? _revisionDiffTarget;
    private readonly IExplicitBinaryPathProvider? _explicitBinaryPathProvider;
    private readonly CancellationTokenSource _cts = new();
    private Task? _workerTask;

    // Cached set of logical paths whose target-specific metadata explicitly
    // marks them as binary. Held as a single IReadOnlySet<string> reference
    // so the hot upsert loop reads it lock-free; refreshed by reference swap.
    private IReadOnlySet<string> _explicitBinaryLogicalPaths = new HashSet<string>(StringComparer.Ordinal);

    // Stable root_id bindings read from the roots table once per daemon start.
    private IReadOnlyDictionary<string, long>? _rootIds;

    /// <summary>
    /// 0 = live, 1 = disposed. Flipped atomically via
    /// <see cref="Interlocked.Exchange(ref int, int)"/> in
    /// <see cref="DisposeAsync"/> so concurrent dispose callers serialize on
    /// the first winner.
    /// </summary>
    private int _disposed;

    /// <summary>
    /// Count of batches currently being processed (0 or 1 in practice, since
    /// there is exactly one worker). Read by <see cref="IsProcessingBatch"/>
    /// to expose an in-flight signal to freshness reporting.
    /// </summary>
    private int _batchesInFlight;

    /// <summary>
    /// <c>true</c> after the worker loop exits due to an unhandled exception.
    /// Read by <c>DaemonHost.BuildStatus</c> to surface a degraded state.
    /// </summary>
    public bool IsFaulted { get; private set; }

    /// <summary>
    /// <c>true</c> while the worker is in the middle of applying a batch
    /// (between <see cref="DebouncingEventQueue.DequeueAsync"/> returning and
    /// the transaction committing). DaemonHost folds this into
    /// <see cref="Indexed.Abstractions.Freshness.IsStale"/> so callers do not
    /// see <c>isStale=false</c> purely because
    /// <see cref="DebouncingEventQueue.PendingCount"/> dropped to zero during
    /// in-flight processing.
    /// </summary>
    public bool IsProcessingBatch => Volatile.Read(ref _batchesInFlight) > 0;

    /// <summary>
    /// Fires after each batch commit so DaemonHost can reset the idle timer.
    /// </summary>
    public event Action? BatchCommitted;

    /// <summary>
    /// Create an incremental indexer.
    /// </summary>
    public IncrementalIndexer(
        IIndexTarget target,
        SqliteIndex index,
        DebouncingEventQueue queue,
        IReadOnlyList<string>? excludeGlobs = null,
        ILogger? logger = null,
        ExtractorRegistry? extractorRegistry = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _logger = logger ?? NullLogger.Instance;
        _excludeFilter = new ExcludeFilter(excludeGlobs);
        _extractorRegistry = extractorRegistry ?? ExtractorRegistry.BuildDefault();
        _revisionDiffTarget = target as IRevisionDiffTarget;
        _explicitBinaryPathProvider = target as IExplicitBinaryPathProvider;
    }

    /// <summary>
    /// Start the background worker task.
    /// </summary>
    public void Start()
    {
        if (_workerTask is not null) return;

        if (_explicitBinaryPathProvider is not null)
        {
            try
            {
                var set = _explicitBinaryPathProvider
                    .GetExplicitBinaryLogicalPathsAsync(files: null, cancellationToken: _cts.Token)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                Volatile.Write(ref _explicitBinaryLogicalPaths, set);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "initial explicit binary-path seed failed; will retry during reconciliation");
            }
        }

        _workerTask = Task.Run(() => WorkerLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Signal shutdown and wait for the worker to drain the current batch.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

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
        var queueDrained = false;

        await EnsureRootIdsAsync(ct).ConfigureAwait(false);

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

            if (batch.Count == 0) { queueDrained = true; break; }

            Interlocked.Increment(ref _batchesInFlight);
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
            }
            finally
            {
                Interlocked.Decrement(ref _batchesInFlight);
            }
        }

        if (ct.IsCancellationRequested || queueDrained)
        {
            _logger.LogInformation("incremental indexer worker stopped (shutdown)");
        }
        else
        {
            IsFaulted = true;
            _logger.LogWarning("incremental indexer worker stopped unexpectedly");
        }
    }

    private async Task ProcessBatchAsync(IReadOnlyList<IndexEvent> batch, CancellationToken ct)
    {
        var toUpsert = new HashSet<string>(StringComparer.Ordinal);
        var toDelete = new HashSet<string>(StringComparer.Ordinal);
        var reconciliationRequested = false;
        string? newRevisionToken = null;

        foreach (var evt in batch)
        {
            switch (evt)
            {
                case HeadMoved hm:
                    var expandedHead = await ExpandHeadMovedAsync(hm, toUpsert, toDelete, ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(expandedHead))
                        newRevisionToken = expandedHead;
                    break;

                case ReconciliationRequested:
                    reconciliationRequested = true;
                    var reconciledRevision = await ExpandReconciliationAsync(toUpsert, toDelete, ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(reconciledRevision))
                        newRevisionToken = reconciledRevision;
                    break;

                case FileChanged fc:
                    if (!_excludeFilter.IsExcluded(fc.LogicalPath))
                        toUpsert.Add(fc.LogicalPath);
                    break;

                case FileDeleted fd:
                    toDelete.Add(fd.LogicalPath);
                    break;
            }
        }

        if (toDelete.Count > 0)
        {
            await using var scope = await _index.BeginWriteAsync(ct).ConfigureAwait(false);
            try
            {
                var deletedIds = new List<long>(toDelete.Count);
                foreach (var logicalPath in toDelete)
                {
                    var id = _index.LookupFileIdByLogicalPath(logicalPath);
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

        var upserted = 0;
        var unchanged = 0;
        var skipped = 0;

        if (toUpsert.Count > 0 || newRevisionToken is not null || reconciliationRequested)
        {
            await using var scope = await _index.BeginWriteAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var logicalPath in toUpsert)
                {
                    ct.ThrowIfCancellationRequested();

                    if (_excludeFilter.IsExcluded(logicalPath))
                    {
                        skipped++;
                        continue;
                    }

                    if (!_target.TryResolveLogicalPath(new LogicalPath(logicalPath), out var file))
                    {
                        _logger.LogDebug("skip {Path}: target could not resolve logical path", logicalPath);
                        skipped++;
                        continue;
                    }

                    if (IsBinary(logicalPath, file.AbsolutePath))
                    {
                        skipped++;
                        continue;
                    }

                    long mtimeUtc;
                    byte[] sha;
                    try
                    {
                        var info = new FileInfo(file.AbsolutePath);
                        if (!info.Exists) { skipped++; continue; }
                        if (info.Length > IndexLimits.MaxIndexableFileBytes) { skipped++; continue; }
                        mtimeUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeSeconds();

                        using (var shaStream = new FileStream(file.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true))
                        {
                            sha = await SHA256.HashDataAsync(shaStream, ct).ConfigureAwait(false);
                        }
                    }
                    catch (IOException ex)
                    {
                        _logger.LogDebug(ex, "skip {Path}: {Message}", logicalPath, ex.Message);
                        skipped++;
                        continue;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        _logger.LogDebug(ex, "skip {Path}: {Message}", logicalPath, ex.Message);
                        skipped++;
                        continue;
                    }

                    var prior = _index.TryGetShaByLogicalPath(logicalPath);
                    if (prior.Length == sha.Length && prior.AsSpan().SequenceEqual(sha))
                    {
                        unchanged++;
                        continue;
                    }

                    byte[] bytes;
                    try
                    {
                        bytes = await File.ReadAllBytesAsync(file.AbsolutePath, ct).ConfigureAwait(false);
                        if (bytes.LongLength > IndexLimits.MaxIndexableFileBytes)
                        {
                            _logger.LogDebug("skip {Path}: grew past size cap after read ({Size} bytes)", logicalPath, bytes.LongLength);
                            skipped++;
                            continue;
                        }
                    }
                    catch (IOException ex)
                    {
                        _logger.LogDebug(ex, "skip {Path}: {Message}", logicalPath, ex.Message);
                        skipped++;
                        continue;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        _logger.LogDebug(ex, "skip {Path}: {Message}", logicalPath, ex.Message);
                        skipped++;
                        continue;
                    }

                    var content = TextDecoder.Decode(bytes);
                    var language = LanguageGuess.FromPath(logicalPath);
                    var proseSpans = _extractorRegistry.Extract(logicalPath, content);

                    var fileId = SqliteIndex.UpsertFile(
                        scope: scope,
                        rootId: GetRootId(file.Root),
                        relativePath: file.RelativePath,
                        logicalPath: logicalPath,
                        mtimeUtc: mtimeUtc,
                        sizeBytes: bytes.LongLength,
                        sha256: sha,
                        language: language,
                        indexedAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        textForTokenization: content);
                    SqliteIndex.ReplaceProseSpans(scope, fileId, proseSpans);

                    upserted++;
                }

                if (newRevisionToken is not null)
                {
                    SqliteIndex.SetMeta(scope, SqliteSchema.MetaKey_IndexedHead, newRevisionToken);
                }

                if (reconciliationRequested)
                {
                    SqliteIndex.SetMeta(
                        scope,
                        SqliteSchema.MetaKey_LastReconciliationAt,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                scope.Fail();
                _logger.LogError(ex, "failed during upsert batch — rolling back");
                throw;
            }
        }

        if (upserted > 0 || toDelete.Count > 0 || reconciliationRequested || newRevisionToken is not null)
        {
            _logger.LogInformation(
                "batch complete: upserted={Upserted} deleted={Deleted} unchanged={Unchanged} skipped={Skipped}",
                upserted, toDelete.Count, unchanged, skipped);
        }
    }

    private async Task<string?> ExpandHeadMovedAsync(
        HeadMoved hm,
        ISet<string> toUpsert,
        ISet<string> toDelete,
        CancellationToken cancellationToken)
    {
        if (_revisionDiffTarget is null)
        {
            _logger.LogDebug("HeadMoved observed for a target without revision-diff support; requesting reconciliation");
            _queue.Enqueue(new ReconciliationRequested());
            return null;
        }

        try
        {
            await _revisionDiffTarget
                .ExpandRevisionChangeAsync(hm.OldHead, hm.NewHead, (ICollection<string>)toUpsert, (ICollection<string>)toDelete, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogDebug(
                "revision moved {Old}->{New}",
                hm.OldHead[..Math.Min(7, hm.OldHead.Length)],
                hm.NewHead[..Math.Min(7, hm.NewHead.Length)]);
            return hm.NewHead;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "revision diff expansion failed for {Old}->{New}; queuing reconciliation",
                hm.OldHead, hm.NewHead);
            _queue.Enqueue(new ReconciliationRequested());
            return null;
        }
    }

    private async Task<string?> ExpandReconciliationAsync(
        ISet<string> toUpsert,
        ISet<string> toDelete,
        CancellationToken ct)
    {
        _logger.LogDebug("running reconciliation");

        var targetFiles = new List<EnumeratedFile>();
        var livePaths = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var file in _target.EnumerateFilesAsync(ct).ConfigureAwait(false))
        {
            targetFiles.Add(file);
            livePaths.Add(file.LogicalPath.Value);
        }

        await RefreshExplicitBinaryLogicalPathsAsync(targetFiles, ct).ConfigureAwait(false);

        var indexed = await _index.GetAllLogicalPathsWithShaAsync(ct).ConfigureAwait(false);

        foreach (var logicalPath in livePaths)
        {
            if (!indexed.ContainsKey(logicalPath))
                toUpsert.Add(logicalPath);
        }

        foreach (var logicalPath in indexed.Keys)
        {
            if (!livePaths.Contains(logicalPath))
                toDelete.Add(logicalPath);
        }

        IReadOnlyDictionary<string, SqliteIndex.IndexedFileStat>? stats = null;
        try { stats = await _index.GetAllLogicalPathsWithStatAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "stat-drift probe skipped"); }

        if (stats is not null)
        {
            foreach (var file in targetFiles)
            {
                var logicalPath = file.LogicalPath.Value;
                if (!stats.TryGetValue(logicalPath, out var stat)) continue;

                try
                {
                    var info = new FileInfo(file.AbsolutePath);
                    if (!info.Exists) continue;
                    var diskMtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeSeconds();
                    if (diskMtime != stat.MtimeUtc || info.Length != stat.SizeBytes)
                        toUpsert.Add(logicalPath);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        var currentRevisionToken = SafeGetCurrentRevisionToken(ct);
        var indexedRevisionToken = _index.GetMeta(SqliteSchema.MetaKey_IndexedHead);
        if (_revisionDiffTarget is not null
            && !string.IsNullOrEmpty(currentRevisionToken)
            && !string.IsNullOrEmpty(indexedRevisionToken)
            && !string.Equals(currentRevisionToken, indexedRevisionToken, StringComparison.Ordinal))
        {
            try
            {
                await _revisionDiffTarget
                    .ExpandRevisionChangeAsync(indexedRevisionToken, currentRevisionToken, (ICollection<string>)toUpsert, (ICollection<string>)toDelete, ct)
                    .ConfigureAwait(false);
                _logger.LogDebug("reconciliation also advanced revision token {Old}->{New}", indexedRevisionToken, currentRevisionToken);
                return currentRevisionToken;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "revision drift expansion failed during reconciliation for {Old}->{New}",
                    indexedRevisionToken, currentRevisionToken);
            }
        }

        _logger.LogDebug("reconciliation: {Upsert} to add/update, {Delete} to remove",
            toUpsert.Count, toDelete.Count);
        return null;
    }

    private async Task EnsureRootIdsAsync(CancellationToken cancellationToken)
    {
        if (_rootIds is not null)
            return;

        await using var scope = await _index.BeginWriteAsync(cancellationToken).ConfigureAwait(false);
        _rootIds = SqliteIndex.UpsertRoots(scope, _target.Roots);
    }

    private async Task RefreshExplicitBinaryLogicalPathsAsync(
        IReadOnlyList<EnumeratedFile>? files,
        CancellationToken cancellationToken)
    {
        if (_explicitBinaryPathProvider is null)
        {
            Volatile.Write(ref _explicitBinaryLogicalPaths, new HashSet<string>(StringComparer.Ordinal));
            return;
        }

        try
        {
            var set = await _explicitBinaryPathProvider
                .GetExplicitBinaryLogicalPathsAsync(files, cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _explicitBinaryLogicalPaths, set);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "explicit binary-path refresh failed");
        }
    }

    private string? SafeGetCurrentRevisionToken(CancellationToken cancellationToken)
    {
        try { return _target.GetCurrentRevisionToken(cancellationToken); }
        catch { return null; }
    }

    private long GetRootId(TargetRoot root)
    {
        if (_rootIds is null)
            throw new InvalidOperationException("root ids have not been initialized");

        var key = TargetPathUtilities.NormalizeForComparison(root.AbsolutePath);
        if (_rootIds.TryGetValue(key, out var rootId))
            return rootId;

        throw new InvalidOperationException($"no root id mapping exists for '{root.AbsolutePath}'");
    }

    private bool IsBinary(string logicalPath, string absolutePath)
    {
        if (Volatile.Read(ref _explicitBinaryLogicalPaths).Contains(logicalPath))
            return true;
        return BinaryFileClassifier.IsLikelyBinary(absolutePath);
    }
}
