using System.Collections.Generic;

namespace Indexed.Abstractions;

/// <summary>
/// Response body for <c>POST /search</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Matches"/> ordering honors the request's <see cref="SortBy"/>
/// and is stable: two identical requests against the same index state produce
/// matches in the same order.
/// </para>
/// <para>
/// <see cref="Truncated"/> is set when any cap was hit:
/// <see cref="SearchRequest.MaxMatches"/>,
/// <see cref="SearchRequest.MaxMatchesPerFile"/>, or
/// <see cref="SearchRequest.TimeoutMs"/>. The caller cannot distinguish which
/// cap fired from the response alone, only that results are incomplete.
/// </para>
/// <para>
/// <see cref="TotalMatches"/> reports the number of hits found <em>before</em>
/// the global <see cref="SearchRequest.MaxMatches"/> cap was applied. It is at
/// most the cap plus one (the service stops counting after the cap is
/// exceeded); callers that need an exact population count must page with
/// disjoint <see cref="SearchRequest.PathGlob"/> restrictions.
/// </para>
/// </remarks>
/// <param name="Freshness">
/// Index-staleness metadata that applies to every match in this response.
/// </param>
/// <param name="Matches">
/// Ordered hit list. Empty when no matches were found; never <c>null</c>.
/// </param>
/// <param name="Truncated">
/// Whether any cap prevented a complete result set.
/// </param>
/// <param name="TotalMatches">
/// Count of matches found before the global cap was applied. See remarks.
/// </param>
/// <param name="ElapsedMs">
/// Wall-clock duration spent executing the query on the service side. Does not
/// include HTTP framing or client-side parsing.
/// </param>
public sealed record SearchResponse(
    Freshness Freshness,
    IReadOnlyList<Match> Matches,
    bool Truncated,
    int TotalMatches,
    long ElapsedMs);
