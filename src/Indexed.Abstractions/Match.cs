using System.Collections.Generic;

namespace Indexed.Abstractions;

/// <summary>
/// A single search hit returned in <see cref="SearchResponse.Matches"/>.
/// </summary>
/// <remarks>
/// <para>
/// One match corresponds to one <c>(path, line, column)</c> triple in the
/// repository at the moment the daemon answered the request. The match text is
/// the on-disk line content; it has already been decoded from the source
/// encoding (UTF-8, UTF-16, etc.) and BOMs have been stripped, so callers can
/// display it directly without re-reading the file.
/// </para>
/// <para>
/// <see cref="ByteOffset"/> semantics (Stage 2 reality): the value is the
/// 0-based <em>UTF-16 character offset</em> of the match within the decoded
/// in-memory string, not the byte offset within the raw file. For pure-ASCII
/// files the two coincide; for UTF-8 files containing non-ASCII characters
/// the character offset is less than the byte offset, and for UTF-16 files
/// they differ by a factor of two. The field is retained under its historical
/// name for wire compatibility; Stage 3 plans a UTF-8 byte mapping that
/// matches the documented name without a DTO break.
/// </para>
/// <para>
/// <see cref="Span"/> is populated when and only when <see cref="Kind"/> is
/// not <see cref="SpanKind.Code"/>. Matches in <see cref="QueryMode.Code"/>
/// responses never carry a span.
/// </para>
/// <para>
/// <see cref="ContextBefore"/> and <see cref="ContextAfter"/> are fewer than
/// the requested counts when the match is near the start or end of the file;
/// they are never null and are empty lists when no context was requested.
/// </para>
/// </remarks>
/// <param name="Path">
/// Repository-relative POSIX path (forward slashes, no leading slash).
/// </param>
/// <param name="Line">1-based line number of the match.</param>
/// <param name="Column">
/// 1-based column of the first character of the match within
/// <see cref="Text"/>. Measured in UTF-16 code units to match standard editor
/// conventions.
/// </param>
/// <param name="ByteOffset">
/// 0-based UTF-16 character offset of <see cref="Text"/> in the decoded
/// in-memory string. See the remarks section for the byte-vs-character
/// caveat; the field name is historical.
/// </param>
/// <param name="Text">
/// The full line containing the match, trimmed of trailing line terminators.
/// </param>
/// <param name="Kind">Content category; see <see cref="SpanKind"/>.</param>
/// <param name="Span">
/// Enclosing prose span for non-code matches; <c>null</c> when
/// <see cref="Kind"/> is <see cref="SpanKind.Code"/>.
/// </param>
/// <param name="ContextBefore">
/// Lines immediately preceding <see cref="Text"/>, oldest first. Length is at
/// most <see cref="SearchRequest.ContextBefore"/>.
/// </param>
/// <param name="ContextAfter">
/// Lines immediately following <see cref="Text"/>, in source order. Length is
/// at most <see cref="SearchRequest.ContextAfter"/>.
/// </param>
public sealed record Match(
    string Path,
    int Line,
    int Column,
    long ByteOffset,
    string Text,
    SpanKind Kind,
    MatchSpan? Span,
    IReadOnlyList<string> ContextBefore,
    IReadOnlyList<string> ContextAfter);
