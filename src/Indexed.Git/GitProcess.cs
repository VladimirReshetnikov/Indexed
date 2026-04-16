using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Indexed.Git;

/// <summary>
/// Thin wrapper around <see cref="Process"/> invocations of <c>git.exe</c>.
/// </summary>
/// <remarks>
/// <para>
/// Centralizes process plumbing (working directory, environment, encoding,
/// exit-code handling) so <see cref="GitRepository"/> can read one-shot git
/// output as a string or a byte array without repeating boilerplate.
/// </para>
/// <para>
/// Encoding contract: git emits paths as raw bytes (configurable via
/// <c>core.quotePath</c>) and metadata as UTF-8. Callers that need raw path
/// bytes for filesystems with unusual encodings use the byte-oriented
/// overload; callers that expect UTF-8 text use the string overload. Both
/// write <c>LANG</c>/<c>LC_ALL</c>=<c>C.UTF-8</c> to the subprocess to get
/// stable English output.
/// </para>
/// <para>
/// Failure behavior: non-zero exit produces a <see cref="GitProcessException"/>
/// whose message carries the trimmed stderr text. Stderr is read concurrently
/// with stdout to avoid deadlocking on pipe-full conditions.
/// </para>
/// <para>
/// Retry: when git exits with a message containing <c>index.lock</c>
/// (another git process holds the lock), the call is retried up to
/// <see cref="MaxLockRetries"/> times with exponential back-off. This avoids
/// transient failures when the daemon's git operations collide with IDE
/// auto-fetch, interactive rebase, or user-initiated commits.
/// </para>
/// </remarks>
internal static class GitProcess
{
    /// <summary>
    /// Executable name resolved on <c>PATH</c>. Set to a full path for tests.
    /// </summary>
    internal static string Executable { get; set; } = "git";

    /// <summary>Maximum number of retries on <c>index.lock</c> contention.</summary>
    internal static int MaxLockRetries { get; set; } = 4;

    /// <summary>
    /// Maximum wall-clock time to wait for a git subprocess to exit.
    /// Prevents the daemon from hanging indefinitely if git stalls.
    /// </summary>
    internal static TimeSpan ProcessTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Environment variables that must be removed from the git subprocess to
    /// prevent accidental inheritance (e.g., running inside a git hook).
    /// </summary>
    private static readonly string[] PoisonousEnvVars =
    {
        "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE",
        "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_CEILING_DIRECTORIES", "GIT_COMMON_DIR",
    };

    /// <summary>
    /// Run <c>git</c> with the given arguments and return stdout as a UTF-8 string.
    /// </summary>
    /// <remarks>
    /// Preconditions: <paramref name="workingDirectory"/> exists. Optional
    /// <paramref name="stdin"/> bytes are written and the stream closed before
    /// stdout is drained.
    /// </remarks>
    public static string RunText(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        byte[]? stdin = null,
        CancellationToken cancellationToken = default)
    {
        var bytes = RunBytes(workingDirectory, arguments, stdin, cancellationToken);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Run <c>git</c> and return stdout as a raw byte array. Use for
    /// <c>-z</c>-terminated output that may contain non-UTF-8 path bytes.
    /// </summary>
    /// <remarks>
    /// Retries automatically on <c>index.lock</c> contention with
    /// exponential back-off (100 ms, 200 ms, 400 ms, 800 ms).
    /// </remarks>
    public static byte[] RunBytes(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        byte[]? stdin = null,
        CancellationToken cancellationToken = default)
    {
        var attempts = 0;
        while (true)
        {
            try
            {
                return RunBytesCore(workingDirectory, arguments, stdin, cancellationToken);
            }
            catch (GitProcessException ex) when (IsLockContention(ex) && attempts < MaxLockRetries)
            {
                attempts++;
                var delay = TimeSpan.FromMilliseconds(100 * (1 << (attempts - 1)));
                // Wait cancellably: if the caller's token fires during the
                // back-off (shutdown, request deadline) we abandon the retry
                // chain with OperationCanceledException instead of sleeping
                // the full delay. Thread.Sleep would have held the pool
                // thread hostage until the natural wake.
                if (cancellationToken.WaitHandle.WaitOne(delay))
                    cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private static byte[] RunBytesCore(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        byte[]? stdin,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        // Force stable UTF-8 English output.
        psi.Environment["LANG"] = "C.UTF-8";
        psi.Environment["LC_ALL"] = "C.UTF-8";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        // Remove environment variables that could misdirect the subprocess
        // to a different repository (e.g., if the daemon is launched from
        // inside a git hook or a worktree).
        foreach (var name in PoisonousEnvVars)
            psi.Environment.Remove(name);

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                throw new GitProcessException(Executable, arguments, -1, "failed to start git process");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new GitProcessException(Executable, arguments, -1, ex.Message);
        }

        // Drain stdout as bytes and stderr as text concurrently — if either
        // pipe fills, the child blocks and we deadlock.
        using var stdoutBuffer = new MemoryStream();
        var stdoutTask = Task.Run(
            () => process.StandardOutput.BaseStream.CopyToAsync(stdoutBuffer, cancellationToken),
            cancellationToken);
        var stderrTask = Task.Run(
            async () => await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken);

        if (stdin is not null)
        {
            process.StandardInput.BaseStream.Write(stdin, 0, stdin.Length);
            process.StandardInput.Close();
        }

        try
        {
            // Bounded wait — do not block indefinitely if git hangs.
            if (!process.WaitForExit((int)ProcessTimeout.TotalMilliseconds))
            {
                TryKill(process);
                try { stderrTask.GetAwaiter().GetResult(); } catch { }
                throw new GitProcessException(Executable, arguments, -1,
                    $"git process did not exit within {ProcessTimeout.TotalSeconds}s — killed");
            }
            stdoutTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            try { stderrTask.GetAwaiter().GetResult(); } catch { }
            throw;
        }

        var stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new GitProcessException(Executable, arguments, process.ExitCode, stderr.Trim());

        return stdoutBuffer.ToArray();
    }

    /// <summary>
    /// Detect git exit caused by <c>.git/index.lock</c> held by another process.
    /// </summary>
    private static bool IsLockContention(GitProcessException ex)
    {
        return ex.Message.Contains("index.lock", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Unable to create", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* best-effort */ }
    }
}

/// <summary>
/// Raised when a <see cref="GitProcess"/> invocation returns a non-zero exit
/// code or fails to launch.
/// </summary>
/// <remarks>
/// Carries the command that failed in <see cref="Arguments"/> and the trimmed
/// stderr text in <see cref="Message"/> so the caller can surface a useful
/// diagnostic. <see cref="ExitCode"/> is <c>-1</c> when the process could not
/// be started at all (missing executable, permission denied).
/// </remarks>
public sealed class GitProcessException : Exception
{
    /// <summary>Executable path that was invoked.</summary>
    public string Executable { get; }

    /// <summary>Argument list passed to the process.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Process exit code, or <c>-1</c> if the process never started.</summary>
    public int ExitCode { get; }

    /// <summary>Construct with the failing invocation details.</summary>
    public GitProcessException(
        string executable,
        IReadOnlyList<string> arguments,
        int exitCode,
        string stderr)
        : base($"git {string.Join(' ', arguments)} exited with {exitCode}: {stderr}")
    {
        Executable = executable;
        Arguments = arguments;
        ExitCode = exitCode;
    }
}
