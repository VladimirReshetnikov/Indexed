using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Targets;

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
/// from the current target roots and scans it against the query pattern.
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
/// Security — path-traversal defense. Rows in <c>files.logical_path</c> are
/// expected to be valid target logical paths. <see cref="ReadAsync"/>
/// resolves them through <see cref="IIndexTarget"/> (or a single-root
/// compatibility resolver), and any escape outside the owning root yields
/// <see cref="FileReadStatus.OutOfRoot"/>.
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
    private readonly IIndexTarget? _target;
    private readonly string _primaryRootPath;
    private readonly string? _singleRootWithSep;

    /// <summary>
    /// Create a provider backed by an index target. Logical paths are resolved
    /// through the target's root mapping.
    /// </summary>
    public FileContentProvider(IIndexTarget target)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        if (target.Roots.Count == 0)
            throw new ArgumentException("target must expose at least one root", nameof(target));

        _primaryRootPath = target.Roots[0].AbsolutePath;
    }

    /// <summary>
    /// Compatibility constructor for tests and single-root callers. Phase 2
    /// production code should prefer the <see cref="IIndexTarget"/> overload.
    /// </summary>
    public FileContentProvider(string rootPath)
    {
        if (string.IsNullOrEmpty(rootPath))
            throw new ArgumentException("rootPath is required", nameof(rootPath));

        _primaryRootPath = Path.GetFullPath(rootPath);
        _singleRootWithSep = _primaryRootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _primaryRootPath
            : _primaryRootPath + Path.DirectorySeparatorChar;
    }

    /// <summary>Primary root used for display and single-root compatibility tests.</summary>
    public string PrimaryRootPath => _primaryRootPath;

    /// <summary>
    /// Read the file at <paramref name="logicalPath"/>. Returns a
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
    public ValueTask<FileReadOutcome> ReadAsync(string logicalPath, CancellationToken cancellationToken)
        => ReadAsync(new LogicalPath(logicalPath), cancellationToken);

    public async ValueTask<FileReadOutcome> ReadAsync(LogicalPath logicalPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(logicalPath.Value)) return FileReadOutcome.Unreadable;

        string full;
        try
        {
            full = _target is not null
                ? _target.ResolveAbsolutePath(logicalPath)
                : ResolveSingleRootPath(logicalPath.Value);
        }
        catch (InvalidOperationException) { return FileReadOutcome.OutOfRoot; }
        catch (ArgumentException) { return FileReadOutcome.Unreadable; }
        catch (PathTooLongException) { return FileReadOutcome.Unreadable; }
        catch (NotSupportedException) { return FileReadOutcome.Unreadable; }
        catch (System.Security.SecurityException) { return FileReadOutcome.Unreadable; }

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

    private string ResolveSingleRootPath(string logicalPath)
    {
        if (_singleRootWithSep is null)
            throw new InvalidOperationException("single-root resolver is not available");

        var combined = Path.Combine(_primaryRootPath, logicalPath.Replace('/', Path.DirectorySeparatorChar));
        var fullPath = Path.GetFullPath(combined);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(_singleRootWithSep, comparison))
            throw new InvalidOperationException("logical path resolves outside the configured root");

        return fullPath;
    }
}
