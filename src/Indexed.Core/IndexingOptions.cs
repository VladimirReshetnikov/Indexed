using System;
using System.Collections.Generic;
using Indexed.Targets;

namespace Indexed.Core;

/// <summary>
/// Normalized write-time options shared by full scans, incremental updates,
/// file watchers, and query-time content rehydration.
/// </summary>
public sealed record IndexingOptions
{
    public IndexingOptions(
        IReadOnlyList<string>? includeGlobs = null,
        IReadOnlyList<string>? excludeGlobs = null,
        long maxIndexableFileBytes = TargetIndexDefaults.DefaultMaxIndexableFileBytes)
    {
        if (maxIndexableFileBytes <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxIndexableFileBytes),
                maxIndexableFileBytes,
                "maximum indexable file size must be positive");

        IncludeGlobs = includeGlobs;
        ExcludeGlobs = excludeGlobs;
        MaxIndexableFileBytes = maxIndexableFileBytes;
    }

    public static IndexingOptions Default { get; } = new();

    public IReadOnlyList<string>? IncludeGlobs { get; }

    public IReadOnlyList<string>? ExcludeGlobs { get; }

    public long MaxIndexableFileBytes { get; }
}
