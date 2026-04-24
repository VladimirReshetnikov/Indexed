using System;
using System.Collections.Generic;
using System.Text;

namespace Indexed.Core;

/// <summary>
/// Pure helpers for translating between character offsets and line/column
/// coordinates inside a decoded file body. Factored out so the query
/// executor and future callers (rescan diffing, incremental re-indexing) can
/// share one tested implementation.
/// </summary>
/// <remarks>
/// <para>
/// Line/column calculations operate on character offsets into the .NET string
/// (UTF-16 code units). The separate <see cref="Utf8ByteOffsetOf"/> helper
/// maps those positions to the wire-level UTF-8 byte offset reported in
/// <see cref="Indexed.Abstractions.Match.ByteOffset"/>.
/// </para>
/// <para>
/// Line numbers are 1-based; column numbers are 1-based and measured in
/// characters from the start of the line. Both conventions match the CLI's
/// ripgrep-style output.
/// </para>
/// </remarks>
public static class MatchExtraction
{
    /// <summary>
    /// Precompute the starting character offset of every line in
    /// <paramref name="content"/>, including a synthetic entry at <c>0</c> for
    /// the first line. Recognizes <c>\n</c>, <c>\r\n</c>, and bare <c>\r</c>.
    /// </summary>
    public static IReadOnlyList<int> ComputeLineOffsets(string content)
    {
        var offsets = new List<int> { 0 };
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c == '\n')
            {
                offsets.Add(i + 1);
            }
            else if (c == '\r')
            {
                if (i + 1 < content.Length && content[i + 1] == '\n')
                {
                    offsets.Add(i + 2);
                    i++;
                }
                else
                {
                    offsets.Add(i + 1);
                }
            }
        }
        return offsets;
    }

    /// <summary>
    /// Map a character offset into <paramref name="lineOffsets"/> to a
    /// (1-based line, 1-based column) pair.
    /// </summary>
    public static (int Line, int Column) LineAndColumnOf(int offset, IReadOnlyList<int> lineOffsets)
    {
        var lineIdx = BinarySearchLine(lineOffsets, offset);
        var col = offset - lineOffsets[lineIdx] + 1;
        return (lineIdx + 1, col);
    }

    /// <summary>
    /// Return the 1-based line text at <paramref name="line"/>, stripped of
    /// its trailing newline. Returns empty when the line is past the end.
    /// </summary>
    public static string LineTextAt(string content, IReadOnlyList<int> lineOffsets, int line)
    {
        var idx = line - 1;
        if (idx < 0 || idx >= lineOffsets.Count) return string.Empty;
        var start = lineOffsets[idx];
        var end = idx + 1 < lineOffsets.Count ? lineOffsets[idx + 1] : content.Length;
        return TrimTrailingNewline(content.Substring(start, end - start));
    }

    /// <summary>Context lines immediately before <paramref name="matchLine"/>.</summary>
    public static string[] ContextBefore(string content, IReadOnlyList<int> lineOffsets, int matchLine, int count)
    {
        if (count <= 0) return Array.Empty<string>();
        var first = Math.Max(1, matchLine - count);
        var take = matchLine - first;
        if (take <= 0) return Array.Empty<string>();
        var arr = new string[take];
        for (var i = 0; i < take; i++) arr[i] = LineTextAt(content, lineOffsets, first + i);
        return arr;
    }

    /// <summary>Context lines immediately after <paramref name="matchLine"/>.</summary>
    public static string[] ContextAfter(string content, IReadOnlyList<int> lineOffsets, int matchLine, int count)
    {
        if (count <= 0) return Array.Empty<string>();
        var start = matchLine + 1;
        var last = Math.Min(lineOffsets.Count, matchLine + count);
        if (start > last) return Array.Empty<string>();
        var take = last - start + 1;
        var arr = new string[take];
        for (var i = 0; i < take; i++) arr[i] = LineTextAt(content, lineOffsets, start + i);
        return arr;
    }

    /// <summary>
    /// Convert a character offset in <paramref name="content"/> to a UTF-8 byte
    /// offset.
    /// </summary>
    public static long Utf8ByteOffsetOf(int charOffset, string content)
    {
        if (string.IsNullOrEmpty(content) || charOffset <= 0)
            return 0;

        var clamped = Math.Clamp(charOffset, 0, content.Length);
        return Encoding.UTF8.GetByteCount(content.AsSpan(0, clamped));
    }

    private static int BinarySearchLine(IReadOnlyList<int> offsets, int target)
    {
        int lo = 0, hi = offsets.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (offsets[mid] <= target) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    private static string TrimTrailingNewline(string s)
    {
        if (s.EndsWith("\r\n", StringComparison.Ordinal)) return s[..^2];
        if (s.EndsWith("\n", StringComparison.Ordinal) || s.EndsWith("\r", StringComparison.Ordinal))
            return s[..^1];
        return s;
    }
}
