using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Indexed.Core;

/// <summary>
/// Wraps <see cref="FileSystemWatcher"/> with exclude-glob filtering and
/// normalization, pushing <see cref="FileChanged"/>/<see cref="FileDeleted"/>
/// events into a <see cref="DebouncingEventQueue"/>.
/// </summary>
/// <remarks>
/// <para>
/// FSW is a <strong>best-effort hint</strong>. It will miss events under
/// high churn, across network drives, and on some kernel configurations.
/// The <see cref="ReconciliationScheduler"/> is the correctness backstop.
/// </para>
/// <para>
/// Excluded paths: the <c>.git/</c> directory, and any paths matching the
/// configured <c>--exclude-index</c> globs, are silently dropped. This
/// avoids overwhelming the indexer with internal git bookkeeping writes.
/// </para>
/// <para>
/// Normalization: backslash → forward-slash, absolute → repo-relative.
/// All paths pushed into the queue match <see cref="Indexed.Git.GitRepository"/>
/// conventions.
/// </para>
/// </remarks>
public sealed class RepoWatcher : IDisposable
{
    private readonly string _repoRoot;
    private readonly DebouncingEventQueue _queue;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<Regex> _excludeRegexes;
    private FileSystemWatcher? _watcher;

    /// <summary>
    /// Create a watcher for the given repo root.
    /// </summary>
    /// <param name="repoRoot">Absolute path to the repository working tree.</param>
    /// <param name="queue">Target queue for debounced events.</param>
    /// <param name="excludeGlobs">Optional gitignore-style globs to skip.</param>
    /// <param name="logger">Optional logger.</param>
    public RepoWatcher(
        string repoRoot,
        DebouncingEventQueue queue,
        IReadOnlyList<string>? excludeGlobs = null,
        ILogger? logger = null)
    {
        _repoRoot = repoRoot ?? throw new ArgumentNullException(nameof(repoRoot));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _logger = logger ?? NullLogger.Instance;
        _excludeRegexes = CompileExcludes(excludeGlobs);
    }

    /// <summary>
    /// Start watching. Idempotent — calling twice is safe.
    /// </summary>
    public void Start()
    {
        if (_watcher is not null) return;

        _watcher = new FileSystemWatcher(_repoRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size
                | NotifyFilters.DirectoryName,
            // 64 KB buffer — larger than the 8 KB default to reduce dropped events.
            InternalBufferSize = 65536,
        };

        _watcher.Created += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Deleted += OnDeleted;
        _watcher.Error += OnError;

        _watcher.EnableRaisingEvents = true;
        _logger.LogDebug("RepoWatcher started for {RepoRoot}", _repoRoot);
    }

    /// <summary>Stop watching and release resources.</summary>
    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        var rel = Normalize(e.FullPath);
        if (rel is null || ShouldSkip(rel)) return;
        _queue.Enqueue(new FileChanged(rel));
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Emit delete for old path and changed for new path.
        var oldRel = Normalize(e.OldFullPath);
        if (oldRel is not null && !ShouldSkip(oldRel))
            _queue.Enqueue(new FileDeleted(oldRel));

        var newRel = Normalize(e.FullPath);
        if (newRel is not null && !ShouldSkip(newRel))
            _queue.Enqueue(new FileChanged(newRel));
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        var rel = Normalize(e.FullPath);
        if (rel is null || ShouldSkip(rel)) return;
        _queue.Enqueue(new FileDeleted(rel));
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(), "FileSystemWatcher error — events may have been dropped");
    }

    /// <summary>
    /// Convert an absolute path to a repo-relative POSIX path, or return
    /// <c>null</c> if the path is not inside the repo root.
    /// </summary>
    internal string? Normalize(string fullPath)
    {
        // Use GetFullPath to canonicalize any \.\ or \..\ segments.
        var canonical = Path.GetFullPath(fullPath);
        var root = _repoRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _repoRoot
            : _repoRoot + Path.DirectorySeparatorChar;

        if (!canonical.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return null;

        var rel = canonical.Substring(root.Length);
        return rel.Replace('\\', '/');
    }

    private bool ShouldSkip(string relPath)
    {
        // Always skip .git internals.
        if (relPath.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || relPath.StartsWith(".git/", StringComparison.OrdinalIgnoreCase))
            return true;

        // Skip paths matching exclude globs.
        foreach (var rx in _excludeRegexes)
        {
            if (rx.IsMatch(relPath)) return true;
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
