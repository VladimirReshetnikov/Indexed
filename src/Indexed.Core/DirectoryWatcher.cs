using System;
using System.Collections.Generic;
using System.IO;
using Indexed.Targets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Indexed.Core;

/// <summary>
/// Shared multi-root <see cref="FileSystemWatcher"/> wrapper that converts
/// absolute filesystem notifications into logical-path index events.
/// </summary>
/// <remarks>
/// <para>
/// FSW remains a <strong>best-effort hint</strong>. It will miss events under
/// high churn, across network drives, and on some kernel configurations. The
/// <see cref="ReconciliationScheduler"/> is the correctness backstop.
/// </para>
/// <para>
/// One watcher instance is created per target root. Paths are normalized
/// through <see cref="IIndexTarget.TryMapAbsolutePath"/> so git, single-root
/// directory, and multi-root directory-set targets all enqueue the same
/// logical-path events.
/// </para>
/// </remarks>
public sealed class DirectoryWatcher : IDisposable
{
    private readonly IIndexTarget _target;
    private readonly DebouncingEventQueue _queue;
    private readonly ILogger _logger;
    private readonly IndexPathFilter _pathFilter;
    private readonly List<FileSystemWatcher> _watchers = new();

    public DirectoryWatcher(
        IIndexTarget target,
        DebouncingEventQueue queue,
        IReadOnlyList<string>? excludeGlobs = null,
        ILogger? logger = null)
        : this(target, queue, new IndexingOptions(excludeGlobs: excludeGlobs), logger)
    {
    }

    public DirectoryWatcher(
        IIndexTarget target,
        DebouncingEventQueue queue,
        IndexingOptions options,
        ILogger? logger = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _logger = logger ?? NullLogger.Instance;
        _pathFilter = new IndexPathFilter(options ?? throw new ArgumentNullException(nameof(options)));
    }

    /// <summary>Start watching all target roots. Idempotent.</summary>
    public void Start()
    {
        if (_watchers.Count > 0) return;

        foreach (var root in _target.Roots)
        {
            var watcher = new FileSystemWatcher(root.AbsolutePath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.DirectoryName,
                InternalBufferSize = 65536,
            };

            var state = new WatchRootState(root);
            watcher.Created += (_, e) => OnChanged(state, e.FullPath);
            watcher.Changed += (_, e) => OnChanged(state, e.FullPath);
            watcher.Deleted += (_, e) => OnDeleted(state, e.FullPath);
            watcher.Renamed += (_, e) => OnRenamed(state, e.OldFullPath, e.FullPath);
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }

        _logger.LogDebug("DirectoryWatcher started for {RootCount} roots", _target.Roots.Count);
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
    }

    internal string? Normalize(string fullPath)
        => TryNormalize(fullPath, out _, out var logicalPath) ? logicalPath : null;

    private void OnChanged(WatchRootState state, string fullPath)
    {
        if (!TryNormalize(state, fullPath, out var relativePath, out var logicalPath))
            return;
        if (ShouldSkip(relativePath, logicalPath))
            return;
        _queue.Enqueue(new FileChanged(logicalPath));
    }

    private void OnDeleted(WatchRootState state, string fullPath)
    {
        if (!TryNormalize(state, fullPath, out var relativePath, out var logicalPath))
            return;
        if (ShouldSkip(relativePath, logicalPath))
            return;
        _queue.Enqueue(new FileDeleted(logicalPath));
    }

    private void OnRenamed(WatchRootState state, string oldFullPath, string newFullPath)
    {
        if (TryNormalize(state, oldFullPath, out var oldRelativePath, out var oldLogicalPath)
            && !ShouldSkip(oldRelativePath, oldLogicalPath))
        {
            _queue.Enqueue(new FileDeleted(oldLogicalPath));
        }

        if (TryNormalize(state, newFullPath, out var newRelativePath, out var newLogicalPath)
            && !ShouldSkip(newRelativePath, newLogicalPath))
        {
            _queue.Enqueue(new FileChanged(newLogicalPath));
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(), "FileSystemWatcher error — events may have been dropped; requesting reconciliation");
        _queue.Enqueue(new ReconciliationRequested());
    }

    private bool TryNormalize(string fullPath, out string relativePath, out string logicalPath)
    {
        relativePath = string.Empty;
        logicalPath = string.Empty;
        foreach (var root in _target.Roots)
        {
            if (TryNormalize(new WatchRootState(root), fullPath, out relativePath, out logicalPath))
                return true;
        }

        return false;
    }

    private bool TryNormalize(WatchRootState state, string fullPath, out string relativePath, out string logicalPath)
    {
        relativePath = string.Empty;
        logicalPath = string.Empty;
        try
        {
            var canonical = Path.GetFullPath(fullPath);
            if (!canonical.StartsWith(state.RootWithSeparator, TargetPathUtilities.PathComparison)
                && !string.Equals(canonical, state.Root.AbsolutePath, TargetPathUtilities.PathComparison))
            {
                return false;
            }

            relativePath = Path.GetRelativePath(state.Root.AbsolutePath, canonical)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (relativePath == ".")
                return false;

            if (!_target.TryMapAbsolutePath(canonical, out var mappedLogicalPath))
                return false;

            logicalPath = mappedLogicalPath.Value;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private bool ShouldSkip(string relativePath, string logicalPath)
    {
        if (_target.Spec.Kind == TargetKind.GitRepository
            && (relativePath.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || relativePath.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return !_pathFilter.ShouldIndex(logicalPath);
    }

    private sealed record WatchRootState(TargetRoot Root)
    {
        public string RootWithSeparator { get; } = TargetPathUtilities.EnsureDirectorySeparatorSuffix(Root.AbsolutePath);
    }
}
