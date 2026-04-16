using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Indexed.Core;

/// <summary>
/// Reads file content from the working tree at query time. Paired with the
/// contentless FTS5 index (schema version 2+): the index stores posting
/// lists but no content, so snippet rendering pulls the live on-disk text
/// from here and scans it against the query pattern.
/// </summary>
/// <remarks>
/// <para>
/// Every <see cref="ReadAsync"/> result is a fresh view of the file —
/// there is no in-memory content cache. Adding one is an explicit later
/// step held in reserve if disk-read overhead pushes <c>/search</c>
/// latency past the regression budget (see
/// <c>Indexed-Size-Reduction-SafeNearTerm-Plan.md §Risk register</c>).
/// </para>
/// <para>
/// Failure policy. Any I/O issue (missing file, permission denied,
/// hostile path) yields <c>null</c>. Oversize files (&gt;
/// <see cref="FullScanIndexer.MaxIndexableFileBytes"/>) also yield
/// <c>null</c> — the indexer refused to scan them at write time, so
/// returning them here would introduce a behavior difference between
/// at-index-time and at-query-time scanning. Callers
/// (<see cref="CodeQueryExecutor"/>) treat <c>null</c> as
/// "no match for this file" and optionally enqueue a repair event.
/// </para>
/// </remarks>
public sealed class FileContentProvider
{
    private readonly string _repoRoot;

    /// <summary>
    /// Create a provider rooted at <paramref name="repoRoot"/>. All relative
    /// paths passed to <see cref="ReadAsync"/> are resolved against this root.
    /// </summary>
    public FileContentProvider(string repoRoot)
    {
        if (string.IsNullOrEmpty(repoRoot))
            throw new ArgumentException("repoRoot is required", nameof(repoRoot));
        _repoRoot = repoRoot;
    }

    /// <summary>Repository root this provider resolves relative paths against.</summary>
    public string RepoRoot => _repoRoot;

    /// <summary>
    /// Read the file at <paramref name="relPath"/> (repo-relative,
    /// forward-slash separators accepted). Returns <c>null</c> when the
    /// file is missing, unreadable, or exceeds
    /// <see cref="FullScanIndexer.MaxIndexableFileBytes"/>; otherwise
    /// returns the decoded text using <see cref="TextDecoder"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// var text = await provider.ReadAsync("src/foo.cs", ct);
    /// if (text is null)
    /// {
    ///     // file was deleted, oversized, or unreadable — skip and maybe repair
    /// }
    /// </code>
    /// </example>
    public async ValueTask<string?> ReadAsync(string relPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(relPath)) return null;

        var full = Path.Combine(_repoRoot, relPath.Replace('/', Path.DirectorySeparatorChar));

        try
        {
            var info = new FileInfo(full);
            if (!info.Exists) return null;
            if (info.Length > FullScanIndexer.MaxIndexableFileBytes) return null;

            var bytes = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
            // Re-check length post-read in case the file grew between stat and read.
            if (bytes.LongLength > FullScanIndexer.MaxIndexableFileBytes) return null;

            return TextDecoder.Decode(bytes);
        }
        // PathTooLongException derives from IOException so it is implicitly
        // covered. Catch order below matches exception hierarchy (specific
        // first where hierarchy allows).
        catch (UnauthorizedAccessException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (IOException) { return null; }
    }
}
