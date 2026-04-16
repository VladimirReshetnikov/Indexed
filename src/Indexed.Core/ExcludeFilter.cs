using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Indexed.Core;

/// <summary>
/// Shared helper that compiles gitignore-style glob patterns into compiled
/// <see cref="Regex"/> instances and tests repository-relative paths against
/// the set. Eliminates duplication across <see cref="FullScanIndexer"/>,
/// <see cref="IncrementalIndexer"/>, <see cref="RepoWatcher"/>, and
/// <see cref="CodeQueryExecutor"/>.
/// </summary>
internal sealed class ExcludeFilter
{
    private readonly IReadOnlyList<Regex> _regexes;

    /// <summary>An empty filter that matches nothing.</summary>
    public static ExcludeFilter None { get; } = new(Array.Empty<Regex>());

    public ExcludeFilter(IReadOnlyList<string>? globs)
    {
        _regexes = Compile(globs);
    }

    private ExcludeFilter(IReadOnlyList<Regex> regexes)
    {
        _regexes = regexes;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="relPath"/> matches any of the
    /// configured exclude globs. Path is normalized to forward slashes before
    /// matching.
    /// </summary>
    public bool IsExcluded(string relPath)
    {
        if (_regexes.Count == 0) return false;
        var norm = relPath.Replace('\\', '/');
        foreach (var rx in _regexes)
        {
            if (rx.IsMatch(norm)) return true;
        }
        return false;
    }

    private static IReadOnlyList<Regex> Compile(IReadOnlyList<string>? globs)
    {
        if (globs is null || globs.Count == 0) return Array.Empty<Regex>();
        var list = new Regex[globs.Count];
        for (var i = 0; i < globs.Count; i++) list[i] = PathGlob.Compile(globs[i]);
        return list;
    }
}
