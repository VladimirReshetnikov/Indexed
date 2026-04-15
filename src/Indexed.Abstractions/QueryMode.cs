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
/// and never carry a <see cref="MatchSpan"/>. In <see cref="Prose"/> mode,
/// matches carry the extracted span's kind and a concrete start/end line range.
/// In <see cref="Auto"/> mode both plans run in parallel; when a prose hit and
/// a code hit collide on the same <c>(path, line)</c> the prose hit wins
/// because it carries the richer metadata.
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
    /// Run both code and prose plans and merge the results. This is the default.
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
