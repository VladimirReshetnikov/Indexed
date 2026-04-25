using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Service;
using Indexed.Targets;

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
    /// Start a detached daemon process for <paramref name="targetSpec"/> and
    /// return once either the port-file is observable or the timeout expires.
    /// </summary>
    /// <param name="targetSpec">Canonical target specification to serve.</param>
    /// <param name="daemonJsonPath">Path to watch for the bound port-file.</param>
    /// <param name="timeout">How long to wait for the daemon to become ready.</param>
    /// <param name="appData">Optional override of <c>%LOCALAPPDATA%\Indexed</c>.</param>
    /// <param name="idleTimeoutSeconds">
    /// Optional daemon idle-exit override (in seconds) forwarded via
    /// <c>--idle-timeout-seconds</c>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The observed <see cref="DaemonInfo"/>, or <c>null</c> on timeout.</returns>
    public static async Task<DaemonInfo?> LaunchAsync(
        TargetSpec targetSpec,
        string daemonJsonPath,
        TimeSpan timeout,
        string? appData = null,
        int? idleTimeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        if (targetSpec is null) throw new ArgumentNullException(nameof(targetSpec));

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
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };
        AddTargetArguments(psi.ArgumentList, targetSpec);
        if (!string.IsNullOrEmpty(appData))
        {
            psi.ArgumentList.Add("--app-data");
            psi.ArgumentList.Add(appData);
        }
        if (idleTimeoutSeconds is not null)
        {
            psi.ArgumentList.Add("--idle-timeout-seconds");
            psi.ArgumentList.Add(idleTimeoutSeconds.Value.ToString());
        }
        if (targetSpec.IndexIncludeGlobs is not null)
        {
            foreach (var glob in targetSpec.IndexIncludeGlobs)
            {
                psi.ArgumentList.Add("--include-index");
                psi.ArgumentList.Add(glob);
            }
        }
        if (targetSpec.IndexExcludeGlobs is not null)
        {
            foreach (var glob in targetSpec.IndexExcludeGlobs)
            {
                psi.ArgumentList.Add("--exclude-index");
                psi.ArgumentList.Add(glob);
            }
        }
        if (!targetSpec.UseDefaultIndexExcludes)
        {
            psi.ArgumentList.Add("--no-default-excludes");
        }
        if (targetSpec.Kind is TargetKind.DirectoryTree or TargetKind.DirectorySet
            && !targetSpec.UseDefaultDirectoryExcludes)
        {
            psi.ArgumentList.Add("--no-default-directory-excludes");
        }
        if (targetSpec.MaxIndexableFileBytes != Indexed.Targets.TargetIndexDefaults.DefaultMaxIndexableFileBytes)
        {
            psi.ArgumentList.Add("--max-indexable-file-bytes");
            psi.ArgumentList.Add(targetSpec.MaxIndexableFileBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (targetSpec.UpdateMode != IndexUpdateMode.Live)
        {
            psi.ArgumentList.Add("--index-updates");
            psi.ArgumentList.Add(targetSpec.UpdateMode switch
            {
                IndexUpdateMode.Manual => "manual",
                _ => throw new InvalidOperationException($"unsupported update mode '{targetSpec.UpdateMode}'"),
            });
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

    private static void AddTargetArguments(Collection<string> argumentList, TargetSpec spec)
    {
        switch (spec.Kind)
        {
            case TargetKind.GitRepository:
                argumentList.Add("--repo-root");
                argumentList.Add(spec.Roots[0].Path);
                break;

            case TargetKind.DirectoryTree:
                argumentList.Add("--root");
                argumentList.Add(TargetRootArgumentParser.Format(spec.Roots[0], requireLabel: false));
                break;

            case TargetKind.DirectorySet:
                foreach (var root in spec.Roots)
                {
                    argumentList.Add("--root");
                    argumentList.Add(TargetRootArgumentParser.Format(root, requireLabel: true));
                }
                break;

            default:
                throw new InvalidOperationException($"unsupported target kind '{spec.Kind}'");
        }
    }
}
