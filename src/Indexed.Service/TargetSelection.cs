using System;
using System.Collections.Generic;
using System.Threading;
using Indexed.Git;
using Indexed.Targets;

namespace Indexed.Service;

public sealed record TargetSelection
{
    public string? RepoRoot { get; init; }

    public IReadOnlyList<TargetRootSpec>? Roots { get; init; }

    public IReadOnlyList<string>? IndexIncludeGlobs { get; init; }

    public IReadOnlyList<string>? IndexExcludeGlobs { get; init; }

    public bool UseDefaultIndexExcludes { get; init; } = true;

    public bool UseDefaultDirectoryExcludes { get; init; }

    public long MaxIndexableFileBytes { get; init; } = TargetIndexDefaults.DefaultMaxIndexableFileBytes;

    public IIndexTarget OpenTarget(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(RepoRoot) && Roots is { Count: > 0 })
        {
            throw new InvalidOperationException("repoRoot and roots cannot both be specified");
        }

        if (Roots is { Count: > 0 })
        {
            return Roots.Count == 1
                ? DirectoryTreeIndexTarget.Open(
                    Roots[0].Path,
                    IndexExcludeGlobs,
                    UseDefaultIndexExcludes,
                    UseDefaultDirectoryExcludes,
                    IndexIncludeGlobs,
                    MaxIndexableFileBytes)
                : DirectorySetIndexTarget.Open(
                    Roots,
                    IndexExcludeGlobs,
                    UseDefaultIndexExcludes,
                    UseDefaultDirectoryExcludes,
                    IndexIncludeGlobs,
                    MaxIndexableFileBytes);
        }

        if (string.IsNullOrWhiteSpace(RepoRoot))
            throw new InvalidOperationException("either repoRoot or roots must be specified");

        return GitIndexTarget.Open(
            RepoRoot,
            IndexExcludeGlobs,
            UseDefaultIndexExcludes,
            UseDefaultDirectoryExcludes,
            IndexIncludeGlobs,
            MaxIndexableFileBytes,
            cancellationToken);
    }
}
