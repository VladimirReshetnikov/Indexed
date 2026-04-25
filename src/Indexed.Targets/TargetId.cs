using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Indexed.Targets;

public static class TargetId
{
    public static string Compute(TargetSpec spec, string? legacyRepoId = null)
    {
        if (spec is null) throw new ArgumentNullException(nameof(spec));

        if (!string.IsNullOrEmpty(legacyRepoId) && TargetSpecFactory.IsLegacyCompatibleGitDefault(spec))
            return legacyRepoId;

        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);

        WriteField(writer, "indexed-target-v1");
        WriteField(writer, spec.Kind.ToString());
        WriteField(writer, spec.Roots.Count.ToString(CultureInfo.InvariantCulture));

        foreach (var root in spec.Roots)
        {
            WriteField(writer, root.Name ?? string.Empty);
            WriteField(writer, NormalizeRootForIdentity(root.Path));
        }

        WriteField(writer, spec.UseDefaultIndexExcludes ? "1" : "0");
        WriteField(writer, spec.UseDefaultDirectoryExcludes ? "1" : "0");
        WriteField(writer, spec.MaxIndexableFileBytes.ToString(CultureInfo.InvariantCulture));

        var includeGlobs = spec.IndexIncludeGlobs ?? Array.Empty<string>();
        WriteField(writer, includeGlobs.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var glob in includeGlobs)
            WriteField(writer, glob);

        var excludeGlobs = spec.IndexExcludeGlobs ?? Array.Empty<string>();
        WriteField(writer, excludeGlobs.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var glob in excludeGlobs)
            WriteField(writer, glob);

        writer.Flush();

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), hash);
        return Convert.ToHexString(hash[..6]).ToLowerInvariant();
    }

    private static string NormalizeRootForIdentity(string path)
    {
        var normalized = Path.GetFullPath(path);
        return OperatingSystem.IsWindows()
            ? normalized.ToUpperInvariant()
            : normalized;
    }

    private static void WriteField(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.UTF8.GetBytes(value));
        writer.Write((byte)0);
    }
}
