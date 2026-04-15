using System;
using System.IO;

namespace Indexed.Service;

/// <summary>
/// Resolves the per-repository paths the daemon reads and writes.
/// </summary>
/// <remarks>
/// <para>
/// Root layout (Windows):
/// </para>
/// <code>
/// %APPDATA%\Indexed\&lt;repoId&gt;\
///     daemon.json
///     logs\indexed-YYYYMMDD.log
///     index.db            (added in S2)
/// </code>
/// <para>
/// Tests override the base path by passing <paramref name="baseDirectory"/>
/// to <see cref="ForRepo"/>. Production code passes the empty string (default)
/// so the class resolves <c>%APPDATA%\Indexed</c> from the environment.
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
    /// Per-repo root (<c>%APPDATA%\Indexed\&lt;repoId&gt;</c>). Created by
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
    /// Resolve paths for the repo identified by <paramref name="repoId"/>.
    /// </summary>
    /// <param name="repoId">
    /// 12-character id returned by <see cref="RepoId.Compute"/>.
    /// </param>
    /// <param name="baseDirectory">
    /// Override the parent of the per-repo directory. Pass <c>null</c> or
    /// empty to use <c>%APPDATA%\Indexed</c>; pass a temp directory in tests.
    /// </param>
    public static DaemonPaths ForRepo(string repoId, string? baseDirectory = null)
    {
        if (string.IsNullOrEmpty(repoId))
            throw new ArgumentException("repoId must be non-empty", nameof(repoId));

        var parent = string.IsNullOrEmpty(baseDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Indexed")
            : baseDirectory;

        return new DaemonPaths(Path.Combine(parent, repoId));
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
