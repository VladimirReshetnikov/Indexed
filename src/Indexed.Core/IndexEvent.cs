namespace Indexed.Core;

/// <summary>
/// Base type for events that flow through the <see cref="DebouncingEventQueue"/>
/// into the <see cref="IncrementalIndexer"/> worker loop.
/// </summary>
/// <remarks>
/// Events are produced by three sources:
/// <list type="bullet">
///   <item><description><see cref="RepoWatcher"/> — FSW-driven <see cref="FileChanged"/>/<see cref="FileDeleted"/>.</description></item>
///   <item><description><see cref="HeadPoller"/> — <see cref="HeadMoved"/> when HEAD diverges from <c>indexed_head</c>.</description></item>
///   <item><description><see cref="ReconciliationScheduler"/> — periodic <see cref="ReconciliationRequested"/>.</description></item>
/// </list>
/// </remarks>
public abstract record IndexEvent;

/// <summary>
/// A file was created or modified in the working tree. The incremental
/// indexer will read, SHA, and upsert if the content has actually changed.
/// </summary>
/// <param name="RelativePath">Repository-relative POSIX path (forward-slash separators).</param>
public sealed record FileChanged(string RelativePath) : IndexEvent;

/// <summary>
/// A file was deleted from the working tree. The incremental indexer will
/// remove the corresponding index entry if one exists.
/// </summary>
/// <param name="RelativePath">Repository-relative POSIX path.</param>
public sealed record FileDeleted(string RelativePath) : IndexEvent;

/// <summary>
/// HEAD has moved to a different commit. The incremental indexer will run
/// <c>git diff-tree</c> between the old and new HEAD and apply the diff.
/// </summary>
/// <param name="OldHead">The previously indexed HEAD SHA (40-hex).</param>
/// <param name="NewHead">The current HEAD SHA (40-hex).</param>
public sealed record HeadMoved(string OldHead, string NewHead) : IndexEvent;

/// <summary>
/// Periodic reconciliation requested. The incremental indexer will diff the
/// git file set against the index contents to catch missed FSW events,
/// external git operations, or files modified while the daemon was stopped.
/// </summary>
public sealed record ReconciliationRequested : IndexEvent;
