using System.Collections.Generic;

namespace Indexed.Abstractions;

/// <summary>
/// Request body for <c>POST /search</c>.
/// </summary>
/// <remarks>
/// <para>
/// Wire shape matches the proposal's §8.1 JSON contract exactly. Omitted fields
/// in the JSON payload fall back to the defaults declared on this record.
/// </para>
/// <para>
/// Preconditions enforced by the service — callers that violate these receive
/// an <see cref="ErrorResponse"/> with <see cref="IndexedErrorCode.BadRequest"/>:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="Pattern"/> must be non-null and non-empty.</description></item>
///   <item><description><see cref="MaxMatches"/> must be in <c>[1, 10000]</c>.</description></item>
///   <item><description><see cref="MaxMatchesPerFile"/> must be in <c>[1, MaxMatches]</c>.</description></item>
///   <item><description><see cref="ContextBefore"/> and <see cref="ContextAfter"/> must be in <c>[0, 50]</c>.</description></item>
///   <item><description><see cref="TimeoutMs"/> must be in <c>[1, 30000]</c>.</description></item>
/// </list>
/// <para>
/// <see cref="IsRegex"/> is a hint from the CLI; the service always parses the
/// pattern as regex for code mode (literals are a degenerate regex) and as an
/// FTS5 match expression for prose mode. The flag exists so the CLI can report
/// clear errors without waiting for a round-trip.
/// </para>
/// </remarks>
/// <param name="Pattern">
/// The search pattern. Interpreted as a literal byte sequence for code
/// searches when <see cref="IsRegex"/> is <c>false</c>, as a .NET regex when
/// <see cref="IsRegex"/> is <c>true</c>, and as an FTS5 query expression for
/// prose searches (regardless of <see cref="IsRegex"/>).
/// </param>
/// <param name="Mode">
/// Which index surface to query. See <see cref="QueryMode"/>.
/// </param>
/// <param name="IsRegex">
/// Whether <see cref="Pattern"/> is a regular expression rather than a literal.
/// Ignored in <see cref="QueryMode.Prose"/>.
/// </param>
/// <param name="CaseSensitive">
/// Case-sensitivity flag for code searches. Prose searches are always
/// case-insensitive (porter stemming normalizes case).
/// </param>
/// <param name="KindFilter">
/// Optional post-retrieval filter over <see cref="Match.Kind"/>. <c>null</c>
/// means no restriction. Applied after index retrieval, so narrow filters
/// reduce payload size but not query time.
/// </param>
/// <param name="PathGlob">
/// Optional gitignore-style glob applied to repository-relative POSIX paths.
/// <c>null</c> means every indexed file is eligible.
/// </param>
/// <param name="ExcludeGlob">
/// Optional list of gitignore-style globs that remove files from the candidate
/// set after <see cref="PathGlob"/> is applied.
/// </param>
/// <param name="ContextBefore">
/// Number of lines of leading context to attach to each <see cref="Match"/>.
/// </param>
/// <param name="ContextAfter">
/// Number of lines of trailing context to attach to each <see cref="Match"/>.
/// </param>
/// <param name="MaxMatches">
/// Global cap on <see cref="SearchResponse.Matches"/>. The service emits
/// <c>truncated = true</c> once the cap is reached.
/// </param>
/// <param name="MaxMatchesPerFile">
/// Per-file cap applied before <see cref="MaxMatches"/>. Prevents a single
/// large file from crowding out hits from other files.
/// </param>
/// <param name="SortBy">
/// Ordering policy for <see cref="SearchResponse.Matches"/>.
/// </param>
/// <param name="TimeoutMs">
/// Soft deadline in milliseconds. A request that exceeds this budget returns
/// whatever was found so far with <c>truncated = true</c>, or an
/// <see cref="ErrorResponse"/> with
/// <see cref="IndexedErrorCode.TimeoutExceeded"/> if nothing was found yet.
/// </param>
/// <example>
/// <code>
/// var req = new SearchRequest(
///     Pattern: "IndexManifest",
///     Mode: QueryMode.Code,
///     PathGlob: "src/**/*.cs");
/// </code>
/// </example>
public sealed record SearchRequest(
    string Pattern,
    QueryMode Mode = QueryMode.Auto,
    bool IsRegex = false,
    bool CaseSensitive = false,
    IReadOnlyList<SpanKind>? KindFilter = null,
    string? PathGlob = null,
    IReadOnlyList<string>? ExcludeGlob = null,
    int ContextBefore = 0,
    int ContextAfter = 0,
    int MaxMatches = 200,
    int MaxMatchesPerFile = 20,
    SortBy SortBy = SortBy.Path,
    int TimeoutMs = 2000);
