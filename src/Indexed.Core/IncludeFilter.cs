using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Indexed.Core;

/// <summary>
/// Compiled include-glob set. An empty include list means "include all".
/// </summary>
public sealed class IncludeFilter
{
    private readonly IReadOnlyList<Regex> _regexes;

    public IncludeFilter(IReadOnlyList<string>? globs)
    {
        _regexes = Compile(globs);
    }

    public bool IsIncluded(string relPath)
    {
        if (_regexes.Count == 0) return true;

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
