using System;
using System.Collections.Generic;
using System.Text;

namespace Indexed.Extractors;

internal static class TextExtractionUtilities
{
    public static string NormalizeLineEndings(string text)
        => string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');

    public static int CountSourceLines(string text)
    {
        var normalized = NormalizeLineEndings(text);
        if (normalized.Length == 0)
            return 0;

        var lines = 1;
        for (var i = 0; i < normalized.Length; i++)
        {
            if (normalized[i] == '\n')
                lines++;
        }

        if (normalized[^1] == '\n')
            lines--;

        return Math.Max(lines, 0);
    }

    public static string[] SplitLogicalLines(string text)
    {
        var normalized = NormalizeLineEndings(text);
        if (normalized.Length == 0)
            return Array.Empty<string>();

        var lines = normalized.Split('\n');
        if (normalized[^1] == '\n' && lines.Length > 0)
            Array.Resize(ref lines, lines.Length - 1);

        return lines;
    }

    public static bool HasSearchableText(string? text)
        => !string.IsNullOrWhiteSpace(text);

    public static void TryAddSpan(
        ICollection<ExtractedProseSpan> spans,
        int startLine,
        int endLine,
        Indexed.Abstractions.SpanKind kind,
        string? content)
    {
        if (spans is null)
            throw new ArgumentNullException(nameof(spans));
        if (startLine <= 0 || endLine < startLine)
            return;

        var normalized = NormalizeLineEndings(content ?? string.Empty);
        if (!HasSearchableText(normalized))
            return;

        spans.Add(new ExtractedProseSpan(startLine, endLine, kind, normalized));
    }

    public static string StripOptionalFollowingSpace(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return text[0] == ' ' ? text[1..] : text;
    }

    public static string NormalizeBlockComment(
        string rawComment,
        string startDelimiter,
        string endDelimiter,
        bool trimLeadingAsterisk)
    {
        var body = NormalizeLineEndings(rawComment);
        if (body.StartsWith(startDelimiter, StringComparison.Ordinal))
            body = body[startDelimiter.Length..];
        if (body.EndsWith(endDelimiter, StringComparison.Ordinal))
            body = body[..^endDelimiter.Length];

        var lines = SplitLogicalLines(body);
        if (lines.Length == 0)
            return string.Empty;

        var builder = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (trimLeadingAsterisk)
                line = TrimLeadingAsterisk(line);

            builder.Append(line);
            if (i + 1 < lines.Length)
                builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string TrimLeadingAsterisk(string line)
    {
        if (string.IsNullOrEmpty(line))
            return string.Empty;

        var index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;

        if (index >= line.Length || line[index] != '*')
            return line;

        index++;
        if (index < line.Length && line[index] == ' ')
            index++;

        return line[index..];
    }
}
