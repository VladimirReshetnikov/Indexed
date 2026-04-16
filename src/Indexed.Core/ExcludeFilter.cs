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
/// <remarks>
/// <para>
/// The static <see cref="DefaultBinaryAdjacentGlobs"/> list provides a curated
/// set of patterns for files that inflate the FTS5 trigram index without
/// providing search value (lockfiles, minified bundles, source maps, generated
/// code). It is applied by default via <see cref="Combine"/>; callers can opt
/// out by passing <c>useDefaults: false</c>.
/// </para>
/// </remarks>
public sealed class ExcludeFilter
{
    private readonly IReadOnlyList<Regex> _regexes;

    /// <summary>An empty filter that matches nothing.</summary>
    public static ExcludeFilter None { get; } = new(Array.Empty<Regex>());

    /// <summary>
    /// Curated default globs for files that inflate the trigram index without
    /// providing useful search value. Includes JS/TS and ecosystem lockfiles,
    /// minified bundles, source maps, and generated C# files.
    /// </summary>
    /// <remarks>
    /// <para>Applied by default in <see cref="DaemonOptions.UseDefaultIndexExcludes"/>.</para>
    /// <list type="bullet">
    ///   <item><description><c>**/package-lock.json</c>, <c>**/yarn.lock</c>, <c>**/pnpm-lock.yaml</c>, <c>**/npm-shrinkwrap.json</c> — JS/TS lockfiles.</description></item>
    ///   <item><description><c>**/*.min.js</c>, <c>**/*.min.css</c> — minified bundles.</description></item>
    ///   <item><description><c>**/*.map</c> — source maps (often larger than the source they describe).</description></item>
    ///   <item><description><c>**/Cargo.lock</c>, <c>**/composer.lock</c>, <c>**/Gemfile.lock</c>, <c>**/Pipfile.lock</c>, <c>**/poetry.lock</c>, <c>**/go.sum</c>, <c>**/packages.lock.json</c> — other ecosystem lockfiles.</description></item>
    ///   <item><description><c>**/*.generated.cs</c>, <c>**/*.g.cs</c>, <c>**/*.g.i.cs</c>, <c>**/*.Designer.cs</c> — generated C# files with high token density and low search value.</description></item>
    /// </list>
    /// </remarks>
    public static IReadOnlyList<string> DefaultBinaryAdjacentGlobs { get; } = new string[]
    {
        // JS/TS lockfiles
        "**/package-lock.json",
        "**/yarn.lock",
        "**/pnpm-lock.yaml",
        "**/npm-shrinkwrap.json",

        // Minified bundles and source maps
        "**/*.min.js",
        "**/*.min.css",
        "**/*.map",

        // Ecosystem lockfiles
        "**/Cargo.lock",
        "**/composer.lock",
        "**/Gemfile.lock",
        "**/Pipfile.lock",
        "**/poetry.lock",
        "**/go.sum",
        "**/packages.lock.json",

        // Generated C# files (protobuf, T4, WinForms designer)
        "**/*.generated.cs",
        "**/*.g.cs",
        "**/*.g.i.cs",
        "**/*.Designer.cs",
    };

    /// <summary>
    /// Concatenate two glob lists into a single <see cref="IReadOnlyList{T}"/>.
    /// Returns <c>null</c> when both inputs are null or empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Always returns a freshly-allocated array — the caller may freely
    /// mutate <paramref name="a"/> or <paramref name="b"/> after the call
    /// without disturbing the returned view. The input lists themselves
    /// are not modified. Cheap to call: typical default-globs lists are
    /// on the order of 20 strings.
    /// </para>
    /// </remarks>
    /// <param name="a">User-supplied globs (may be <c>null</c>).</param>
    /// <param name="b">Additional globs to append (may be <c>null</c>).</param>
    public static IReadOnlyList<string>? Combine(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        var aCount = a?.Count ?? 0;
        var bCount = b?.Count ?? 0;
        if (aCount == 0 && bCount == 0) return null;

        // Always copy: prevents caller mutations from silently changing the
        // returned view, and keeps the result type predictable (always a
        // fresh string[]) regardless of input-list implementation.
        var merged = new string[aCount + bCount];
        if (aCount > 0)
            for (var i = 0; i < aCount; i++) merged[i] = a![i];
        if (bCount > 0)
            for (var i = 0; i < bCount; i++) merged[aCount + i] = b![i];
        return merged;
    }

    /// <summary>
    /// Create a filter from the given glob list. Pass <c>null</c> for an
    /// empty (pass-through) filter.
    /// </summary>
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
