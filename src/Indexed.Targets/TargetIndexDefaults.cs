namespace Indexed.Targets;

/// <summary>
/// Target-level defaults that participate in target identity and must be
/// visible below the core indexing layer.
/// </summary>
public static class TargetIndexDefaults
{
    /// <summary>
    /// Default upper bound for files accepted into the raw-content index.
    /// </summary>
    public const long DefaultMaxIndexableFileBytes = 50L * 1024 * 1024;
}
