using System;
using System.IO;

namespace Indexed.Targets;

public static class TargetPathUtilities
{
    public static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string NormalizeDirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("path must be non-empty", nameof(path));

        var normalized = Path.GetFullPath(path);
        return TrimTrailingSeparators(normalized);
    }

    public static string NormalizeForComparison(string path)
    {
        var normalized = NormalizeDirectoryPath(path);
        return OperatingSystem.IsWindows()
            ? normalized.ToUpperInvariant()
            : normalized;
    }

    public static string EnsureDirectorySeparatorSuffix(string path)
    {
        var normalized = NormalizeDirectoryPath(path);
        return normalized.EndsWith(Path.DirectorySeparatorChar)
            ? normalized
            : normalized + Path.DirectorySeparatorChar;
    }

    public static bool IsSameOrUnder(string candidatePath, string rootPath)
    {
        var fullCandidate = NormalizeDirectoryPath(candidatePath);
        var fullRoot = NormalizeDirectoryPath(rootPath);

        if (string.Equals(fullCandidate, fullRoot, PathComparison))
            return true;

        return fullCandidate.StartsWith(EnsureDirectorySeparatorSuffix(fullRoot), PathComparison);
    }

    public static string GetRelativePosixPath(string rootPath, string fullPath)
    {
        var relative = Path.GetRelativePath(
            NormalizeDirectoryPath(rootPath),
            NormalizeDirectoryPath(fullPath));
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    public static string TrimTrailingSeparators(string path)
    {
        if (path.Length <= 1)
            return path;

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0)
            return path;

        if (trimmed.EndsWith(':'))
            return trimmed + Path.DirectorySeparatorChar;

        return trimmed;
    }
}
