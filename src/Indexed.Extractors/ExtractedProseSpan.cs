using Indexed.Abstractions;

namespace Indexed.Extractors;

/// <summary>
/// One extracted prose span ready to be stored in <c>prose_fts</c>.
/// </summary>
/// <param name="StartLine">1-based inclusive start line in the source file.</param>
/// <param name="EndLine">1-based inclusive end line in the source file.</param>
/// <param name="Kind">Semantic kind of the extracted span.</param>
/// <param name="Content">
/// Normalized extracted prose content. Line breaks are normalized to
/// <c>\n</c> so query-time highlighting and context slicing remain stable.
/// </param>
public sealed record ExtractedProseSpan(
    int StartLine,
    int EndLine,
    SpanKind Kind,
    string Content);
