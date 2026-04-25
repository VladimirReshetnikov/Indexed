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
    /// <summary>
    /// Legacy git-mode compatibility root. When <see cref="TargetSelection"/>
    /// is absent, the daemon uses this path to open a git target.
    /// </summary>
    public string? RepoRoot { get; init; }

    /// <summary>
    /// Optional target-selection object for directory-tree, directory-set, or
    /// future non-git modes. When absent, <see cref="RepoRoot"/> is treated as
    /// the git compatibility path.
    /// </summary>
    public TargetSelection? TargetSelection { get; init; }

    /// <summary>
    /// Override for the <c>%LOCALAPPDATA%\Indexed</c> parent of the per-repo
    /// directory. <c>null</c> uses the real local profile.
    /// </summary>
    /// <remarks>
    /// The option is named <see cref="AppDataBase"/> for API compatibility;
    /// the default is resolved from
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/>
    /// (non-roaming) because <c>index.db</c> is a large, machine-specific
    /// derived artifact.
    /// </remarks>
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
    /// opens the per-target <c>index.db</c>, runs a full scan if empty, and
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
    /// Gitignore-style globs applied to logical paths during full-scan
    /// indexing. Matching files are skipped before reading.
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
    /// Optional gitignore-style include globs applied before excludes during
    /// indexing. When null or empty, every target file is in scope.
    /// </summary>
    public IReadOnlyList<string>? IndexIncludeGlobs { get; init; }

    /// <summary>
    /// Maximum file size accepted into the raw-content index.
    /// </summary>
    public long MaxIndexableFileBytes { get; init; } = Indexed.Targets.TargetIndexDefaults.DefaultMaxIndexableFileBytes;

    /// <summary>
    /// Controls whether automatic filesystem/revision/reconciliation sources
    /// are started. Manual mode still allows the initial scan and explicit
    /// <c>POST /rescan</c> work.
    /// </summary>
    public Indexed.Targets.IndexUpdateMode UpdateMode { get; init; } = Indexed.Targets.IndexUpdateMode.Live;

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
    /// Whether directory-mode default excludes participate in target identity.
    /// Git mode leaves this disabled.
    /// </summary>
    public bool UseDefaultDirectoryExcludes { get; init; }

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
