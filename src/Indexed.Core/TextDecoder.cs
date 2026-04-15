using System;
using System.Text;

namespace Indexed.Core;

/// <summary>
/// Shared text decoder that detects BOMs (UTF-8, UTF-16 LE/BE, UTF-32 LE/BE)
/// and falls back to UTF-8 for files without a BOM. Non-decodable bytes are
/// replaced with U+FFFD so the FTS5 index always contains valid text.
/// </summary>
/// <remarks>
/// <para>
/// BOM detection order matches the recommendation from the Unicode standard
/// (longest prefix first): UTF-32 LE/BE → UTF-16 LE/BE → UTF-8 → fallback.
/// </para>
/// <para>
/// Files without a BOM are decoded as UTF-8 with the <c>DecoderFallback.ReplacementFallback</c>.
/// This handles the vast majority of modern source files. Legacy encodings
/// (Windows-1252, Shift-JIS) without a BOM produce U+FFFD substitutions —
/// imperfect but safe. A heuristic for Windows-1252 is not attempted because
/// it would misidentify valid UTF-8 sequences; the BOM-or-UTF-8 rule is the
/// pragmatic choice for a code search index.
/// </para>
/// </remarks>
internal static class TextDecoder
{
    /// <summary>
    /// Decode a byte buffer to a string, detecting and stripping any BOM.
    /// </summary>
    /// <param name="bytes">Raw file bytes.</param>
    /// <returns>Decoded text content.</returns>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        // UTF-32 LE BOM: FF FE 00 00 (must check before UTF-16 LE which shares the first two bytes)
        if (bytes.Length >= 4
            && bytes[0] == 0xFF && bytes[1] == 0xFE
            && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return Encoding.UTF32.GetString(bytes.Slice(4));
        }

        // UTF-32 BE BOM: 00 00 FE FF
        if (bytes.Length >= 4
            && bytes[0] == 0x00 && bytes[1] == 0x00
            && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return GetUtf32Be().GetString(bytes.Slice(4));
        }

        // UTF-16 LE BOM: FF FE
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes.Slice(2));
        }

        // UTF-16 BE BOM: FE FF
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes.Slice(2));
        }

        // UTF-8 BOM: EF BB BF
        if (bytes.Length >= 3
            && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes.Slice(3));
        }

        // No BOM — decode as UTF-8. Invalid byte sequences become U+FFFD.
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Get or create a UTF-32 Big Endian encoding instance.
    /// </summary>
    private static Encoding GetUtf32Be()
        => new UTF32Encoding(bigEndian: true, byteOrderMark: false);
}
