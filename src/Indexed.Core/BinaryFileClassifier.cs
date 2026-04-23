using System;
using System.IO;

namespace Indexed.Core;

/// <summary>
/// Target-neutral binary-file heuristic used by the core indexers.
/// </summary>
/// <remarks>
/// <para>
/// The git adapter still exposes explicit binary overrides through
/// <c>.gitattributes</c>, but the last-resort content probe belongs in the
/// core layer because directory targets need the same decision policy.
/// </para>
/// <para>
/// Files are treated as binary when they are missing, unreadable, directories,
/// exceed <see cref="IndexLimits.MaxIndexableFileBytes"/>, or contain a NUL
/// byte in the first 8 KiB.
/// </para>
/// </remarks>
public static class BinaryFileClassifier
{
    private const int MaxBinaryPeekBytes = 8192;

    public static bool IsLikelyBinary(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return true;

        FileInfo info;
        try
        {
            info = new FileInfo(absolutePath);
            if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0)
                return true;
            if (info.Length > IndexLimits.MaxIndexableFileBytes)
                return true;
        }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException) { return true; }

        try
        {
            using var stream = File.OpenRead(absolutePath);
            Span<byte> buffer = stackalloc byte[MaxBinaryPeekBytes];
            var read = stream.Read(buffer);
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == 0) return true;
            }

            return false;
        }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException) { return true; }
    }
}
