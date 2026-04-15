using System.Text.Json.Serialization;

namespace Indexed.Abstractions;

/// <summary>
/// Ordering policy for the <see cref="SearchResponse.Matches"/> list.
/// </summary>
/// <remarks>
/// <para>
/// Sort order is stable: two invocations of the same query against the same
/// index state must produce matches in the same order. This matters for
/// snapshot tests and for agents that compare successive responses.
/// </para>
/// <para>
/// Wire format: kebab-case JSON strings (<c>"path"</c>, <c>"relevance"</c>).
/// Default when omitted from a request is <see cref="Path"/>.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<SortBy>))]
public enum SortBy
{
    /// <summary>
    /// Lexicographic by repository-relative POSIX path, then by line, then by
    /// column. Prose-hit tiebreaks within a single line use BM25 descending.
    /// </summary>
    [JsonStringEnumMemberName("path")]
    Path = 0,

    /// <summary>
    /// Ranked by <c>bm25(prose_fts)</c> for prose hits; code hits use a stable
    /// path-based secondary key. Callers that need deterministic ordering
    /// across unrelated pattern/query pairs should prefer <see cref="Path"/>.
    /// </summary>
    [JsonStringEnumMemberName("relevance")]
    Relevance = 1,
}
