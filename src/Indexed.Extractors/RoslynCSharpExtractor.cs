using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using Indexed.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Indexed.Extractors;

public sealed class RoslynCSharpExtractor : IContentExtractor
{
    private static readonly Regex SeeCrefRegex = new(
        "<(?:see|seealso)\\s+cref\\s*=\\s*\"([^\"]+)\"\\s*/>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex NameReferenceRegex = new(
        "<(?:paramref|typeparamref)\\s+name\\s*=\\s*\"([^\"]+)\"\\s*/>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex BreakRegex = new(
        "<br\\s*/?>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex XmlTagRegex = new(
        "</?[^>]+>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IReadOnlyList<ExtractedProseSpan> Extract(string logicalPath, string content)
    {
        if (!TextExtractionUtilities.HasSearchableText(content))
            return [];

        var tree = CSharpSyntaxTree.ParseText(
            content,
            new CSharpParseOptions(documentationMode: DocumentationMode.Parse));
        var root = tree.GetRoot();
        var spans = new List<ExtractedProseSpan>();
        var seenDocSpans = new HashSet<(int Start, int End)>();
        var docLines = new HashSet<int>();
        var singleLineComments = new List<(int Line, string Content)>();

        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            switch (trivia.Kind())
            {
                case SyntaxKind.SingleLineDocumentationCommentTrivia:
                case SyntaxKind.MultiLineDocumentationCommentTrivia:
                {
                    var (startLine, endLine) = GetInclusiveLineRange(trivia);
                    if (!seenDocSpans.Add((startLine, endLine)))
                        break;

                    var normalized = NormalizeXmlDocComment(trivia.ToFullString());
                    TextExtractionUtilities.TryAddSpan(
                        spans,
                        startLine,
                        endLine,
                        SpanKind.XmlDoc,
                        normalized);

                    for (var line = startLine; line <= endLine; line++)
                        docLines.Add(line);

                    break;
                }

                case SyntaxKind.SingleLineCommentTrivia:
                {
                    var (line, _) = GetInclusiveLineRange(trivia);
                    if (docLines.Contains(line))
                        break;

                    singleLineComments.Add((line, StripLineComment(trivia.ToFullString())));
                    break;
                }

                case SyntaxKind.MultiLineCommentTrivia:
                {
                    var (startLine, endLine) = GetInclusiveLineRange(trivia);
                    var normalized = TextExtractionUtilities.NormalizeBlockComment(
                        trivia.ToFullString(),
                        "/*",
                        "*/",
                        trimLeadingAsterisk: true);
                    TextExtractionUtilities.TryAddSpan(
                        spans,
                        startLine,
                        endLine,
                        SpanKind.BlockComment,
                        normalized);
                    break;
                }
            }
        }

        singleLineComments.Sort(static (left, right) => left.Line.CompareTo(right.Line));
        AppendLineCommentBlocks(spans, singleLineComments);

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

    private static void AppendLineCommentBlocks(
        ICollection<ExtractedProseSpan> spans,
        IReadOnlyList<(int Line, string Content)> comments)
    {
        if (comments.Count == 0)
            return;

        var startLine = comments[0].Line;
        var endLine = startLine;
        var lines = new List<string> { comments[0].Content };

        for (var i = 1; i < comments.Count; i++)
        {
            var current = comments[i];
            if (current.Line == endLine + 1)
            {
                endLine = current.Line;
                lines.Add(current.Content);
                continue;
            }

            TextExtractionUtilities.TryAddSpan(
                spans,
                startLine,
                endLine,
                SpanKind.LineCommentBlock,
                string.Join('\n', lines));

            startLine = current.Line;
            endLine = current.Line;
            lines.Clear();
            lines.Add(current.Content);
        }

        TextExtractionUtilities.TryAddSpan(
            spans,
            startLine,
            endLine,
            SpanKind.LineCommentBlock,
            string.Join('\n', lines));
    }

    private static string StripLineComment(string rawComment)
    {
        var normalized = TextExtractionUtilities.NormalizeLineEndings(rawComment);
        var markerIndex = normalized.IndexOf("//", StringComparison.Ordinal);
        if (markerIndex < 0)
            return normalized;

        return TextExtractionUtilities.StripOptionalFollowingSpace(normalized[(markerIndex + 2)..]);
    }

    private static string NormalizeXmlDocComment(string rawComment)
    {
        var lines = TextExtractionUtilities.SplitLogicalLines(rawComment);
        if (lines.Length == 0)
            return string.Empty;

        for (var i = 0; i < lines.Length; i++)
            lines[i] = StripXmlDocExterior(lines[i], i == 0, i == lines.Length - 1);

        var text = string.Join('\n', lines);
        text = BreakRegex.Replace(text, "\n");
        text = SeeCrefRegex.Replace(text, "$1");
        text = NameReferenceRegex.Replace(text, "$1");
        text = XmlTagRegex.Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        return TextExtractionUtilities.NormalizeLineEndings(text);
    }

    private static string StripXmlDocExterior(string line, bool isFirst, bool isLast)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("///", StringComparison.Ordinal))
            return TextExtractionUtilities.StripOptionalFollowingSpace(trimmed[3..]);

        if (isFirst && trimmed.StartsWith("/**", StringComparison.Ordinal))
            trimmed = trimmed[3..];
        else if (trimmed.StartsWith("*", StringComparison.Ordinal))
            trimmed = trimmed[1..];

        if (isLast)
        {
            var closing = trimmed.IndexOf("*/", StringComparison.Ordinal);
            if (closing >= 0)
                trimmed = trimmed[..closing];
        }

        return TextExtractionUtilities.StripOptionalFollowingSpace(trimmed);
    }

    private static (int StartLine, int EndLine) GetInclusiveLineRange(SyntaxTrivia trivia)
    {
        var lineSpan = trivia.GetLocation().GetLineSpan();
        var startLine = lineSpan.StartLinePosition.Line + 1;
        var endLine = lineSpan.EndLinePosition.Line + 1;

        // Roslyn trivia locations often include the trailing newline, which
        // makes EndLine point at the following code line with column 0. Stored
        // prose spans should cover only the source lines that belong to the
        // comment itself, not the first non-comment line after it.
        if (lineSpan.EndLinePosition.Character == 0
            && lineSpan.EndLinePosition.Line > lineSpan.StartLinePosition.Line)
        {
            endLine--;
        }

        return (startLine, Math.Max(startLine, endLine));
    }
}
