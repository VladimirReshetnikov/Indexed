using System;
using System.Collections.Generic;
using Indexed.Abstractions;

namespace Indexed.Extractors;

internal readonly record struct BlockDelimiter(string Start, string End);

internal abstract class RegexCommentExtractorBase : IContentExtractor
{
    protected virtual IReadOnlyList<string> LinePrefixes => Array.Empty<string>();
    protected virtual IReadOnlyList<BlockDelimiter> BlockDelimiters => Array.Empty<BlockDelimiter>();
    protected virtual bool TrimLeadingAsteriskInBlocks => false;
    protected virtual bool IgnoreHashBangOnFirstLine => false;

    protected virtual SpanKind LineCommentKind => SpanKind.LineCommentBlock;
    protected virtual SpanKind BlockCommentKind => SpanKind.BlockComment;

    public IReadOnlyList<ExtractedProseSpan> Extract(string logicalPath, string content)
    {
        var normalized = TextExtractionUtilities.NormalizeLineEndings(content);
        if (!TextExtractionUtilities.HasSearchableText(normalized))
            return [];

        var spans = new List<ExtractedProseSpan>();
        var blockedLines = new HashSet<int>();

        ScanBlockComments(normalized, spans, blockedLines);
        ScanLineComments(normalized, spans, blockedLines);

        spans.Sort(static (left, right) =>
        {
            var byLine = left.StartLine.CompareTo(right.StartLine);
            if (byLine != 0)
                return byLine;

            var byEnd = left.EndLine.CompareTo(right.EndLine);
            if (byEnd != 0)
                return byEnd;

            return left.Kind.CompareTo(right.Kind);
        });

        return spans;
    }

    private void ScanBlockComments(
        string content,
        List<ExtractedProseSpan> spans,
        HashSet<int> blockedLines)
    {
        if (BlockDelimiters.Count == 0)
            return;

        var lineStarts = BuildLineStarts(content);
        var searchIndex = 0;

        while (searchIndex < content.Length)
        {
            var bestIndex = -1;
            BlockDelimiter bestDelimiter = default;

            foreach (var delimiter in BlockDelimiters)
            {
                var candidate = content.IndexOf(delimiter.Start, searchIndex, StringComparison.Ordinal);
                if (candidate < 0)
                    continue;

                if (bestIndex < 0 || candidate < bestIndex)
                {
                    bestIndex = candidate;
                    bestDelimiter = delimiter;
                }
            }

            if (bestIndex < 0)
                break;

            var closingIndex = content.IndexOf(
                bestDelimiter.End,
                bestIndex + bestDelimiter.Start.Length,
                StringComparison.Ordinal);
            var endExclusive = closingIndex >= 0
                ? closingIndex + bestDelimiter.End.Length
                : content.Length;

            var startLine = GetLineNumber(lineStarts, bestIndex);
            var endLine = GetLineNumber(lineStarts, Math.Max(bestIndex, endExclusive - 1));
            var rawComment = content.Substring(bestIndex, endExclusive - bestIndex);
            var normalized = TextExtractionUtilities.NormalizeBlockComment(
                rawComment,
                bestDelimiter.Start,
                bestDelimiter.End,
                TrimLeadingAsteriskInBlocks);

            TextExtractionUtilities.TryAddSpan(
                spans,
                startLine,
                endLine,
                BlockCommentKind,
                normalized);

            for (var line = startLine; line <= endLine; line++)
                blockedLines.Add(line);

            searchIndex = Math.Max(bestIndex + bestDelimiter.Start.Length, endExclusive);
        }
    }

    private void ScanLineComments(
        string content,
        List<ExtractedProseSpan> spans,
        HashSet<int> blockedLines)
    {
        if (LinePrefixes.Count == 0)
            return;

        var lines = TextExtractionUtilities.SplitLogicalLines(content);
        var currentStartLine = 0;
        var currentEndLine = 0;
        List<string>? currentLines = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            if (blockedLines.Contains(lineNumber))
            {
                FlushCurrentLineBlock(spans, ref currentStartLine, ref currentEndLine, ref currentLines);
                continue;
            }

            var line = lines[i];
            var bestIndex = -1;
            string? bestPrefix = null;
            foreach (var prefix in LinePrefixes)
            {
                var candidate = line.IndexOf(prefix, StringComparison.Ordinal);
                if (candidate < 0)
                    continue;

                if (bestIndex < 0 || candidate < bestIndex)
                {
                    bestIndex = candidate;
                    bestPrefix = prefix;
                }
            }

            if (bestIndex < 0 || bestPrefix is null)
            {
                FlushCurrentLineBlock(spans, ref currentStartLine, ref currentEndLine, ref currentLines);
                continue;
            }

            if (IgnoreHashBangOnFirstLine
                && lineNumber == 1
                && bestPrefix == "#"
                && line.StartsWith("#!", StringComparison.Ordinal))
            {
                FlushCurrentLineBlock(spans, ref currentStartLine, ref currentEndLine, ref currentLines);
                continue;
            }

            var commentText = TextExtractionUtilities.StripOptionalFollowingSpace(
                line[(bestIndex + bestPrefix.Length)..]);

            if (currentLines is null || currentEndLine + 1 != lineNumber)
            {
                FlushCurrentLineBlock(spans, ref currentStartLine, ref currentEndLine, ref currentLines);
                currentStartLine = lineNumber;
                currentEndLine = lineNumber;
                currentLines = new List<string>();
            }
            else
            {
                currentEndLine = lineNumber;
            }

            currentLines.Add(commentText);
        }

        FlushCurrentLineBlock(spans, ref currentStartLine, ref currentEndLine, ref currentLines);
    }

    private void FlushCurrentLineBlock(
        List<ExtractedProseSpan> spans,
        ref int currentStartLine,
        ref int currentEndLine,
        ref List<string>? currentLines)
    {
        if (currentLines is null)
            return;

        TextExtractionUtilities.TryAddSpan(
            spans,
            currentStartLine,
            currentEndLine,
            LineCommentKind,
            string.Join('\n', currentLines));

        currentStartLine = 0;
        currentEndLine = 0;
        currentLines = null;
    }

    private static int[] BuildLineStarts(string content)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n' && i + 1 < content.Length)
                starts.Add(i + 1);
        }

        return starts.ToArray();
    }

    private static int GetLineNumber(int[] lineStarts, int index)
    {
        var position = Array.BinarySearch(lineStarts, index);
        if (position >= 0)
            return position + 1;

        position = ~position - 1;
        return Math.Max(position + 1, 1);
    }
}

internal sealed class CFamilyRegexCommentExtractor : RegexCommentExtractorBase
{
    protected override IReadOnlyList<string> LinePrefixes => ["//"];
    protected override IReadOnlyList<BlockDelimiter> BlockDelimiters => [new("/*", "*/")];
    protected override bool TrimLeadingAsteriskInBlocks => true;
}

internal sealed class FSharpRegexCommentExtractor : RegexCommentExtractorBase
{
    protected override IReadOnlyList<string> LinePrefixes => ["//"];
    protected override IReadOnlyList<BlockDelimiter> BlockDelimiters => [new("(*", "*)")];
}

internal sealed class HashLineCommentExtractor : RegexCommentExtractorBase
{
    protected override IReadOnlyList<string> LinePrefixes => ["#"];
    protected override bool IgnoreHashBangOnFirstLine => true;
}

internal sealed class PowerShellCommentExtractor : RegexCommentExtractorBase
{
    protected override IReadOnlyList<string> LinePrefixes => ["#"];
    protected override IReadOnlyList<BlockDelimiter> BlockDelimiters => [new("<#", "#>")];
    protected override bool IgnoreHashBangOnFirstLine => true;
}

internal sealed class SqlCommentExtractor : RegexCommentExtractorBase
{
    protected override IReadOnlyList<string> LinePrefixes => ["--"];
    protected override IReadOnlyList<BlockDelimiter> BlockDelimiters => [new("/*", "*/")];
    protected override bool TrimLeadingAsteriskInBlocks => true;
}

internal sealed class XmlHtmlCommentExtractor : RegexCommentExtractorBase
{
    protected override IReadOnlyList<BlockDelimiter> BlockDelimiters => [new("<!--", "-->")];
}
