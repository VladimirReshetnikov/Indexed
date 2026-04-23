using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Indexed.Targets;

namespace Indexed.Service;

/// <summary>
/// Discovery record written to <c>daemon.json</c> when the daemon binds a port.
/// </summary>
/// <remarks>
/// <para>
/// Schema is stable across daemon versions — older CLIs must be able to read
/// this file from a newer daemon to at least discover the port. Fields may be
/// added in minor releases; never renamed or removed.
/// </para>
/// <para>
/// <see cref="ShutdownToken"/> authenticates destructive endpoints
/// (<c>POST /shutdown</c> today; <c>POST /rescan</c> when it gets side
/// effects). Only processes that can read the file — enforced by Windows
/// filesystem ACLs granting the owning user exclusive access to
/// <c>%LOCALAPPDATA%</c> — obtain the token. This is stronger than a
/// loopback-only check, which does not distinguish the daemon's owner from
/// a different local user on multi-user workstations.
/// </para>
/// </remarks>
/// <param name="Port">TCP port the daemon is listening on (loopback only).</param>
/// <param name="Pid">OS process id of the daemon.</param>
/// <param name="TargetKind">Kind of the served target.</param>
/// <param name="TargetId">Deterministic target identity for daemon discovery.</param>
/// <param name="Roots">Validated roots that belong to the served target.</param>
/// <param name="PrimaryRoot">Primary root for the target.</param>
/// <param name="RepoRoot">Repository working-tree root the daemon serves, when applicable.</param>
/// <param name="RepoId">12-character repo id (<see cref="Service.RepoId"/>), when applicable.</param>
/// <param name="StartedAt">UTC time the daemon began listening.</param>
/// <param name="DaemonVersion">Informational version string.</param>
/// <param name="ShutdownToken">
/// Opaque base64 token required in the <c>X-Indexed-Shutdown-Token</c> header
/// to invoke destructive endpoints. Rotated per process lifetime.
/// </param>
public sealed record DaemonInfo(
    int Port,
    int Pid,
    TargetKind TargetKind,
    string TargetId,
    IReadOnlyList<TargetRoot> Roots,
    TargetRoot PrimaryRoot,
    string? RepoRoot,
    string? RepoId,
    DateTimeOffset StartedAt,
    string DaemonVersion,
    string ShutdownToken)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Generate a cryptographically random 32-byte shutdown token as base64.
    /// </summary>
    public static string NewShutdownToken()
    {
        Span<byte> buf = stackalloc byte[32];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToBase64String(buf);
    }

    /// <summary>
    /// Atomically write this record to <paramref name="path"/> via temp-file +
    /// rename. Overwrites any existing <c>daemon.json</c>.
    /// </summary>
    /// <remarks>
    /// The atomic rename guarantees that a concurrent CLI either sees the
    /// complete previous record or the complete new one — never a truncated
    /// intermediate.
    /// </remarks>
    public void WriteAtomic(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(this, SerializerOptions));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Read a <see cref="DaemonInfo"/> from <paramref name="path"/>, or
    /// return <c>null</c> when the file is absent or unparseable.
    /// </summary>
    /// <remarks>
    /// Never throws for IO or JSON errors: callers use this during
    /// auto-start probing, where "file doesn't exist" and "file is stale
    /// garbage" produce the same recovery (spawn a new daemon).
    /// </remarks>
    public static DaemonInfo? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            return JsonSerializer.Deserialize<DaemonInfo>(bytes, SerializerOptions);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Best-effort delete of <paramref name="path"/>. Never throws for
    /// filesystem or path-validation errors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The catch list is intentionally narrow: only the exception types
    /// <see cref="File.Delete(string)"/> can raise. A blanket
    /// <c>catch</c> would swallow programmer errors
    /// (<see cref="NullReferenceException"/>,
    /// <see cref="ObjectDisposedException"/> on injected wrappers, etc.)
    /// that the daemon should fail fast on rather than hide behind a
    /// "best-effort cleanup" comment.
    /// </para>
    /// </remarks>
    public static void TryDelete(string path)
    {
        // Catch order: IOException is the base for DirectoryNotFoundException
        // and PathTooLongException, so the single IOException handler below
        // subsumes both. Listing them separately would be a build error.
        try { File.Delete(path); }
        catch (UnauthorizedAccessException) { /* read-only file or ACL */ }
        catch (IOException) { /* locked, in use, parent missing, path too long */ }
        catch (ArgumentException) { /* invalid path characters */ }
        catch (NotSupportedException) { /* path format not supported */ }
    }
}
