using System;
using System.Collections.Generic;
using System.IO;
using Indexed.Targets;

namespace Indexed.Service;

internal static class DaemonCommandLine
{
    public static DaemonOptions ParseOptions(IReadOnlyList<string> args)
    {
        if (args is null) throw new ArgumentNullException(nameof(args));

        string? positionalRepoRoot = null;
        string? repoRoot = null;
        string? appData = null;
        var idleSeconds = (int)TimeSpan.FromMinutes(30).TotalSeconds;
        long maxIndexableFileBytes = TargetIndexDefaults.DefaultMaxIndexableFileBytes;
        var includeGlobs = new List<string>();
        var excludeGlobs = new List<string>();
        var useDefaultExcludes = true;
        var useDefaultDirectoryExcludes = true;
        var updateMode = IndexUpdateMode.Live;
        List<string>? rootArgs = null;

        string TakeArg(ref int i, string flag)
        {
            if (i + 1 >= args.Count)
                throw new ArgumentException($"{flag} requires a value", nameof(args));
            return args[++i];
        }

        var start = 0;
        if (args.Count > 0 && !args[0].StartsWith("-", StringComparison.Ordinal))
        {
            positionalRepoRoot = args[0];
            start = 1;
        }

        for (var i = start; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--repo-root":
                    repoRoot = TakeArg(ref i, args[i]);
                    break;
                case "--root":
                    (rootArgs ??= new List<string>()).Add(TakeArg(ref i, args[i]));
                    break;
                case "--idle-timeout-seconds":
                    if (!int.TryParse(TakeArg(ref i, args[i]), out idleSeconds) || idleSeconds < 0)
                        throw new ArgumentException("--idle-timeout-seconds requires a non-negative integer", nameof(args));
                    break;
                case "--app-data":
                    appData = TakeArg(ref i, args[i]);
                    break;
                case "--include-index":
                    includeGlobs.Add(TakeArg(ref i, args[i]));
                    break;
                case "--exclude-index":
                    excludeGlobs.Add(TakeArg(ref i, args[i]));
                    break;
                case "--max-indexable-file-bytes":
                    if (!long.TryParse(TakeArg(ref i, args[i]), out maxIndexableFileBytes) || maxIndexableFileBytes <= 0)
                        throw new ArgumentException("--max-indexable-file-bytes requires a positive integer", nameof(args));
                    break;
                case "--max-indexable-file-mb":
                    if (!long.TryParse(TakeArg(ref i, args[i]), out var maxMb) || maxMb <= 0)
                        throw new ArgumentException("--max-indexable-file-mb requires a positive integer", nameof(args));
                    checked
                    {
                        maxIndexableFileBytes = maxMb * 1024L * 1024L;
                    }
                    break;
                case "--index-updates":
                    updateMode = ParseUpdateMode(TakeArg(ref i, args[i]));
                    break;
                case "--manual-index-updates":
                    updateMode = IndexUpdateMode.Manual;
                    break;
                case "--no-default-excludes":
                    useDefaultExcludes = false;
                    break;
                case "--no-default-directory-excludes":
                    useDefaultDirectoryExcludes = false;
                    break;
                default:
                    throw new ArgumentException($"unknown option: {args[i]}", nameof(args));
            }
        }

        if (!string.IsNullOrEmpty(positionalRepoRoot) && !string.IsNullOrEmpty(repoRoot))
            throw new ArgumentException("repo root cannot be supplied both positionally and via --repo-root", nameof(args));

        repoRoot ??= positionalRepoRoot;

        if (!string.IsNullOrEmpty(repoRoot) && rootArgs is { Count: > 0 })
            throw new ArgumentException("--repo-root and --root are mutually exclusive", nameof(args));

        IReadOnlyList<TargetRootSpec>? roots = null;
        if (rootArgs is { Count: > 0 })
            roots = TargetRootArgumentParser.NormalizeSelection(rootArgs);

        if (!useDefaultDirectoryExcludes && roots is null)
            throw new ArgumentException(
                "--no-default-directory-excludes requires one or more --root arguments",
                nameof(args));

        return new DaemonOptions
        {
            RepoRoot = roots is null ? repoRoot ?? Directory.GetCurrentDirectory() : null,
            TargetSelection = roots is not null
                ? new TargetSelection
                {
                    Roots = roots,
                    IndexIncludeGlobs = includeGlobs.Count > 0 ? includeGlobs : null,
                    IndexExcludeGlobs = excludeGlobs.Count > 0 ? excludeGlobs : null,
                    UseDefaultIndexExcludes = useDefaultExcludes,
                    UseDefaultDirectoryExcludes = useDefaultDirectoryExcludes,
                    MaxIndexableFileBytes = maxIndexableFileBytes,
                    UpdateMode = updateMode,
                }
                : null,
            AppDataBase = appData,
            IdleTimeout = TimeSpan.FromSeconds(idleSeconds),
            IndexIncludeGlobs = roots is null && includeGlobs.Count > 0 ? includeGlobs : null,
            IndexExcludeGlobs = roots is null && excludeGlobs.Count > 0 ? excludeGlobs : null,
            UseDefaultIndexExcludes = useDefaultExcludes,
            UseDefaultDirectoryExcludes = roots is not null && useDefaultDirectoryExcludes,
            MaxIndexableFileBytes = maxIndexableFileBytes,
            UpdateMode = updateMode,
        };
    }

    private static IndexUpdateMode ParseUpdateMode(string value)
        => value switch
        {
            "live" => IndexUpdateMode.Live,
            "manual" => IndexUpdateMode.Manual,
            _ => throw new ArgumentException(
                $"--index-updates must be one of: live, manual; got '{value}'",
                nameof(value)),
        };
}
