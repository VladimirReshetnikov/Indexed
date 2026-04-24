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
/// Single-threaded indexer that walks every file in an <see cref="IIndexTarget"/>,
/// filters out binaries and oversize blobs, computes SHA-256, and UPSERTs a
/// <c>code_fts</c> row per file using the writer connection on
/// <see cref="SqliteIndex"/>.
/// </summary>
/// <remarks>
/// <para>
/// This class is the daemon's cold-start and rebuild path: when
/// <see cref="DaemonHost"/> detects an empty or schema-mismatched DB it runs a
/// full scan before serving queries. Ongoing edits are handled by
/// <see cref="IncrementalIndexer"/>, but the full scan still owns the
/// authoritative "enumerate everything and restamp the world" pass.
/// </para>
/// <para>
/// Transaction granularity: batches of up to <see cref="BatchFileCount"/>
/// files or <see cref="BatchTimeBudget"/> elapsed time, whichever hits first.
/// A transaction boundary is also forced at the end of the run. SQLite in WAL
/// mode handles hundreds of small transactions without appreciable overhead,
/// but one mega-transaction for tens of thousands of files keeps rebuilds
/// snappy (&lt; 60 s for this repo per proposal §14).
/// </para>
/// <para>
/// Progress reporting: <see cref="IndexProgress"/> instances are emitted via
/// the caller-supplied <see cref="IProgress{T}"/> (optional). Targets may
/// provide an exact total through <see cref="IFileCountHintTarget"/>; when
/// unavailable the total remains <see langword="null"/>.
/// Each indexed file updates both the contentless trigram code surface and,
/// when an extractor is registered for the extension, the stored prose spans
/// in <c>prose_fts</c>.
/// </para>
/// </remarks>
public sealed class FullScanIndexer
{
    /// <summary>Max files per transaction.</summary>
    public const int BatchFileCount = 200;

    /// <summary>Max wall-clock time per transaction.</summary>
    public static readonly TimeSpan BatchTimeBudget = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Files larger than this bypass the indexer. Alias of
    /// <see cref="IndexLimits.MaxIndexableFileBytes"/> retained for
    /// back-compatibility with existing call sites and tests.
    /// </summary>
    public const long MaxIndexableFileBytes = IndexLimits.MaxIndexableFileBytes;

    private readonly IIndexTarget _target;
    private readonly SqliteIndex _index;
    private readonly ILogger _logger;
    private readonly ExcludeFilter _excludeFilter;
    private readonly ExtractorRegistry _extractorRegistry;
    private readonly IExplicitBinaryPathProvider? _explicitBinaryPathProvider;
    private readonly IFileCountHintTarget? _fileCountHintTarget;
    private IReadOnlyDictionary<string, long>? _rootIds;

    /// <summary>
    /// Create an indexer for the given target → index pair.
    /// </summary>
    /// <param name="target">Target whose working tree will be scanned.</param>
    /// <param name="index">Target index for upserts.</param>
    /// <param name="excludeGlobs">
    /// Optional gitignore-style globs applied to logical paths. Files
    /// matching any glob are skipped before reading. Typical
    /// usage: <c>lib/**</c> to exclude vendored upstream sources.
    /// </param>
    /// <param name="logger">Optional structured logger.</param>
    public FullScanIndexer(
        IIndexTarget target,
        SqliteIndex index,
        IReadOnlyList<string>? excludeGlobs = null,
        ILogger? logger = null,
        ExtractorRegistry? extractorRegistry = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _logger = logger ?? NullLogger.Instance;
        _excludeFilter = new ExcludeFilter(excludeGlobs);
        _extractorRegistry = extractorRegistry ?? ExtractorRegistry.BuildDefault();
        _explicitBinaryPathProvider = target as IExplicitBinaryPathProvider;
        _fileCountHintTarget = target as IFileCountHintTarget;
    }

    /// <summary>
    /// Walk every tracked+untracked path, filter, SHA-compare, UPSERT.
    /// Returns once the final batch commits.
    /// </summary>
    public async Task<IndexStatistics> RunAsync(
        IProgress<IndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var stats = new IndexStatistics
        {
            Total = _fileCountHintTarget is null
                ? null
                : await _fileCountHintTarget.GetFileCountHintAsync(cancellationToken).ConfigureAwait(false),
        };
        var explicitBinaryLogicalPaths = await GetExplicitBinaryLogicalPathsAsync(cancellationToken).ConfigureAwait(false);

        var scope = await _index.BeginWriteAsync(cancellationToken).ConfigureAwait(false);
        var batchStart = DateTimeOffset.UtcNow;
        var filesInBatch = 0;

        try
        {
            _rootIds = SqliteIndex.UpsertRoots(scope, _target.Roots);

            await foreach (var file in _target.EnumerateFilesAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var logicalPath = file.LogicalPath.Value;

                if (_excludeFilter.IsExcluded(logicalPath)
                    || explicitBinaryLogicalPaths.Contains(logicalPath)
                    || BinaryFileClassifier.IsLikelyBinary(file.AbsolutePath))
                {
                    stats.Skipped++;
                    progress?.Report(new IndexProgress(logicalPath, stats.Indexed, stats.Skipped, stats.Total));
                    continue;
                }

                long mtimeUtc;
                byte[] sha;
                try
                {
                    var info = new FileInfo(file.AbsolutePath);
                    if (!info.Exists) { stats.Skipped++; continue; }
                    if (info.Length > MaxIndexableFileBytes) { stats.Skipped++; continue; }
                    mtimeUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeSeconds();

                    // Compute SHA from a stream — avoids allocating the full byte[]
                    // for unchanged files, which are the common case during re-scans.
                    using (var shaStream = new FileStream(file.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true))
                    {
                        sha = await SHA256.HashDataAsync(shaStream, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogDebug(ex, "skip {Path}: {Message}", logicalPath, ex.Message);
                    stats.Skipped++;
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogDebug(ex, "skip {Path}: {Message}", logicalPath, ex.Message);
                    stats.Skipped++;
                    continue;
                }

                var prior = _index.TryGetShaByLogicalPath(logicalPath);
                if (AreEqual(prior, sha))
                {
                    stats.Unchanged++;
                    progress?.Report(new IndexProgress(logicalPath, stats.Indexed, stats.Skipped, stats.Total));
                    continue;
                }

                // SHA differs — read the full file for content extraction.
                byte[] bytes;
                try
                {
                    bytes = await File.ReadAllBytesAsync(file.AbsolutePath, cancellationToken).ConfigureAwait(false);
                    // Re-check after read: the file may have grown between stat and read.
                    if (bytes.LongLength > MaxIndexableFileBytes) { stats.Skipped++; continue; }
                }
                catch (IOException ex)
                {
                    _logger.LogDebug(ex, "skip {Path}: {Message}", logicalPath, ex.Message);
                    stats.Skipped++;
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogDebug(ex, "skip {Path}: {Message}", logicalPath, ex.Message);
                    stats.Skipped++;
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

                stats.Indexed++;
                filesInBatch++;
                progress?.Report(new IndexProgress(logicalPath, stats.Indexed, stats.Skipped, stats.Total));

                var batchExpired = DateTimeOffset.UtcNow - batchStart >= BatchTimeBudget;
                if (filesInBatch >= BatchFileCount || batchExpired)
                {
                    await scope.DisposeAsync().ConfigureAwait(false);
                    scope = await _index.BeginWriteAsync(cancellationToken).ConfigureAwait(false);
                    batchStart = DateTimeOffset.UtcNow;
                    filesInBatch = 0;
                }
            }

        }
        finally
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }

        // Write meta outside the file-upsert batches but inside a dedicated
        // writer scope so the write is serialized properly.
        await using (var metaScope = await _index.BeginWriteAsync(cancellationToken).ConfigureAwait(false))
        {
            SqliteIndex.SetMeta(metaScope, SqliteSchema.MetaKey_IndexedHead, SafeCurrentRevisionToken(cancellationToken));
            SqliteIndex.SetMeta(
                metaScope,
                SqliteSchema.MetaKey_LastFullScanAt,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        stats.Elapsed = DateTimeOffset.UtcNow - started;
        return stats;
    }

    private async ValueTask<IReadOnlySet<string>> GetExplicitBinaryLogicalPathsAsync(CancellationToken cancellationToken)
    {
        if (_explicitBinaryPathProvider is null)
            return new HashSet<string>(StringComparer.Ordinal);

        try
        {
            return await _explicitBinaryPathProvider
                .GetExplicitBinaryLogicalPathsAsync(files: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "explicit binary-path enumeration failed; falling back to heuristic classification");
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private string? SafeCurrentRevisionToken(CancellationToken cancellationToken)
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

    private static bool AreEqual(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;
        return a.SequenceEqual(b);
    }

}

/// <summary>Aggregate counts returned by <see cref="FullScanIndexer.RunAsync"/>.</summary>
public sealed class IndexStatistics
{
    public int? Total { get; set; }
    public int Indexed { get; set; }
    public int Skipped { get; set; }
    public int Unchanged { get; set; }
    public TimeSpan Elapsed { get; set; }
}

/// <summary>Progress tick reported by the indexer after each file.</summary>
public readonly record struct IndexProgress(string Path, int Indexed, int Skipped, int? Total);
