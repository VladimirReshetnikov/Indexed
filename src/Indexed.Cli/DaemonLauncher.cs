using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Service;

namespace Indexed.Cli;

/// <summary>
/// Locates the <c>Indexed.Service</c> executable and spawns a detached
/// daemon process for the requested repository root.
/// </summary>
/// <remarks>
/// <para>
/// Resolution order:
/// </para>
/// <list type="number">
///   <item><description><c>INDEXED_SERVICE_EXE</c> environment variable — explicit override.</description></item>
///   <item><description>Sibling file <c>Indexed.Service.exe</c> next to the CLI binary.</description></item>
///   <item><description>Sibling directory walk under the CLI's build tree (for debug-build runs).</description></item>
/// </list>
/// <para>
/// The spawned process runs fully detached: no console window, no stdio
/// inheritance, no wait. The CLI returns as soon as the daemon's
/// <c>daemon.json</c> is observable, which the caller polls via
/// <see cref="WaitForDaemonAsync"/>.
/// </para>
/// </remarks>
internal static class DaemonLauncher
{
    /// <summary>
    /// Start a detached daemon process for <paramref name="repoRoot"/> and
    /// return once either the port-file is observable or the timeout expires.
    /// </summary>
    /// <param name="repoRoot">Working-tree root to index.</param>
    /// <param name="daemonJsonPath">Path to watch for the bound port-file.</param>
    /// <param name="timeout">How long to wait for the daemon to become ready.</param>
    /// <param name="appData">Optional override of <c>%APPDATA%\Indexed</c>.</param>
    /// <param name="indexExcludeGlobs">Additional index-time exclude globs forwarded on launch.</param>
    /// <param name="noDefaultExcludes">
    /// When <c>true</c>, passes <c>--no-default-excludes</c> so the daemon
    /// does not apply the built-in default exclude list.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The observed <see cref="DaemonInfo"/>, or <c>null</c> on timeout.</returns>
    public static async Task<DaemonInfo?> LaunchAsync(
        string repoRoot,
        string daemonJsonPath,
        TimeSpan timeout,
        string? appData = null,
        IReadOnlyList<string>? indexExcludeGlobs = null,
        bool noDefaultExcludes = false,
        CancellationToken cancellationToken = default)
    {
        var exe = ResolveServiceExecutable()
            ?? throw new FileNotFoundException(
                "Could not locate Indexed.Service.exe. Set INDEXED_SERVICE_EXE or run `dotnet build`.");

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            WorkingDirectory = repoRoot,
        };
        psi.ArgumentList.Add(repoRoot);
        if (!string.IsNullOrEmpty(appData))
        {
            psi.ArgumentList.Add("--app-data");
            psi.ArgumentList.Add(appData);
        }
        if (indexExcludeGlobs is not null)
        {
            foreach (var glob in indexExcludeGlobs)
            {
                psi.ArgumentList.Add("--exclude-index");
                psi.ArgumentList.Add(glob);
            }
        }
        if (noDefaultExcludes)
        {
            psi.ArgumentList.Add("--no-default-excludes");
        }

        using var _ = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null");

        return await WaitForDaemonAsync(daemonJsonPath, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Poll for <paramref name="daemonJsonPath"/> to appear and parse cleanly.
    /// </summary>
    public static async Task<DaemonInfo?> WaitForDaemonAsync(
        string daemonJsonPath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = DaemonInfo.TryRead(daemonJsonPath);
            if (info is not null) return info;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    internal static string? ResolveServiceExecutable()
    {
        var envOverride = Environment.GetEnvironmentVariable("INDEXED_SERVICE_EXE");
        if (!string.IsNullOrEmpty(envOverride) && File.Exists(envOverride))
            return envOverride;

        var baseDir = AppContext.BaseDirectory;
        var exeName = OperatingSystem.IsWindows() ? "Indexed.Service.exe" : "Indexed.Service";

        // 1. Side-by-side in the CLI's output directory (typical publish layout).
        var sideBySide = Path.Combine(baseDir, exeName);
        if (File.Exists(sideBySide)) return sideBySide;

        // 2. Debug/build-tree: walk up to the solution-level bin tree and look
        //    for Indexed.Service\bin\<config>\<tfm>\Indexed.Service.exe.
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            // bin\<config>\<tfm>\  → climb three to get the CLI project dir,
            // then hop to the sibling project.
            var candidate = Path.Combine(
                dir.FullName, "..", "Indexed.Service",
                "bin", ConfigurationSegment(baseDir), TfmSegment(baseDir), exeName);
            candidate = Path.GetFullPath(candidate);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static string ConfigurationSegment(string baseDir)
    {
        // baseDir looks like ...\Indexed.Cli\bin\<config>\<tfm>\
        var parts = baseDir.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (parts[i].Equals("bin", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
                return parts[i + 1];
        }
        return "Release";
    }

    private static string TfmSegment(string baseDir)
    {
        var parts = baseDir.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "net10.0-windows" : parts[^1];
    }
}
