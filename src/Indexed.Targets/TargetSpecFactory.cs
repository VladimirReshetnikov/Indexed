using System;
using System.Collections.Generic;
using System.Linq;

namespace Indexed.Targets;

public static class TargetSpecFactory
{
    public static TargetSpec CreateGitRepository(
        string repoRoot,
        IReadOnlyList<string>? indexExcludeGlobs = null,
        bool useDefaultIndexExcludes = true,
        bool useDefaultDirectoryExcludes = false)
        => CreateCore(
            TargetKind.GitRepository,
            new[] { new TargetRootSpec(Name: null, Path: repoRoot) },
            indexExcludeGlobs,
            useDefaultIndexExcludes,
            useDefaultDirectoryExcludes);

    public static TargetSpec CreateDirectoryTree(
        string root,
        IReadOnlyList<string>? indexExcludeGlobs = null,
        bool useDefaultIndexExcludes = true,
        bool useDefaultDirectoryExcludes = true)
        => CreateCore(
            TargetKind.DirectoryTree,
            new[] { new TargetRootSpec(Name: null, Path: root) },
            indexExcludeGlobs,
            useDefaultIndexExcludes,
            useDefaultDirectoryExcludes);

    public static TargetSpec CreateDirectorySet(
        IReadOnlyList<TargetRootSpec> roots,
        IReadOnlyList<string>? indexExcludeGlobs = null,
        bool useDefaultIndexExcludes = true,
        bool useDefaultDirectoryExcludes = true)
        => CreateCore(
            TargetKind.DirectorySet,
            roots,
            indexExcludeGlobs,
            useDefaultIndexExcludes,
            useDefaultDirectoryExcludes);

    public static IReadOnlyList<TargetRoot> MaterializeRoots(TargetSpec spec)
    {
        if (spec is null) throw new ArgumentNullException(nameof(spec));

        return spec.Roots
            .Select((root, index) => new TargetRoot(root.Name, root.Path, index == 0))
            .ToArray();
    }

    public static bool IsLegacyCompatibleGitDefault(TargetSpec spec)
    {
        if (spec is null) throw new ArgumentNullException(nameof(spec));

        return spec.Kind == TargetKind.GitRepository
            && spec.Roots.Count == 1
            && string.IsNullOrEmpty(spec.Roots[0].Name)
            && spec.UseDefaultIndexExcludes
            && !spec.UseDefaultDirectoryExcludes
            && (spec.IndexExcludeGlobs is null || spec.IndexExcludeGlobs.Count == 0);
    }

    private static TargetSpec CreateCore(
        TargetKind kind,
        IReadOnlyList<TargetRootSpec> roots,
        IReadOnlyList<string>? indexExcludeGlobs,
        bool useDefaultIndexExcludes,
        bool useDefaultDirectoryExcludes)
    {
        if (roots is null) throw new ArgumentNullException(nameof(roots));
        if (roots.Count == 0) throw new ArgumentException("at least one root is required", nameof(roots));

        var normalizedRoots = NormalizeRoots(kind, roots);
        var normalizedGlobs = NormalizeExcludeGlobs(indexExcludeGlobs);

        return new TargetSpec(
            Kind: kind,
            Roots: normalizedRoots,
            IndexExcludeGlobs: normalizedGlobs,
            UseDefaultIndexExcludes: useDefaultIndexExcludes,
            UseDefaultDirectoryExcludes: useDefaultDirectoryExcludes);
    }

    private static IReadOnlyList<TargetRootSpec> NormalizeRoots(TargetKind kind, IReadOnlyList<TargetRootSpec> roots)
    {
        var normalized = new List<TargetRootSpec>(roots.Count);
        foreach (var root in roots)
        {
            if (root is null) throw new ArgumentException("root entries must be non-null", nameof(roots));

            var normalizedPath = TargetPathUtilities.NormalizeDirectoryPath(root.Path);
            var normalizedName = NormalizeRootName(root.Name);
            normalized.Add(new TargetRootSpec(normalizedName, normalizedPath));
        }

        if (normalized.Count == 1)
            return new[] { normalized[0] with { Name = null } };

        foreach (var root in normalized)
        {
            if (string.IsNullOrEmpty(root.Name))
                throw new ArgumentException("multi-root targets require a unique name for each root", nameof(roots));
        }

        var ordered = normalized
            .OrderBy(static root => root.Name, StringComparer.Ordinal)
            .ToArray();

        EnsureDistinctNames(ordered);
        EnsureDisjointRoots(ordered);

        if (kind == TargetKind.DirectoryTree)
            throw new ArgumentException("directory-tree targets must have exactly one root", nameof(roots));

        return ordered;
    }

    private static IReadOnlyList<string>? NormalizeExcludeGlobs(IReadOnlyList<string>? indexExcludeGlobs)
    {
        if (indexExcludeGlobs is null || indexExcludeGlobs.Count == 0)
            return null;

        return indexExcludeGlobs
            .Where(static glob => !string.IsNullOrWhiteSpace(glob))
            .Select(static glob => glob.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static glob => glob, StringComparer.Ordinal)
            .ToArray();
    }

    private static string? NormalizeRootName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var trimmed = name.Trim();
        if (trimmed is "." or "..")
            throw new ArgumentException("root names '.' and '..' are not allowed", nameof(name));

        if (trimmed.IndexOfAny(['/', '\\', '=']) >= 0)
            throw new ArgumentException(
                $"root name '{trimmed}' contains a reserved character; '/', '\\', and '=' are not allowed",
                nameof(name));

        foreach (var ch in trimmed)
        {
            if (char.IsControl(ch))
                throw new ArgumentException($"root name '{trimmed}' contains control characters", nameof(name));
        }

        return trimmed;
    }

    private static void EnsureDistinctNames(IReadOnlyList<TargetRootSpec> roots)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (!names.Add(root.Name!))
                throw new ArgumentException($"duplicate root name '{root.Name}'", nameof(roots));
        }
    }

    private static void EnsureDisjointRoots(IReadOnlyList<TargetRootSpec> roots)
    {
        for (var i = 0; i < roots.Count; i++)
        {
            for (var j = i + 1; j < roots.Count; j++)
            {
                var a = roots[i].Path;
                var b = roots[j].Path;
                var same = string.Equals(
                    TargetPathUtilities.NormalizeForComparison(a),
                    TargetPathUtilities.NormalizeForComparison(b),
                    StringComparison.Ordinal);
                if (same)
                    throw new ArgumentException($"roots '{a}' and '{b}' resolve to the same directory", nameof(roots));

                if (TargetPathUtilities.IsSameOrUnder(a, b) || TargetPathUtilities.IsSameOrUnder(b, a))
                    throw new ArgumentException($"roots '{a}' and '{b}' overlap or nest", nameof(roots));
            }
        }
    }
}
