using System;
using System.Collections.Generic;
using Indexed.Abstractions;
using Indexed.Targets;

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

    /// <summary>
    /// Optional directory-target roots selected via repeated <c>--root</c>.
    /// One root means <c>directory-tree</c>; multiple labeled roots mean
    /// <c>directory-set</c>.
    /// </summary>
    public IReadOnlyList<TargetRootSpec>? Roots { get; init; }

    /// <summary>
    /// Index-time exclude globs forwarded to the daemon on launch.
    /// Matching files are skipped during full-scan indexing.
    /// </summary>
    public IReadOnlyList<string>? IndexExcludeGlob { get; init; }

    /// <summary>
    /// Index-time include globs forwarded to the daemon on launch.
    /// Null or empty means all target files remain in scope.
    /// </summary>
    public IReadOnlyList<string>? IndexIncludeGlob { get; init; }

    /// <summary>
    /// Optional daemon launch override for the maximum indexable file size.
    /// </summary>
    public long? MaxIndexableFileBytes { get; init; }

    /// <summary>
    /// Controls automatic daemon update sources. Manual mode keeps the index
    /// fixed except for initial scans and explicit rescans.
    /// </summary>
    public IndexUpdateMode UpdateMode { get; init; } = IndexUpdateMode.Live;

    /// <summary>
    /// When <c>true</c>, the daemon will <em>not</em> apply the built-in
    /// default exclude list (lockfiles, minified bundles, generated C#).
    /// Maps to <c>--no-default-excludes</c> on the CLI.
    /// </summary>
    public bool NoDefaultExcludes { get; init; }

    /// <summary>
    /// When <c>true</c>, the daemon will not apply the directory-mode default
    /// exclude list. Only meaningful when <see cref="Roots"/> is non-empty.
    /// </summary>
    public bool NoDefaultDirectoryExcludes { get; init; }

    /// <summary>
    /// Optional override forwarded to the daemon at launch to change its
    /// idle-exit window. Maps to <c>--idle-timeout-seconds</c> on the daemon.
    /// </summary>
    /// <remarks>
    /// Only applies when this CLI invocation launches a new daemon. If an
    /// existing daemon is adopted via <c>daemon.json</c>, the already-running
    /// daemon's idle timeout remains in effect.
    /// </remarks>
    public int? IdleTimeoutSeconds { get; init; }

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

    /// <summary>List daemon descriptors found under the Indexed app-data root.</summary>
    Daemons,
}
