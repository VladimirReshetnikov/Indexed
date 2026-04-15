namespace Indexed.Abstractions;

/// <summary>
/// Line range of a prose span that contained a match.
/// </summary>
/// <remarks>
/// <para>
/// Both bounds are 1-based and inclusive. A single-line span has
/// <c>StartLine == EndLine</c>. Line numbers refer to the original source file
/// (not the extracted prose), so callers can seek directly to the region with
/// a standard line-based editor.
/// </para>
/// <para>
/// This type is only present on matches whose <see cref="Match.Kind"/> is not
/// <see cref="SpanKind.Code"/>. Code matches have no span concept; the
/// <c>Line</c>/<c>Column</c> pair on the match itself is the only location.
/// </para>
/// </remarks>
/// <param name="StartLine">The 1-based, inclusive first line of the span.</param>
/// <param name="EndLine">The 1-based, inclusive last line of the span.</param>
public readonly record struct MatchSpan(int StartLine, int EndLine);
