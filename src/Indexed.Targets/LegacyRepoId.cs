using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Indexed.Targets;

public static class LegacyRepoId
{
    public static string Compute(string repoRoot, string firstCommitSha)
    {
        if (repoRoot is null) throw new ArgumentNullException(nameof(repoRoot));
        if (firstCommitSha is null) throw new ArgumentNullException(nameof(firstCommitSha));

        var full = Path.GetFullPath(repoRoot);
        var input = Encoding.UTF8.GetBytes(full + "\0" + firstCommitSha);
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);
        return Convert.ToHexString(hash[..6]).ToLowerInvariant();
    }
}
