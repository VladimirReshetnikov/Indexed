using System.Text.Json.Serialization;

namespace Indexed.Abstractions;

/// <summary>
/// Selects which index surface a <see cref="SearchRequest"/> is served from.
/// </summary>
/// <remarks>
/// <para>
/// The mode is chosen by the caller and never inferred from the pattern. It
/// determines tokenization rules, case-sensitivity handling, and the shape of
/// hits the service returns.
/// </para>
/// <para>
/// In <see cref="Code"/> mode, matches always carry <see cref="SpanKind.Code"/>
/// and never carry a <see cref="MatchSpan"/>. <see cref="Prose"/> mode queries
/// extractor-produced spans (XML doc comments, comment blocks, Markdown,
/// plain text) and returns their real <see cref="SpanKind"/> plus an enclosing
/// <see cref="MatchSpan"/>. <see cref="Auto"/> runs both surfaces when both can
/// contribute, then merges the results with prose preferred on exact
/// <c>(path, line)</c> collisions.
/// </para>
/// <para>
/// Wire format: kebab-case JSON strings (<c>"auto"</c>, <c>"code"</c>,
/// <c>"prose"</c>). Default when omitted from a request is <see cref="Auto"/>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var req = new SearchRequest("IndexManifest") { Mode = QueryMode.Code };
/// </code>
/// </example>
[JsonConverter(typeof(JsonStringEnumConverter<QueryMode>))]
public enum QueryMode
{
    /// <summary>
    /// The DTO default. Runs both code and prose plans when both are
    /// meaningful for the request shape, then merges the results.
    /// Regex queries run only the code side because prose search uses FTS5
    /// match expressions rather than regex syntax.
    /// </summary>
    [JsonStringEnumMemberName("auto")]
    Auto = 0,

    /// <summary>
    /// Query the trigram-tokenized code surface only. Honors
    /// <see cref="SearchRequest.CaseSensitive"/>. Every returned match has
    /// <c>Kind = Code</c>.
    /// </summary>
    [JsonStringEnumMemberName("code")]
    Code = 1,

    /// <summary>
    /// Query the porter-stemmed prose surface only. Case-insensitive regardless
    /// of <see cref="SearchRequest.CaseSensitive"/>. Matches carry their
    /// extracted <see cref="SpanKind"/> and <see cref="MatchSpan"/>.
    /// </summary>
    [JsonStringEnumMemberName("prose")]
    Prose = 2,
}
