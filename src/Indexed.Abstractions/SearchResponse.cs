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
/// <see cref="Truncated"/> is set when either of the collection caps hit —
/// <see cref="SearchRequest.MaxMatches"/> or
/// <see cref="SearchRequest.MaxMatchesPerFile"/>. Timeouts do not set this
/// flag; exceeding <see cref="SearchRequest.TimeoutMs"/> returns an
/// <see cref="ErrorResponse"/> with
/// <see cref="IndexedErrorCode.TimeoutExceeded"/> rather than a partial
/// <see cref="SearchResponse"/>.
/// </para>
/// <para>
/// <see cref="TotalMatches"/> is a lower bound on the population of hits
/// actually scanned: it accumulates the pre-per-file-cap hit count for each
/// file the executor inspected before the global cap halted the scan. It is
/// therefore only complete when <see cref="Truncated"/> is <c>false</c>;
/// when <see cref="Truncated"/> is <c>true</c> the unscanned suffix of the
/// candidate set contributes nothing to this number. Callers that need an
/// exact population count must page with disjoint
/// <see cref="SearchRequest.PathGlob"/> restrictions.
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
