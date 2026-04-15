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
/// </remarks>
internal static class GitProcess
{
    /// <summary>
    /// Executable name resolved on <c>PATH</c>. Set to a full path for tests.
    /// </summary>
    internal static string Executable { get; set; } = "git";

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
    public static byte[] RunBytes(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        byte[]? stdin = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        psi.Environment["LANG"] = "C.UTF-8";
        psi.Environment["LC_ALL"] = "C.UTF-8";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

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
            process.WaitForExit();
            stdoutTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new GitProcessException(Executable, arguments, process.ExitCode, stderr.Trim());

        return stdoutBuffer.ToArray();
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
