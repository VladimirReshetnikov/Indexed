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
/// and never carry a <see cref="MatchSpan"/>. <see cref="Prose"/> mode is not
/// yet wired at Stage 2 and returns
/// <see cref="IndexedErrorCode.NotImplemented"/>; it will become available once
/// Stage 3 ships the prose extractor and span-kind tagging. At Stage 2,
/// <see cref="Auto"/> (the DTO default) is executed as an alias of
/// <see cref="Code"/> — Auto is not a merged/parallel plan at this stage.
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
    /// The DTO default. At Stage 2 this is served as an alias of
    /// <see cref="Code"/>; Stage 3 will run both the code and prose plans
    /// and merge the results.
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
    /// <remarks>
    /// Not implemented at Stage 2: a request with
    /// <see cref="QueryMode.Prose"/> returns
    /// <see cref="IndexedErrorCode.NotImplemented"/>. Callers should use
    /// <see cref="Code"/> explicitly until the Stage 3 extractor ships.
    /// </remarks>
    [JsonStringEnumMemberName("prose")]
    Prose = 2,
}
