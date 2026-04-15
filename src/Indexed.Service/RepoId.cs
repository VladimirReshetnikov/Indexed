using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Indexed.Service;

/// <summary>
/// Derives the 12-character repository identifier used throughout Indexed.
/// </summary>
/// <remarks>
/// <para>
/// Definition: <c>SHA1(abspath + "\0" + firstCommitSha)</c> truncated to the
/// first 12 hex characters (six bytes). This formula is deliberately
/// <em>not</em> stable across worktree moves that also change the first
/// commit — a git graft or rebase that rewrites the root commit produces a
/// new id. That is intentional: the index state is meaningful only against a
/// specific history, and a rewritten root is effectively a new repository.
/// </para>
/// <para>
/// Uses: single-instance mutex name, <c>%APPDATA%\Indexed\&lt;repoId&gt;\</c>
/// directory segment, <see cref="Indexed.Abstractions.StatusResponse.RepoId"/>
/// field.
/// </para>
/// </remarks>
public static class RepoId
{
    /// <summary>
    /// Compute the 12-character repository id for the given root and
    /// first-commit SHA.
    /// </summary>
    /// <param name="repoRoot">
    /// Absolute path to the repository working-tree root. Normalized with
    /// <see cref="Path.GetFullPath(string)"/> before hashing so case and
    /// separator differences collapse.
    /// </param>
    /// <param name="firstCommitSha">
    /// Full 40-character SHA-1 of the root commit (<c>rev-list --max-parents=0
    /// HEAD</c>). Pass the empty string for a repository with no commits; the
    /// resulting id is still stable as long as the path stays the same.
    /// </param>
    public static string Compute(string repoRoot, string firstCommitSha)
    {
        if (repoRoot is null) throw new ArgumentNullException(nameof(repoRoot));
        if (firstCommitSha is null) throw new ArgumentNullException(nameof(firstCommitSha));

        var full = Path.GetFullPath(repoRoot);
        var input = Encoding.UTF8.GetBytes(full + "\0" + firstCommitSha);
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);

        // First 6 bytes = 12 hex characters — enough for ~2^47 distinct
        // repo/commit pairs before meaningful collision risk.
        return Convert.ToHexString(hash[..6]).ToLowerInvariant();
    }
}
