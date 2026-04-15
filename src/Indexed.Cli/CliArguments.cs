using System;
using System.Collections.Generic;
using Indexed.Abstractions;

namespace Indexed.Cli;

/// <summary>
/// Parsed CLI invocation — a tagged record covering every supported command.
/// </summary>
/// <remarks>
/// <para>
/// Produced by <see cref="ArgumentParser.Parse"/>. Stored as a record so tests
/// can assert parser output directly without spawning a real command.
/// </para>
/// </remarks>
public sealed record CliArguments
{
    /// <summary>Which top-level verb the user invoked.</summary>
    public required CliCommand Command { get; init; }

    /// <summary>Search pattern (required for <see cref="CliCommand.Find"/>).</summary>
    public string? Pattern { get; init; }

    /// <summary>Query mode; defaults to <see cref="QueryMode.Auto"/>.</summary>
    public QueryMode Mode { get; init; } = QueryMode.Auto;

    /// <summary>Whether <see cref="Pattern"/> is a regex rather than literal.</summary>
    public bool IsRegex { get; init; }

    /// <summary>Case-sensitive flag for code searches.</summary>
    public bool CaseSensitive { get; init; }

    /// <summary>Optional include glob.</summary>
    public string? PathGlob { get; init; }

    /// <summary>Optional exclude globs.</summary>
    public IReadOnlyList<string>? ExcludeGlob { get; init; }

    /// <summary>Kind filter for prose results.</summary>
    public IReadOnlyList<SpanKind>? KindFilter { get; init; }

    /// <summary>Lines of leading context.</summary>
    public int ContextBefore { get; init; }

    /// <summary>Lines of trailing context.</summary>
    public int ContextAfter { get; init; }

    /// <summary>Global match cap.</summary>
    public int MaxMatches { get; init; } = 200;

    /// <summary>Per-file match cap.</summary>
    public int MaxMatchesPerFile { get; init; } = 20;

    /// <summary>Emit raw JSON instead of ripgrep-style text.</summary>
    public bool EmitJson { get; init; }

    /// <summary>Override the repository root (defaults to <c>cwd</c>).</summary>
    public string? RepoRoot { get; init; }

    /// <summary>Parse error message for <see cref="CliCommand.Help"/>.</summary>
    public string? Diagnostic { get; init; }
}

/// <summary>Top-level CLI verb.</summary>
public enum CliCommand
{
    /// <summary>Default when parsing fails or --help is requested.</summary>
    Help,

    /// <summary>Run a query against the daemon.</summary>
    Find,

    /// <summary>Print daemon status as JSON.</summary>
    Status,

    /// <summary>Force a rescan.</summary>
    Rescan,

    /// <summary>Ask the daemon to exit gracefully.</summary>
    Stop,
}
