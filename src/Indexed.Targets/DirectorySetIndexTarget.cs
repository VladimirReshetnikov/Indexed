using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Indexed.Targets;

public sealed class DirectorySetIndexTarget : IIndexTarget
{
    private readonly IReadOnlyDictionary<string, TargetRoot> _rootsByName;
    private readonly IReadOnlyList<(TargetRoot Root, string Prefix)> _rootsWithPrefixes;

    private DirectorySetIndexTarget(TargetSpec spec, string targetId, IReadOnlyList<TargetRoot> roots)
    {
        Spec = spec;
        TargetId = targetId;
        Roots = roots;
        _rootsByName = roots.ToDictionary(static root => root.Name!, StringComparer.Ordinal);
        _rootsWithPrefixes = roots
            .Select(static root => (root, TargetPathUtilities.EnsureDirectorySeparatorSuffix(root.AbsolutePath)))
            .ToArray();
    }

    public TargetSpec Spec { get; }

    public string TargetId { get; }

    public IReadOnlyList<TargetRoot> Roots { get; }

    public static DirectorySetIndexTarget Open(
        IReadOnlyList<TargetRootSpec> roots,
        IReadOnlyList<string>? indexExcludeGlobs = null,
        bool useDefaultIndexExcludes = true,
        bool useDefaultDirectoryExcludes = true,
        IReadOnlyList<string>? indexIncludeGlobs = null,
        long maxIndexableFileBytes = TargetIndexDefaults.DefaultMaxIndexableFileBytes)
    {
        var spec = TargetSpecFactory.CreateDirectorySet(
            roots,
            indexExcludeGlobs,
            useDefaultIndexExcludes,
            useDefaultDirectoryExcludes,
            indexIncludeGlobs,
            maxIndexableFileBytes);
        return new DirectorySetIndexTarget(
            spec,
            Indexed.Targets.TargetId.Compute(spec),
            TargetSpecFactory.MaterializeRoots(spec));
    }

    public async IAsyncEnumerable<EnumeratedFile> EnumerateFilesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var root in Roots)
        {
            foreach (var absolutePath in DirectoryTargetFileEnumerator.EnumerateFilesRecursive(root.AbsolutePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = TargetPathUtilities
                    .GetRelativePosixPath(root.AbsolutePath, absolutePath);
                var logicalPath = new LogicalPath($"{root.Name}/{relativePath}");
                yield return new EnumeratedFile(root, relativePath, logicalPath, absolutePath);
                await Task.CompletedTask.ConfigureAwait(false);
            }
        }
    }

    public bool TryMapAbsolutePath(string absolutePath, out LogicalPath logicalPath)
    {
        logicalPath = default;
        if (string.IsNullOrWhiteSpace(absolutePath))
            return false;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(absolutePath);
        }
        catch
        {
            return false;
        }

        foreach (var (root, prefix) in _rootsWithPrefixes)
        {
            if (!fullPath.StartsWith(prefix, TargetPathUtilities.PathComparison)
                && !string.Equals(fullPath, root.AbsolutePath, TargetPathUtilities.PathComparison))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(root.AbsolutePath, fullPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (relativePath == ".")
                return false;
            logicalPath = new LogicalPath($"{root.Name}/{relativePath}");
            return true;
        }

        return false;
    }

    public bool TryResolveLogicalPath(LogicalPath logicalPath, out EnumeratedFile file)
    {
        file = default;
        if (!TryParseLogicalPath(logicalPath, out var root, out var relativePath))
            return false;

        string absolutePath;
        try
        {
            absolutePath = ResolveAbsolutePath(logicalPath);
        }
        catch
        {
            return false;
        }

        file = new EnumeratedFile(root, relativePath, logicalPath, absolutePath);
        return true;
    }

    public string ResolveAbsolutePath(LogicalPath logicalPath)
    {
        if (!TryParseLogicalPath(logicalPath, out var root, out var relativePath))
            throw new ArgumentException("logical path is not valid for this directory set", nameof(logicalPath));

        var combined = Path.Combine(root.AbsolutePath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var fullPath = Path.GetFullPath(combined);
        if (!TargetPathUtilities.IsSameOrUnder(fullPath, root.AbsolutePath))
            throw new InvalidOperationException($"logical path '{logicalPath.Value}' resolves outside root '{root.Name}'");
        return fullPath;
    }

    public string? GetCurrentRevisionToken(CancellationToken cancellationToken = default) => null;

    private bool TryParseLogicalPath(LogicalPath logicalPath, out TargetRoot root, out string relativePath)
    {
        root = default!;
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(logicalPath.Value))
            return false;

        var slash = logicalPath.Value.IndexOf('/');
        if (slash <= 0 || slash == logicalPath.Value.Length - 1)
            return false;

        var rootName = logicalPath.Value[..slash];
        relativePath = logicalPath.Value[(slash + 1)..];
        return _rootsByName.TryGetValue(rootName, out root!);
    }
}
