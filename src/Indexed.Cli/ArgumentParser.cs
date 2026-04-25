using System;
using System.Collections.Generic;
using Indexed.Abstractions;
using Indexed.Targets;

namespace Indexed.Cli;

/// <summary>
/// Minimal argument parser for the <c>idx</c> CLI.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately hand-rolled rather than taking a dependency on
/// <c>System.CommandLine</c> — the surface is small and the parser's error
/// messages need tight alignment with the tool's HTTP error shape. The
/// parser is a pure function of <paramref name="args"/> so tests assert
/// output without a test harness.
/// </para>
/// <para>
/// Syntax recognized:
/// </para>
/// <code>
/// idx find &lt;pattern&gt; [--mode auto|code|prose] [--regex] [--case-sensitive]
///                    [--glob &lt;g&gt;] [--exclude &lt;g&gt;]* [--kind &lt;k&gt;]*
///                    [--context-before N] [--context-after N] [-C N]
///                    [--max-matches N] [--max-matches-per-file N]
///                    [--json] [--repo-root &lt;dir&gt;] [--root &lt;dir&gt;|&lt;label=dir&gt;]...
///                    [--include-index &lt;g&gt;]* [--exclude-index &lt;g&gt;]*
///                    [--max-indexable-file-mb N|--max-indexable-file-bytes N]
///                    [--idle-timeout-seconds N]
/// idx status [--repo-root &lt;dir&gt;|--root &lt;dir&gt;|&lt;label=dir&gt; ...] [--json] [daemon options]
/// idx rescan [--repo-root &lt;dir&gt;|--root &lt;dir&gt;|&lt;label=dir&gt; ...] [daemon options]
/// idx stop [--repo-root &lt;dir&gt;|--root &lt;dir&gt;|&lt;label=dir&gt; ...] [daemon options]
/// idx daemons [--json]
/// idx --help | idx -h
/// </code>
/// </remarks>
public static class ArgumentParser
{
    /// <summary>Parse <paramref name="args"/> into a <see cref="CliArguments"/> record.</summary>
    public static CliArguments Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] is "-h" or "--help")
            return new CliArguments { Command = CliCommand.Help };

        var verb = args[0] switch
        {
            "find" => CliCommand.Find,
            "status" => CliCommand.Status,
            "rescan" => CliCommand.Rescan,
            "stop" => CliCommand.Stop,
            "daemons" => CliCommand.Daemons,
            _ => CliCommand.Help,
        };

        if (verb == CliCommand.Help)
            return new CliArguments { Command = CliCommand.Help, Diagnostic = $"unknown command: {args[0]}" };

        // Defaults
        string? pattern = null;
        var mode = QueryMode.Auto;
        var isRegex = false;
        var caseSensitive = false;
        string? pathGlob = null;
        List<string>? excludeGlob = null;
        List<string>? indexIncludeGlob = null;
        List<string>? indexExcludeGlob = null;
        long? maxIndexableFileBytes = null;
        var noDefaultExcludes = false;
        List<SpanKind>? kindFilter = null;
        var contextBefore = 0;
        var contextAfter = 0;
        var maxMatches = 200;
        var maxMatchesPerFile = 20;
        var emitJson = false;
        string? repoRoot = null;
        List<string>? rootArgs = null;
        int? idleTimeoutSeconds = null;
        var noDefaultDirectoryExcludes = false;

        string? TakeArg(ref int i, string flag)
        {
            if (i + 1 >= args.Count)
                throw new ArgumentParseException($"{flag} requires a value");
            return args[++i];
        }

        try
        {
            for (var i = 1; i < args.Count; i++)
            {
                var a = args[i];
                switch (a)
                {
                    case "--mode":
                        mode = ParseMode(TakeArg(ref i, a)!);
                        break;
                    case "--regex":
                    case "-e":
                        isRegex = true;
                        break;
                    case "--case-sensitive":
                    case "-s":
                        caseSensitive = true;
                        break;
                    case "--glob":
                    case "-g":
                        pathGlob = TakeArg(ref i, a);
                        break;
                    case "--exclude":
                        (excludeGlob ??= new List<string>()).Add(TakeArg(ref i, a)!);
                        break;
                    case "--include-index":
                        (indexIncludeGlob ??= new List<string>()).Add(TakeArg(ref i, a)!);
                        break;
                    case "--exclude-index":
                        (indexExcludeGlob ??= new List<string>()).Add(TakeArg(ref i, a)!);
                        break;
                    case "--max-indexable-file-bytes":
                        maxIndexableFileBytes = ParseLong(TakeArg(ref i, a)!, a, positive: true);
                        break;
                    case "--max-indexable-file-mb":
                        checked
                        {
                            maxIndexableFileBytes = ParseLong(TakeArg(ref i, a)!, a, positive: true) * 1024L * 1024L;
                        }
                        break;
                    case "--no-default-excludes":
                        noDefaultExcludes = true;
                        break;
                    case "--kind":
                        (kindFilter ??= new List<SpanKind>()).Add(ParseKind(TakeArg(ref i, a)!));
                        break;
                    case "--context-before":
                    case "-B":
                        contextBefore = ParseInt(TakeArg(ref i, a)!, a);
                        break;
                    case "--context-after":
                    case "-A":
                        contextAfter = ParseInt(TakeArg(ref i, a)!, a);
                        break;
                    case "-C":
                    case "--context":
                        var ctx = ParseInt(TakeArg(ref i, a)!, a);
                        contextBefore = ctx;
                        contextAfter = ctx;
                        break;
                    case "--max-matches":
                        maxMatches = ParseInt(TakeArg(ref i, a)!, a);
                        break;
                    case "--max-matches-per-file":
                        maxMatchesPerFile = ParseInt(TakeArg(ref i, a)!, a);
                        break;
                    case "--json":
                        emitJson = true;
                        break;
                    case "--repo-root":
                        repoRoot = TakeArg(ref i, a);
                        break;
                    case "--root":
                        (rootArgs ??= new List<string>()).Add(TakeArg(ref i, a)!);
                        break;
                    case "--idle-timeout-seconds":
                        idleTimeoutSeconds = ParseInt(TakeArg(ref i, a)!, a);
                        break;
                    case "--no-default-directory-excludes":
                        noDefaultDirectoryExcludes = true;
                        break;
                    case "-h":
                    case "--help":
                        return new CliArguments { Command = CliCommand.Help };
                    default:
                        if (a.StartsWith('-'))
                            throw new ArgumentParseException($"unknown option: {a}");
                        if (pattern is null && verb == CliCommand.Find)
                            pattern = a;
                        else
                            throw new ArgumentParseException($"unexpected positional argument: {a}");
                        break;
                }
            }

            if (verb == CliCommand.Find && string.IsNullOrEmpty(pattern))
                throw new ArgumentParseException("find requires a <pattern>");

            if (!string.IsNullOrEmpty(repoRoot) && rootArgs is { Count: > 0 })
                throw new ArgumentParseException("--repo-root and --root are mutually exclusive");

            var roots = NormalizeRoots(rootArgs);

            if (noDefaultDirectoryExcludes && roots is null)
                throw new ArgumentParseException("--no-default-directory-excludes requires one or more --root arguments");

            ValidateVerbSpecificUsage(
                verb,
                pattern,
                mode,
                isRegex,
                caseSensitive,
                pathGlob,
                excludeGlob,
                indexIncludeGlob,
                indexExcludeGlob,
                maxIndexableFileBytes,
                noDefaultExcludes,
                kindFilter,
                contextBefore,
                contextAfter,
                maxMatches,
                maxMatchesPerFile,
                repoRoot,
                roots,
                idleTimeoutSeconds,
                noDefaultDirectoryExcludes);

            return new CliArguments
            {
                Command = verb,
                Pattern = pattern,
                Mode = mode,
                IsRegex = isRegex,
                CaseSensitive = caseSensitive,
                PathGlob = pathGlob,
                ExcludeGlob = excludeGlob,
                IndexIncludeGlob = indexIncludeGlob,
                IndexExcludeGlob = indexExcludeGlob,
                MaxIndexableFileBytes = maxIndexableFileBytes,
                NoDefaultExcludes = noDefaultExcludes,
                NoDefaultDirectoryExcludes = noDefaultDirectoryExcludes,
                KindFilter = kindFilter,
                ContextBefore = contextBefore,
                ContextAfter = contextAfter,
                MaxMatches = maxMatches,
                MaxMatchesPerFile = maxMatchesPerFile,
                EmitJson = emitJson,
                RepoRoot = repoRoot,
                Roots = roots,
                IdleTimeoutSeconds = idleTimeoutSeconds,
            };
        }
        catch (ArgumentParseException ex)
        {
            return new CliArguments { Command = CliCommand.Help, Diagnostic = ex.Message };
        }
        catch (OverflowException ex)
        {
            return new CliArguments { Command = CliCommand.Help, Diagnostic = ex.Message };
        }
    }

    private static QueryMode ParseMode(string s) => s switch
    {
        "auto" => QueryMode.Auto,
        "code" => QueryMode.Code,
        "prose" => QueryMode.Prose,
        _ => throw new ArgumentParseException($"invalid --mode: {s}"),
    };

    private static SpanKind ParseKind(string s) => s switch
    {
        "code" => SpanKind.Code,
        "markdown" => SpanKind.Markdown,
        "plain-text" => SpanKind.PlainText,
        "xml-doc" => SpanKind.XmlDoc,
        "line-comment-block" => SpanKind.LineCommentBlock,
        "block-comment" => SpanKind.BlockComment,
        _ => throw new ArgumentParseException($"invalid --kind: {s}"),
    };

    private static int ParseInt(string s, string flag)
    {
        if (!int.TryParse(s, out var n))
            throw new ArgumentParseException($"{flag} expects an integer; got '{s}'");
        if (n < 0)
            throw new ArgumentParseException($"{flag} must be non-negative; got {n}");
        return n;
    }

    private static long ParseLong(string s, string flag, bool positive)
    {
        if (!long.TryParse(s, out var n))
            throw new ArgumentParseException($"{flag} expects an integer; got '{s}'");
        if (positive ? n <= 0 : n < 0)
        {
            throw new ArgumentParseException(
                positive
                    ? $"{flag} must be positive; got {n}"
                    : $"{flag} must be non-negative; got {n}");
        }

        return n;
    }

    private static IReadOnlyList<TargetRootSpec>? NormalizeRoots(List<string>? rootArgs)
    {
        if (rootArgs is null || rootArgs.Count == 0)
            return null;

        try
        {
            return TargetRootArgumentParser.NormalizeSelection(rootArgs);
        }
        catch (ArgumentParseException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentParseException(ex.Message);
        }
    }

    private static void ValidateVerbSpecificUsage(
        CliCommand verb,
        string? pattern,
        QueryMode mode,
        bool isRegex,
        bool caseSensitive,
        string? pathGlob,
        IReadOnlyList<string>? excludeGlob,
        IReadOnlyList<string>? indexIncludeGlob,
        IReadOnlyList<string>? indexExcludeGlob,
        long? maxIndexableFileBytes,
        bool noDefaultExcludes,
        IReadOnlyList<SpanKind>? kindFilter,
        int contextBefore,
        int contextAfter,
        int maxMatches,
        int maxMatchesPerFile,
        string? repoRoot,
        IReadOnlyList<TargetRootSpec>? roots,
        int? idleTimeoutSeconds,
        bool noDefaultDirectoryExcludes)
    {
        if (verb != CliCommand.Daemons)
            return;

        var hasUnsupportedOptions =
            pattern is not null
            || mode != QueryMode.Auto
            || isRegex
            || caseSensitive
            || pathGlob is not null
            || excludeGlob is { Count: > 0 }
            || indexIncludeGlob is { Count: > 0 }
            || indexExcludeGlob is { Count: > 0 }
            || maxIndexableFileBytes is not null
            || noDefaultExcludes
            || noDefaultDirectoryExcludes
            || kindFilter is { Count: > 0 }
            || contextBefore != 0
            || contextAfter != 0
            || maxMatches != 200
            || maxMatchesPerFile != 20
            || repoRoot is not null
            || roots is { Count: > 0 }
            || idleTimeoutSeconds is not null;

        if (hasUnsupportedOptions)
            throw new ArgumentParseException("idx daemons only supports --json");
    }

    /// <summary>
    /// Human-readable usage text. Kept in one place so Help and the
    /// diagnostic path share it.
    /// </summary>
    public static string UsageText =>
        """
        idx — Indexed CLI

        Usage:
          idx find <pattern> [options]
          idx status [--json] [--repo-root <dir>] [--root <dir>|<label=dir>]...
          idx rescan [--repo-root <dir>] [--root <dir>|<label=dir>]...
          idx stop [--repo-root <dir>] [--root <dir>|<label=dir>]...
          idx daemons [--json]

        query options:
          --mode <auto|code|prose>      query mode (default: auto)
          --regex, -e                   treat pattern as regex
          --case-sensitive, -s          case-sensitive match
          --glob, -g <glob>             restrict to paths matching glob
          --exclude <glob>              exclude paths (repeatable)
          --kind <kind>                 filter by span kind (repeatable)
          --context-before, -B <n>      lines of leading context
          --context-after,  -A <n>      lines of trailing context
          --context, -C <n>             symmetric context
          --max-matches <n>             global cap (default 200)
          --max-matches-per-file <n>    per-file cap (default 20)
          --json                        emit raw JSON response

        daemon launch options:
          --repo-root <dir>             override repository root (git mode)
          --root <dir>|<label=dir>      select directory target roots
          --include-index <glob>        only index files matching glob (repeatable)
          --exclude-index <glob>        skip files from indexing (repeatable)
          --max-indexable-file-mb <n>   daemon file-size cap in MiB (new daemon only)
          --max-indexable-file-bytes <n>
                                        daemon file-size cap in bytes (new daemon only)
          --no-default-excludes         do not apply built-in default exclude list
                                          (lockfiles, minified bundles, generated C#)
          --idle-timeout-seconds <n>    daemon idle timeout override (new daemon only)
          --no-default-directory-excludes
                                        do not apply directory-mode default excludes
                                        (.git, node_modules, bin/obj, caches, build outputs)
        """;
}

/// <summary>Raised by <see cref="ArgumentParser.Parse"/> for malformed input.</summary>
internal sealed class ArgumentParseException : Exception
{
    public ArgumentParseException(string message) : base(message) { }
}
