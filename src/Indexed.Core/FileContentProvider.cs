using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Indexed.Core;

/// <summary>
/// Classification returned by <see cref="FileContentProvider.ReadAsync"/>
/// so callers can distinguish a genuine file deletion from a transient
/// unreadable state or an oversize file.
/// </summary>
/// <remarks>
/// <para>
/// The original PR-3 API returned a nullable <c>string</c> collapsing all
/// failure modes into <c>null</c>. <see cref="Indexed.Core.CodeQueryExecutor"/>
/// then enqueued a single <see cref="FileChanged"/> repair event for every
/// such outcome, which leaked stale rows into the index: a row for a
/// confirmed-deleted file would live until the next
/// <see cref="ReconciliationScheduler"/> tick because the incremental
/// indexer, on seeing <c>FileChanged</c>, stats the file, finds it gone,
/// and skips without removing the row.
/// </para>
/// <para>
/// By distinguishing <see cref="FileReadStatus.Missing"/> from
/// <see cref="FileReadStatus.Unreadable"/> and <see cref="FileReadStatus.Oversize"/>,
/// callers can enqueue the correct convergence event
/// (<see cref="FileDeleted"/> for missing, <see cref="FileChanged"/> for
/// transient failures).
/// </para>
/// </remarks>
public enum FileReadStatus
{
    /// <summary>File read succeeded; <see cref="FileReadOutcome.Content"/> is non-null.</summary>
    Ok,

    /// <summary>File was absent on disk. Caller should enqueue <see cref="FileDeleted"/>.</summary>
    Missing,

    /// <summary>
    /// File exceeded <see cref="IndexLimits.MaxIndexableFileBytes"/> either at stat
    /// time or after a race grew it post-stat. Caller should treat this like
    /// <see cref="Unreadable"/> — the incremental indexer will re-observe the
    /// size cap and refuse to upsert.
    /// </summary>
    Oversize,

    /// <summary>
    /// An I/O, permissions, or path-validation error occurred. Transient
    /// from the caller's perspective; a <see cref="FileChanged"/> repair
    /// prompts the indexer to retry.
    /// </summary>
    Unreadable,

    /// <summary>
    /// The requested path resolved outside the repository root — defense
    /// against a malformed <c>files.path</c> row containing <c>..</c>
    /// segments or an absolute path. Treated as <see cref="Unreadable"/>
    /// by most callers; enqueuing a repair is appropriate.
    /// </summary>
    OutOfRoot,
}

/// <summary>
/// Result of a <see cref="FileContentProvider.ReadAsync"/> call.
/// </summary>
/// <param name="Status">Classification of the read attempt.</param>
/// <param name="Content">
/// Decoded file text when <paramref name="Status"/> is
/// <see cref="FileReadStatus.Ok"/>; <c>null</c> otherwise.
/// </param>
public readonly record struct FileReadOutcome(FileReadStatus Status, string? Content)
{
    /// <summary>Shorthand for a successful outcome.</summary>
    public static FileReadOutcome Ok(string content) => new(FileReadStatus.Ok, content);

    /// <summary>Shared instance for the missing-file outcome.</summary>
    public static FileReadOutcome Missing { get; } = new(FileReadStatus.Missing, null);

    /// <summary>Shared instance for the oversize outcome.</summary>
    public static FileReadOutcome Oversize { get; } = new(FileReadStatus.Oversize, null);

    /// <summary>Shared instance for the unreadable outcome.</summary>
    public static FileReadOutcome Unreadable { get; } = new(FileReadStatus.Unreadable, null);

    /// <summary>Shared instance for the out-of-root outcome.</summary>
    public static FileReadOutcome OutOfRoot { get; } = new(FileReadStatus.OutOfRoot, null);

    /// <summary><c>true</c> when <see cref="Status"/> is <see cref="FileReadStatus.Ok"/>.</summary>
    public bool IsOk => Status == FileReadStatus.Ok;
}

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
/// Security — path-traversal defense. Rows in <c>files.path</c> are populated
/// by <see cref="Indexed.Git.GitRepository.EnumerateFiles"/> and are
/// expected to be repo-relative POSIX paths without <c>..</c> segments.
/// As defense in depth, <see cref="ReadAsync"/> canonicalizes the resolved
/// full path and confirms it is still rooted under the repo before
/// touching the filesystem. A pathological row (absolute path, <c>..</c>
/// escape, etc.) yields <see cref="FileReadStatus.OutOfRoot"/> and the
/// caller treats it as if the file is unreadable.
/// </para>
/// <para>
/// Failure policy. Missing files yield <see cref="FileReadStatus.Missing"/>;
/// oversize files yield <see cref="FileReadStatus.Oversize"/>; any other
/// I/O or path-validation failure yields <see cref="FileReadStatus.Unreadable"/>.
/// Callers (<see cref="CodeQueryExecutor"/>) use the status to enqueue the
/// correct repair event — <see cref="FileDeleted"/> for missing,
/// <see cref="FileChanged"/> for transient failures — so the incremental
/// indexer converges promptly.
/// </para>
/// </remarks>
public sealed class FileContentProvider
{
    private readonly string _repoRoot;
    private readonly string _repoRootWithSep;

    /// <summary>
    /// Create a provider rooted at <paramref name="repoRoot"/>. All relative
    /// paths passed to <see cref="ReadAsync"/> are resolved against this root.
    /// </summary>
    public FileContentProvider(string repoRoot)
    {
        if (string.IsNullOrEmpty(repoRoot))
            throw new ArgumentException("repoRoot is required", nameof(repoRoot));

        // Canonicalize the root once so the prefix check in ReadAsync is a
        // pure string compare against a stable reference. GetFullPath also
        // collapses any trailing separators, which makes the
        // trailing-separator append below deterministic.
        _repoRoot = Path.GetFullPath(repoRoot);
        _repoRootWithSep = _repoRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _repoRoot
            : _repoRoot + Path.DirectorySeparatorChar;
    }

    /// <summary>Repository root this provider resolves relative paths against.</summary>
    public string RepoRoot => _repoRoot;

    /// <summary>
    /// Read the file at <paramref name="relPath"/> (repo-relative,
    /// forward-slash separators accepted). Returns a
    /// <see cref="FileReadOutcome"/> whose <see cref="FileReadOutcome.Status"/>
    /// distinguishes success from the several failure classes. Never throws
    /// for path or I/O reasons — the outcome carries the classification.
    /// </summary>
    /// <example>
    /// <code>
    /// var outcome = await provider.ReadAsync("src/foo.cs", ct);
    /// switch (outcome.Status)
    /// {
    ///     case FileReadStatus.Ok:
    ///         Use(outcome.Content!);
    ///         break;
    ///     case FileReadStatus.Missing:
    ///         repairQueue.Enqueue(new FileDeleted("src/foo.cs"));
    ///         break;
    ///     default:
    ///         repairQueue.Enqueue(new FileChanged("src/foo.cs"));
    ///         break;
    /// }
    /// </code>
    /// </example>
    public async ValueTask<FileReadOutcome> ReadAsync(string relPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(relPath)) return FileReadOutcome.Unreadable;

        string full;
        try
        {
            var combined = Path.Combine(_repoRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
            full = Path.GetFullPath(combined);
        }
        catch (ArgumentException) { return FileReadOutcome.Unreadable; }
        catch (PathTooLongException) { return FileReadOutcome.Unreadable; }
        catch (NotSupportedException) { return FileReadOutcome.Unreadable; }
        catch (System.Security.SecurityException) { return FileReadOutcome.Unreadable; }

        // Root-escape check: the canonicalized full path must live under the
        // repo root. On Windows, comparison is case-insensitive to match NTFS.
        var rootComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!full.StartsWith(_repoRootWithSep, rootComparison))
            return FileReadOutcome.OutOfRoot;

        try
        {
            var info = new FileInfo(full);
            if (!info.Exists) return FileReadOutcome.Missing;
            if (info.Length > IndexLimits.MaxIndexableFileBytes) return FileReadOutcome.Oversize;

            var bytes = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
            // Re-check length post-read in case the file grew between stat and read.
            if (bytes.LongLength > IndexLimits.MaxIndexableFileBytes)
                return FileReadOutcome.Oversize;

            return FileReadOutcome.Ok(TextDecoder.Decode(bytes));
        }
        catch (FileNotFoundException) { return FileReadOutcome.Missing; }
        catch (DirectoryNotFoundException) { return FileReadOutcome.Missing; }
        // PathTooLongException derives from IOException so it is implicitly
        // covered. Catch order below matches exception hierarchy (specific
        // first where hierarchy allows).
        catch (UnauthorizedAccessException) { return FileReadOutcome.Unreadable; }
        catch (NotSupportedException) { return FileReadOutcome.Unreadable; }
        catch (ArgumentException) { return FileReadOutcome.Unreadable; }
        catch (IOException) { return FileReadOutcome.Unreadable; }
    }
}
