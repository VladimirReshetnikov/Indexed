using System.Collections.Generic;
using Indexed.Abstractions;

namespace Indexed.Extractors;

internal abstract class WholeFileExtractorBase : IContentExtractor
{
    private readonly SpanKind _kind;

    protected WholeFileExtractorBase(SpanKind kind)
    {
        _kind = kind;
    }

    public IReadOnlyList<ExtractedProseSpan> Extract(string logicalPath, string content)
    {
        var normalized = string.Join('\n', TextExtractionUtilities.SplitLogicalLines(content));
        if (!TextExtractionUtilities.HasSearchableText(normalized))
            return [];

        var endLine = TextExtractionUtilities.CountSourceLines(content);
        return
        [
            new ExtractedProseSpan(1, endLine, _kind, normalized)
        ];
    }
}

internal sealed class MarkdownExtractor : WholeFileExtractorBase
{
    public MarkdownExtractor()
        : base(SpanKind.Markdown)
    {
    }
}

internal sealed class PlainTextExtractor : WholeFileExtractorBase
{
    public PlainTextExtractor()
        : base(SpanKind.PlainText)
    {
    }
}
