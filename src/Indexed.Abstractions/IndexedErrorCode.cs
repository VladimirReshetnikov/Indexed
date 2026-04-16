using System.Text.Json.Serialization;

namespace Indexed.Abstractions;

/// <summary>
/// Closed enumeration of failure categories an Indexed response can report.
/// </summary>
/// <remarks>
/// <para>
/// Every non-2xx HTTP response carries an <see cref="ErrorResponse"/> whose
/// <see cref="ErrorResponse.Code"/> is one of these values. The value is
/// intended for programmatic branching by the CLI and by automated agents;
/// <see cref="ErrorResponse.Message"/> is the human-readable surface.
/// </para>
/// <para>
/// Wire format: kebab-case JSON strings (<c>"bad-request"</c>,
/// <c>"not-implemented"</c>, …). New codes may be added in minor releases; the
/// CLI treats unknown codes as <see cref="Internal"/> after logging.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<IndexedErrorCode>))]
public enum IndexedErrorCode
{
    /// <summary>
    /// The request payload violated a documented precondition (invalid enum
    /// value, empty pattern, out-of-range cap). Maps to HTTP 400.
    /// </summary>
    [JsonStringEnumMemberName("bad-request")]
    BadRequest = 0,

    /// <summary>
    /// The supplied pattern could not be parsed as the expected shape — regex
    /// compile failure in code mode, FTS5 match expression error in prose
    /// mode. Maps to HTTP 400.
    /// </summary>
    [JsonStringEnumMemberName("pattern-invalid")]
    PatternInvalid = 1,

    /// <summary>
    /// The query exceeded <see cref="SearchRequest.TimeoutMs"/> before
    /// producing any match. Partial results that hit the cap are returned as a
    /// normal response with <c>truncated = true</c> instead of this code.
    /// Maps to HTTP 504.
    /// </summary>
    [JsonStringEnumMemberName("timeout-exceeded")]
    TimeoutExceeded = 2,

    /// <summary>
    /// The daemon could not locate a git repository at its configured root.
    /// Usually transient during initial startup. Maps to HTTP 503.
    /// </summary>
    [JsonStringEnumMemberName("repo-not-found")]
    RepoNotFound = 3,

    /// <summary>
    /// The service is operational but cannot currently satisfy the request —
    /// for example during a forced rescan that is rebuilding the index. Maps
    /// to HTTP 503.
    /// </summary>
    [JsonStringEnumMemberName("unavailable")]
    Unavailable = 4,

    /// <summary>
    /// The requested capability is recognized but not yet implemented at the
    /// current staging level. Stage 2 uses this for <see cref="QueryMode.Prose"/>
    /// and for <c>sortBy=relevance</c> (requires BM25 scoring that Stage 3
    /// will add). <see cref="QueryMode.Auto"/> is <em>not</em> mapped to this
    /// code — it is served as an alias of <see cref="QueryMode.Code"/>.
    /// Maps to HTTP 501.
    /// </summary>
    [JsonStringEnumMemberName("not-implemented")]
    NotImplemented = 5,

    /// <summary>
    /// An unexpected exception escaped request handling. Details are logged
    /// server-side; the response carries a correlation id in
    /// <see cref="ErrorResponse.Details"/>. Maps to HTTP 500.
    /// </summary>
    [JsonStringEnumMemberName("internal")]
    Internal = 6,

    /// <summary>
    /// The request was well-formed but the caller lacks permission to perform
    /// the requested action — currently used by the shutdown endpoint when the
    /// caller-supplied token does not match the daemon's session token.
    /// Maps to HTTP 403.
    /// </summary>
    [JsonStringEnumMemberName("forbidden")]
    Forbidden = 7,
}
