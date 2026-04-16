namespace Indexed.Core;

/// <summary>
/// Centralized numeric limits enforced across the indexing pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Historically these constants were duplicated across
/// <see cref="FullScanIndexer"/>, <see cref="IncrementalIndexer"/>, and
/// <see cref="FileContentProvider"/>. Consolidating here ensures a single
/// source of truth so that a change to the size cap (or any future limit)
/// is picked up uniformly by write-time scan, write-time upsert, and
/// read-time snippet rehydration.
/// </para>
/// <para>
/// The <see cref="Indexed.Git.GitRepository"/> binary-detection path has its
/// own private copy of the cap because <c>Indexed.Git</c> is a lower-layer
/// assembly that cannot reference <c>Indexed.Core</c>. Values are intentionally
/// kept in sync by convention.
/// </para>
/// </remarks>
public static class IndexLimits
{
    /// <summary>
    /// Upper bound on file size eligible for indexing and snippet rehydration.
    /// Files larger than this are skipped at write time by both the full and
    /// incremental indexers, and return <c>null</c> / <c>Oversize</c> from
    /// <see cref="FileContentProvider"/> at query time so the on-disk and
    /// in-index behaviors stay in agreement.
    /// </summary>
    /// <remarks>
    /// 50 MiB. Raising this risks OOM on <c>File.ReadAllBytesAsync</c>
    /// (single allocation) and slows FTS5 tokenization for minimal search
    /// benefit on such files.
    /// </remarks>
    public const long MaxIndexableFileBytes = 50L * 1024 * 1024;
}
