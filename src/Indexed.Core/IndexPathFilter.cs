using System.Collections.Generic;

namespace Indexed.Core;

/// <summary>
/// Combines index-time include and exclude globs into the final path policy.
/// </summary>
public sealed class IndexPathFilter
{
    private readonly IncludeFilter _includeFilter;
    private readonly ExcludeFilter _excludeFilter;

    public IndexPathFilter(IndexingOptions options)
    {
        _includeFilter = new IncludeFilter(options.IncludeGlobs);
        _excludeFilter = new ExcludeFilter(options.ExcludeGlobs);
    }

    public IndexPathFilter(
        IReadOnlyList<string>? includeGlobs = null,
        IReadOnlyList<string>? excludeGlobs = null)
        : this(new IndexingOptions(includeGlobs, excludeGlobs))
    {
    }

    public bool IsIncluded(string logicalPath) => _includeFilter.IsIncluded(logicalPath);

    public bool IsExcluded(string logicalPath) => _excludeFilter.IsExcluded(logicalPath);

    public bool ShouldIndex(string logicalPath)
        => IsIncluded(logicalPath) && !IsExcluded(logicalPath);
}
