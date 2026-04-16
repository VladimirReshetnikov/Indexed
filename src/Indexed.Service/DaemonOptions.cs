using System;
using System.Collections.Generic;

namespace Indexed.Service;

/// <summary>
/// Configuration for a single <see cref="DaemonHost"/> instance.
/// </summary>
/// <remarks>
/// <para>
/// Options are immutable once passed to the host. Tests override the defaults
/// to exercise edge cases — a short <see cref="IdleTimeout"/> to observe
/// auto-exit, a custom <see cref="AppDataBase"/> to isolate the per-repo
/// directory under the test's temp root, a <see cref="BackendOverride"/> to
/// short-circuit the real SQLite index path.
/// </para>
/// </remarks>
public sealed record DaemonOptions
{
    /// <summary>Repository working-tree root the daemon will serve.</summary>
    public required string RepoRoot { get; init; }

    /// <summary>
    /// Override for the <c>%APPDATA%\Indexed</c> parent of the per-repo
    /// directory. <c>null</c> uses the real roaming profile.
    /// </summary>
    public string? AppDataBase { get; init; }

    /// <summary>
    /// Duration of no-request + no-index-activity after which the daemon
    /// exits. Default 30 minutes per proposal §9.2.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Whether to acquire the single-instance named mutex at startup. Tests
    /// that run several daemons side-by-side in-process set this to
    /// <c>false</c>.
    /// </summary>
    public bool UseSingletonMutex { get; init; } = true;

    /// <summary>
    /// Optional backend injection for tests. When <c>null</c>, the host
    /// opens the per-repo <c>index.db</c>, runs a full scan if empty, and
    /// constructs a <see cref="SqliteSearchBackend"/>.
    /// </summary>
    public ISearchBackend? BackendOverride { get; init; }

    /// <summary>
    /// When <c>true</c> (default), <see cref="DaemonHost.StartAsync"/> runs a
    /// <see cref="Indexed.Core.FullScanIndexer"/> before returning if the
    /// index is empty. Tests that want to observe a cold daemon without
    /// waiting on indexing set this to <c>false</c> and control the index
    /// contents directly.
    /// </summary>
    public bool RunInitialScan { get; init; } = true;

    /// <summary>
    /// Gitignore-style globs applied to repository-relative POSIX paths
    /// during full-scan indexing. Matching files are skipped before reading.
    /// Useful for large vendored trees like <c>lib/**</c> that inflate the
    /// FTS5 trigram index without providing search value.
    /// </summary>
    /// <example>
    /// <code>
    /// IndexExcludeGlobs = new[] { "lib/**", "vendor/**" }
    /// </code>
    /// </example>
    public IReadOnlyList<string>? IndexExcludeGlobs { get; init; }

    /// <summary>
    /// When <c>true</c> (default), the daemon prepends
    /// <see cref="Indexed.Core.ExcludeFilter.DefaultBinaryAdjacentGlobs"/> to
    /// <see cref="IndexExcludeGlobs"/> before passing the combined list to the
    /// indexers and watcher. Set to <c>false</c> to index lockfiles, minified
    /// bundles, and generated files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default list covers JS/TS lockfiles, minified bundles, source maps,
    /// ecosystem lockfiles (Cargo, Gemfile, …), and generated C# files. These
    /// files expand the FTS5 trigram index significantly while providing little
    /// search value. Override with <c>--no-default-excludes</c> on the CLI.
    /// </para>
    /// </remarks>
    public bool UseDefaultIndexExcludes { get; init; } = true;

    /// <summary>
    /// Interval between <see cref="Indexed.Core.IndexOptimizer"/> ticks.
    /// Each tick runs a bounded FTS5 segment merge (see
    /// <see cref="OptimizerPageBudget"/>) if there were batch commits since
    /// the previous tick. Default 15 minutes.
    /// </summary>
    public TimeSpan OptimizerInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// FTS5 page budget per optimizer tick. Caps how much work a single
    /// merge can perform, bounding the time the writer lock is held. Default
    /// 512 pages — typically completes in tens of milliseconds on SSDs.
    /// </summary>
    public int OptimizerPageBudget { get; init; } = 512;

    /// <summary>
    /// Version string reported by <c>/status</c> and stamped in
    /// <c>daemon.json</c>. Defaults to the assembly's informational version.
    /// </summary>
    public string DaemonVersion { get; init; } =
        typeof(DaemonOptions).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is [System.Reflection.AssemblyInformationalVersionAttribute ia, ..]
            ? ia.InformationalVersion
            : "0.1.0-s1";
}
