using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Indexed.Targets;

public sealed class DirectoryTreeIndexTarget : IIndexTarget
{
    private readonly string _rootWithSeparator;

    private DirectoryTreeIndexTarget(TargetSpec spec, string targetId, IReadOnlyList<TargetRoot> roots)
    {
        Spec = spec;
        TargetId = targetId;
        Roots = roots;
        _rootWithSeparator = TargetPathUtilities.EnsureDirectorySeparatorSuffix(roots[0].AbsolutePath);
    }

    public TargetSpec Spec { get; }

    public string TargetId { get; }

    public IReadOnlyList<TargetRoot> Roots { get; }

    public static DirectoryTreeIndexTarget Open(
        string root,
        IReadOnlyList<string>? indexExcludeGlobs = null,
        bool useDefaultIndexExcludes = true,
        bool useDefaultDirectoryExcludes = true)
    {
        var spec = TargetSpecFactory.CreateDirectoryTree(
            root,
            indexExcludeGlobs,
            useDefaultIndexExcludes,
            useDefaultDirectoryExcludes);
        return new DirectoryTreeIndexTarget(
            spec,
            Indexed.Targets.TargetId.Compute(spec),
            TargetSpecFactory.MaterializeRoots(spec));
    }

    public async IAsyncEnumerable<EnumeratedFile> EnumerateFilesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var root = Roots[0];
        foreach (var absolutePath in DirectoryTargetFileEnumerator.EnumerateFilesRecursive(root.AbsolutePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = TargetPathUtilities
                .GetRelativePosixPath(root.AbsolutePath, absolutePath);
            var logicalPath = new LogicalPath(relativePath);
            yield return new EnumeratedFile(root, relativePath, logicalPath, absolutePath);
            await Task.CompletedTask.ConfigureAwait(false);
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

        if (!fullPath.StartsWith(_rootWithSeparator, TargetPathUtilities.PathComparison)
            && !string.Equals(fullPath, Roots[0].AbsolutePath, TargetPathUtilities.PathComparison))
        {
            return false;
        }

        var relativePath = Path.GetRelativePath(Roots[0].AbsolutePath, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (relativePath == ".")
            return false;
        logicalPath = new LogicalPath(relativePath);
        return true;
    }

    public bool TryResolveLogicalPath(LogicalPath logicalPath, out EnumeratedFile file)
    {
        file = default;
        if (string.IsNullOrWhiteSpace(logicalPath.Value))
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

        file = new EnumeratedFile(
            Root: Roots[0],
            RelativePath: logicalPath.Value,
            LogicalPath: logicalPath,
            AbsolutePath: absolutePath);
        return true;
    }

    public string ResolveAbsolutePath(LogicalPath logicalPath)
    {
        if (string.IsNullOrWhiteSpace(logicalPath.Value))
            throw new ArgumentException("logical path must be non-empty", nameof(logicalPath));

        var combined = Path.Combine(Roots[0].AbsolutePath, logicalPath.Value.Replace('/', Path.DirectorySeparatorChar));
        var fullPath = Path.GetFullPath(combined);
        if (!TargetPathUtilities.IsSameOrUnder(fullPath, Roots[0].AbsolutePath))
            throw new InvalidOperationException($"logical path '{logicalPath.Value}' resolves outside the root");
        return fullPath;
    }

    public string? GetCurrentRevisionToken(CancellationToken cancellationToken = default) => null;
}
