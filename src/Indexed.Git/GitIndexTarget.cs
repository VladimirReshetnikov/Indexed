using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Targets;

namespace Indexed.Git;

public sealed class GitIndexTarget : IIndexTarget, IRevisionDiffTarget, IExplicitBinaryPathProvider, IFileCountHintTarget
{
    private readonly string _repoRootWithSeparator;

    private GitIndexTarget(
        GitRepository repository,
        TargetSpec spec,
        string targetId,
        string repositoryId,
        IReadOnlyList<TargetRoot> roots)
    {
        Repository = repository;
        Spec = spec;
        TargetId = targetId;
        RepositoryId = repositoryId;
        Roots = roots;
        _repoRootWithSeparator = TargetPathUtilities.EnsureDirectorySeparatorSuffix(repository.RepoRoot);
    }

    public GitRepository Repository { get; }

    public string RepositoryId { get; }

    public TargetSpec Spec { get; }

    public string TargetId { get; }

    public IReadOnlyList<TargetRoot> Roots { get; }

    public static GitIndexTarget Open(
        string directory,
        IReadOnlyList<string>? indexExcludeGlobs = null,
        bool useDefaultIndexExcludes = true,
        bool useDefaultDirectoryExcludes = false,
        CancellationToken cancellationToken = default)
    {
        var repository = GitRepository.Open(directory);
        var spec = TargetSpecFactory.CreateGitRepository(
            repository.RepoRoot,
            indexExcludeGlobs,
            useDefaultIndexExcludes,
            useDefaultDirectoryExcludes);
        var legacyRepoId = LegacyRepoId.Compute(repository.RepoRoot, repository.GetFirstCommitSha(cancellationToken));
        var targetId = Indexed.Targets.TargetId.Compute(spec, legacyRepoId);
        return new GitIndexTarget(
            repository,
            spec,
            targetId,
            legacyRepoId,
            TargetSpecFactory.MaterializeRoots(spec));
    }

    public async IAsyncEnumerable<EnumeratedFile> EnumerateFilesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var root = Roots[0];
        foreach (var relativePath in Repository.EnumerateFiles(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var absolutePath = Path.Combine(Repository.RepoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            yield return new EnumeratedFile(
                Root: root,
                RelativePath: relativePath,
                LogicalPath: new LogicalPath(relativePath),
                AbsolutePath: absolutePath);
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

        if (!fullPath.StartsWith(_repoRootWithSeparator, TargetPathUtilities.PathComparison)
            && !string.Equals(fullPath, Repository.RepoRoot, TargetPathUtilities.PathComparison))
        {
            return false;
        }

        var relative = Path.GetRelativePath(Repository.RepoRoot, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        logicalPath = new LogicalPath(relative);
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

        var combined = Path.Combine(Repository.RepoRoot, logicalPath.Value.Replace('/', Path.DirectorySeparatorChar));
        var fullPath = Path.GetFullPath(combined);
        if (!TargetPathUtilities.IsSameOrUnder(fullPath, Repository.RepoRoot))
            throw new InvalidOperationException($"logical path '{logicalPath.Value}' resolves outside the repo root");
        return fullPath;
    }

    public string? GetCurrentRevisionToken(CancellationToken cancellationToken = default)
    {
        var head = Repository.GetHeadSha(cancellationToken);
        return string.IsNullOrEmpty(head) ? null : head;
    }

    public ValueTask<int?> GetFileCountHintAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<int?>(Repository.EnumerateFiles(cancellationToken).Count);

    public ValueTask ExpandRevisionChangeAsync(
        string oldRevisionToken,
        string newRevisionToken,
        ICollection<string> toUpsert,
        ICollection<string> toDelete,
        CancellationToken cancellationToken = default)
    {
        var diff = Repository.GetDiffTree(oldRevisionToken, newRevisionToken, cancellationToken);
        foreach (var entry in diff)
        {
            switch (entry.Status)
            {
                case DiffStatus.Added:
                case DiffStatus.Modified:
                    toUpsert.Add(entry.Path);
                    break;

                case DiffStatus.Deleted:
                    toDelete.Add(entry.Path);
                    break;

                case DiffStatus.Renamed:
                    if (entry.OldPath is not null) toDelete.Add(entry.OldPath);
                    toUpsert.Add(entry.Path);
                    break;

                case DiffStatus.Copied:
                case DiffStatus.Unmerged:
                    toUpsert.Add(entry.Path);
                    break;
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlySet<string>> GetExplicitBinaryLogicalPathsAsync(
        IReadOnlyList<EnumeratedFile>? files = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlySet<string> paths = files is null
            ? Repository.GetBinaryAttrPaths(cancellationToken)
            : Repository.GetBinaryAttrPaths(files.Select(static file => file.RelativePath).ToArray(), cancellationToken);
        return ValueTask.FromResult(paths);
    }
}
