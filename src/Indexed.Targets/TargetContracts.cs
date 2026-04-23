using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Indexed.Targets;

public sealed record TargetRootSpec(string? Name, string Path);

public sealed record TargetRoot(string? Name, string AbsolutePath, bool IsPrimary);

public readonly record struct LogicalPath(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct EnumeratedFile(
    TargetRoot Root,
    string RelativePath,
    LogicalPath LogicalPath,
    string AbsolutePath);

public sealed record TargetSpec(
    TargetKind Kind,
    IReadOnlyList<TargetRootSpec> Roots,
    IReadOnlyList<string>? IndexExcludeGlobs,
    bool UseDefaultIndexExcludes,
    bool UseDefaultDirectoryExcludes);

public interface IIndexTarget
{
    TargetSpec Spec { get; }

    string TargetId { get; }

    IReadOnlyList<TargetRoot> Roots { get; }

    IAsyncEnumerable<EnumeratedFile> EnumerateFilesAsync(CancellationToken cancellationToken = default);

    bool TryMapAbsolutePath(string absolutePath, out LogicalPath logicalPath);

    bool TryResolveLogicalPath(LogicalPath logicalPath, out EnumeratedFile file);

    string ResolveAbsolutePath(LogicalPath logicalPath);

    string? GetCurrentRevisionToken(CancellationToken cancellationToken = default);
}

public interface IFileCountHintTarget
{
    ValueTask<int?> GetFileCountHintAsync(CancellationToken cancellationToken = default);
}

public interface IRevisionTracker : IDisposable
{
    string? LastKnownRevisionToken { get; }

    void Start();
}

public interface IRevisionDiffTarget
{
    ValueTask ExpandRevisionChangeAsync(
        string oldRevisionToken,
        string newRevisionToken,
        ICollection<string> toUpsert,
        ICollection<string> toDelete,
        CancellationToken cancellationToken = default);
}

public interface IExplicitBinaryPathProvider
{
    ValueTask<IReadOnlySet<string>> GetExplicitBinaryLogicalPathsAsync(
        IReadOnlyList<EnumeratedFile>? files = null,
        CancellationToken cancellationToken = default);
}
