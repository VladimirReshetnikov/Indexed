using System;
using System.IO;

namespace Indexed.Service;

/// <summary>
/// Resolves the per-target paths the daemon reads and writes.
/// </summary>
/// <remarks>
/// <para>
/// Root layout (Windows):
/// </para>
/// <code>
/// %LOCALAPPDATA%\Indexed\&lt;targetId&gt;\
///     daemon.json
///     logs\indexed-YYYYMMDD.log
///     index.db            (added in S2)
/// </code>
/// <para>
/// <c>%LOCALAPPDATA%</c> (<see cref="Environment.SpecialFolder.LocalApplicationData"/>)
/// is deliberate: <c>index.db</c> is a potentially large, machine-specific,
/// reconstructible-from-source derived artifact, and must not roam between
/// devices. Roaming it (via <c>%APPDATA%</c>) would inflate sync traffic and
/// race with per-machine indexer state.
/// </para>
/// <para>
/// Tests override the base path by passing <paramref name="baseDirectory"/>
/// to <see cref="ForTarget"/>. Production code passes the empty string (default)
/// so the class resolves <c>%LOCALAPPDATA%\Indexed</c> from the environment.
/// </para>
/// </remarks>
public sealed class DaemonPaths
{
    private DaemonPaths(string rootDirectory)
    {
        RootDirectory = rootDirectory;
        DaemonJsonPath = Path.Combine(rootDirectory, "daemon.json");
        LogsDirectory = Path.Combine(rootDirectory, "logs");
        IndexDbPath = Path.Combine(rootDirectory, "index.db");
    }

    /// <summary>
    /// Per-target root (<c>%LOCALAPPDATA%\Indexed\&lt;targetId&gt;</c>). Created by
    /// <see cref="EnsureCreated"/>.
    /// </summary>
    public string RootDirectory { get; }

    /// <summary>Path to the discoverable <c>daemon.json</c> file.</summary>
    public string DaemonJsonPath { get; }

    /// <summary>Directory for rotating log files.</summary>
    public string LogsDirectory { get; }

    /// <summary>
    /// Path to the SQLite+FTS5 index file (<c>index.db</c>). The file is
    /// created lazily on first open; sidecar WAL/SHM files live alongside it.
    /// </summary>
    public string IndexDbPath { get; }

    /// <summary>
    /// Resolve paths for the target identified by <paramref name="targetId"/>.
    /// </summary>
    /// <param name="targetId">
    /// 12-character id returned by target identity computation.
    /// </param>
    /// <param name="baseDirectory">
    /// Override the parent of the per-repo directory. Pass <c>null</c> or
    /// empty to use <c>%LOCALAPPDATA%\Indexed</c>; pass a temp directory in tests.
    /// </param>
    public static DaemonPaths ForTarget(string targetId, string? baseDirectory = null)
    {
        if (string.IsNullOrEmpty(targetId))
            throw new ArgumentException("targetId must be non-empty", nameof(targetId));

        var parent = string.IsNullOrEmpty(baseDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Indexed")
            : baseDirectory;

        return new DaemonPaths(Path.Combine(parent, targetId));
    }

    public static DaemonPaths ForRepo(string repoId, string? baseDirectory = null)
    {
        return ForTarget(repoId, baseDirectory);
    }

    /// <summary>
    /// Ensure <see cref="RootDirectory"/> and <see cref="LogsDirectory"/>
    /// exist. Safe to call repeatedly.
    /// </summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
