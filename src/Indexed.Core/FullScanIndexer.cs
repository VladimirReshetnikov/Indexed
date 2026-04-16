using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Indexed.Core;

/// <summary>
/// Single-threaded indexer that walks every file in a <see cref="GitRepository"/>,
/// filters out binaries and oversize blobs, computes SHA-256, and UPSERTs a
/// <c>code_fts</c> row per file using the writer connection on
/// <see cref="SqliteIndex"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 2 has no watcher, so this class is also the daemon's "startup
/// warm-up" path: when <see cref="DaemonHost"/> detects an empty or
/// schema-mismatched DB it runs a full scan before serving queries (proposal
/// §9.1 and plan task 2.7). Stage 4 keeps this class for cold rebuilds but
/// routes ongoing changes through a real event loop.
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
/// the caller-supplied <see cref="IProgress{T}"/> (optional). The total count
/// is known up-front from <see cref="GitRepository.EnumerateFiles"/>; the
/// "done" count increments after each committed file.
/// </para>
/// </remarks>
public sealed class FullScanIndexer
{
    /// <summary>Max files per transaction.</summary>
    public const int BatchFileCount = 200;

    /// <summary>Max wall-clock time per transaction.</summary>
    public static readonly TimeSpan BatchTimeBudget = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Files larger than this bypass the indexer. Mirrors the cap baked into
    /// <see cref="GitRepository.IsLikelyBinary"/> — duplicated here because the
    /// indexer re-reads file bytes and benefits from the short-circuit before
    /// allocation.
    /// </summary>
    public const long MaxIndexableFileBytes = 50L * 1024 * 1024;

    private readonly GitRepository _repo;
    private readonly SqliteIndex _index;
    private readonly ILogger _logger;
    private readonly ExcludeFilter _excludeFilter;

    /// <summary>
    /// Create an indexer for the given repo → index pair.
    /// </summary>
    /// <param name="repo">Repository whose working tree will be scanned.</param>
    /// <param name="index">Target index for upserts.</param>
    /// <param name="excludeGlobs">
    /// Optional gitignore-style globs applied to repository-relative POSIX
    /// paths. Files matching any glob are skipped before reading. Typical
    /// usage: <c>lib/**</c> to exclude vendored upstream sources.
    /// </param>
    /// <param name="logger">Optional structured logger.</param>
    public FullScanIndexer(
        GitRepository repo,
        SqliteIndex index,
        IReadOnlyList<string>? excludeGlobs = null,
        ILogger? logger = null)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _logger = logger ?? NullLogger.Instance;
        _excludeFilter = new ExcludeFilter(excludeGlobs);
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
        var files = _repo.EnumerateFiles();
        var binaryAttrs = _repo.GetBinaryAttrPaths();

        var stats = new IndexStatistics { Total = files.Count };

        var scope = await _index.BeginWriteAsync(cancellationToken).ConfigureAwait(false);
        var batchStart = DateTimeOffset.UtcNow;
        var filesInBatch = 0;

        try
        {
            foreach (var relPath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_excludeFilter.IsExcluded(relPath) || binaryAttrs.Contains(relPath) || _repo.IsLikelyBinary(relPath))
                {
                    stats.Skipped++;
                    progress?.Report(new IndexProgress(relPath, stats.Indexed, stats.Skipped, stats.Total));
                    continue;
                }

                var full = Path.Combine(_repo.RepoRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
                long mtimeUtc;
                byte[] sha;
                try
                {
                    var info = new FileInfo(full);
                    if (!info.Exists) { stats.Skipped++; continue; }
                    if (info.Length > MaxIndexableFileBytes) { stats.Skipped++; continue; }
                    mtimeUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeSeconds();

                    // Compute SHA from a stream — avoids allocating the full byte[]
                    // for unchanged files, which are the common case during re-scans.
                    using (var shaStream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true))
                    {
                        sha = await SHA256.HashDataAsync(shaStream, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogDebug(ex, "skip {Path}: {Message}", relPath, ex.Message);
                    stats.Skipped++;
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogDebug(ex, "skip {Path}: {Message}", relPath, ex.Message);
                    stats.Skipped++;
                    continue;
                }

                var prior = _index.TryGetShaByPath(relPath);
                if (AreEqual(prior, sha))
                {
                    stats.Unchanged++;
                    progress?.Report(new IndexProgress(relPath, stats.Indexed, stats.Skipped, stats.Total));
                    continue;
                }

                // SHA differs — read the full file for content extraction.
                byte[] bytes;
                try
                {
                    bytes = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
                    // Re-check after read: the file may have grown between stat and read.
                    if (bytes.LongLength > MaxIndexableFileBytes) { stats.Skipped++; continue; }
                }
                catch (IOException ex)
                {
                    _logger.LogDebug(ex, "skip {Path}: {Message}", relPath, ex.Message);
                    stats.Skipped++;
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogDebug(ex, "skip {Path}: {Message}", relPath, ex.Message);
                    stats.Skipped++;
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

                stats.Indexed++;
                filesInBatch++;
                progress?.Report(new IndexProgress(relPath, stats.Indexed, stats.Skipped, stats.Total));

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
            _index.SetMeta(SqliteSchema.MetaKey_IndexedHead, SafeHead());
            _index.SetMeta(
                SqliteSchema.MetaKey_LastFullScanAt,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        stats.Elapsed = DateTimeOffset.UtcNow - started;
        return stats;
    }

    private string SafeHead()
    {
        try { return _repo.GetHeadSha(); }
        catch { return string.Empty; }
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
    public int Total { get; set; }
    public int Indexed { get; set; }
    public int Skipped { get; set; }
    public int Unchanged { get; set; }
    public TimeSpan Elapsed { get; set; }
}

/// <summary>Progress tick reported by the indexer after each file.</summary>
public readonly record struct IndexProgress(string Path, int Indexed, int Skipped, int Total);
