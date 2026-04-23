using System;
using System.Collections.Generic;

namespace Indexed.Targets;

/// <summary>
/// Parses and formats the command-line root syntax used by both the CLI and
/// the daemon entrypoint.
/// </summary>
public static class TargetRootArgumentParser
{
    public static TargetRootSpec ParseBarePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("root argument must be non-empty", nameof(value));

        return new TargetRootSpec(Name: null, Path: value.Trim());
    }

    public static TargetRootSpec ParseLabeled(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("root argument must be non-empty", nameof(value));

        var trimmed = value.Trim();
        var separator = trimmed.IndexOf('=');
        if (separator <= 0 || separator == trimmed.Length - 1 || !LooksLikeLabel(trimmed[..separator]))
            throw new ArgumentException("root must use LABEL=PATH syntax", nameof(value));

        return new TargetRootSpec(
            Name: trimmed[..separator],
            Path: trimmed[(separator + 1)..]);
    }

    public static bool IsLabeled(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        var separator = trimmed.IndexOf('=');
        return separator > 0
            && separator < trimmed.Length - 1
            && LooksLikeLabel(trimmed[..separator]);
    }

    public static string Format(TargetRootSpec root, bool requireLabel)
    {
        if (root is null) throw new ArgumentNullException(nameof(root));

        if (!requireLabel)
            return root.Path;

        if (string.IsNullOrWhiteSpace(root.Name))
            throw new ArgumentException("labeled formatting requires a non-empty root name", nameof(root));

        return $"{root.Name}={root.Path}";
    }

    public static IReadOnlyList<TargetRootSpec> NormalizeSelection(IReadOnlyList<string> rootArgs)
    {
        if (rootArgs is null) throw new ArgumentNullException(nameof(rootArgs));
        if (rootArgs.Count == 0) throw new ArgumentException("at least one root argument is required", nameof(rootArgs));

        if (rootArgs.Count == 1)
        {
            if (IsLabeled(rootArgs[0]))
                throw new ArgumentException("single-root directory targets use a bare path; omit the label", nameof(rootArgs));

            var root = ParseBarePath(rootArgs[0]);
            return TargetSpecFactory.CreateDirectoryTree(root.Path).Roots;
        }

        var roots = new List<TargetRootSpec>(rootArgs.Count);
        foreach (var raw in rootArgs)
        {
            if (!IsLabeled(raw))
                throw new ArgumentException("multi-root targets require LABEL=PATH syntax for every --root", nameof(rootArgs));

            roots.Add(ParseLabeled(raw));
        }

        return TargetSpecFactory.CreateDirectorySet(roots).Roots;
    }

    private static bool LooksLikeLabel(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        foreach (var ch in candidate)
        {
            if (char.IsControl(ch) || ch is '/' or '\\' or ':' or '=')
                return false;
        }

        return true;
    }
}
