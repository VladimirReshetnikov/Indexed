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
/// Files are classified as non-indexable when they are missing, unreadable,
/// directories, exceed the configured size cap, or contain a NUL byte in the
/// first 8 KiB.
/// </para>
/// </remarks>
public static class BinaryFileClassifier
{
    private const int MaxBinaryPeekBytes = 8192;

    public static FileIndexability Classify(
        string absolutePath,
        long maxIndexableFileBytes = IndexLimits.MaxIndexableFileBytes)
    {
        if (maxIndexableFileBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxIndexableFileBytes));

        if (string.IsNullOrWhiteSpace(absolutePath))
            return new FileIndexability(FileIndexabilityStatus.Unreadable, null, "path is empty");

        FileInfo info;
        try
        {
            info = new FileInfo(absolutePath);
            if (!info.Exists)
                return new FileIndexability(FileIndexabilityStatus.Missing, null, null);
            if ((info.Attributes & FileAttributes.Directory) != 0)
                return new FileIndexability(FileIndexabilityStatus.Directory, null, null);
            if (info.Length > maxIndexableFileBytes)
                return new FileIndexability(
                    FileIndexabilityStatus.Oversize,
                    info.Length,
                    $"size {info.Length} bytes exceeds cap {maxIndexableFileBytes} bytes");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new FileIndexability(FileIndexabilityStatus.Unreadable, null, ex.Message);
        }
        catch (IOException ex)
        {
            return new FileIndexability(FileIndexabilityStatus.Unreadable, null, ex.Message);
        }

        try
        {
            using var stream = File.OpenRead(absolutePath);
            Span<byte> buffer = stackalloc byte[MaxBinaryPeekBytes];
            var read = stream.Read(buffer);
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == 0)
                {
                    return new FileIndexability(
                        FileIndexabilityStatus.Binary,
                        info.Length,
                        "NUL byte in first 8192 bytes");
                }
            }

            return new FileIndexability(FileIndexabilityStatus.Text, info.Length, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new FileIndexability(FileIndexabilityStatus.Unreadable, info.Length, ex.Message);
        }
        catch (IOException ex)
        {
            return new FileIndexability(FileIndexabilityStatus.Unreadable, info.Length, ex.Message);
        }
    }

    public static bool IsLikelyBinary(
        string absolutePath,
        long maxIndexableFileBytes = IndexLimits.MaxIndexableFileBytes)
        => Classify(absolutePath, maxIndexableFileBytes).Status != FileIndexabilityStatus.Text;
}

public enum FileIndexabilityStatus
{
    Text,
    Missing,
    Directory,
    Oversize,
    Binary,
    Unreadable,
}

public readonly record struct FileIndexability(
    FileIndexabilityStatus Status,
    long? SizeBytes,
    string? Detail);
