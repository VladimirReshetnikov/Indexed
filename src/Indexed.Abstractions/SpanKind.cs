using System.Text.Json.Serialization;

namespace Indexed.Abstractions;

/// <summary>
/// Classifies the content category of a <see cref="Match"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every match returned by the Indexed service carries a kind. Code matches
/// come from the raw-byte code index (FTS5 trigram tokenizer). Prose matches
/// come from extractor-produced spans (comments, XML doc comments, Markdown,
/// plain-text) and are tokenized with porter unicode61.
/// </para>
/// <para>
/// The vocabulary is closed — adding a new span category is a schema change
/// handled by the extractor and index layers, not by consumers. The enum is
/// deliberately small so callers can exhaustively <c>switch</c> without a
/// default arm; if an unknown string arrives on the wire, JSON deserialization
/// fails fast rather than silently dropping the match.
/// </para>
/// <para>
/// Wire format: kebab-case JSON strings (for example <c>"xml-doc"</c>,
/// <c>"line-comment-block"</c>).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Filter to XML doc hits only:
/// var req = new SearchRequest("lifetime")
/// {
///     Mode = QueryMode.Prose,
///     KindFilter = new[] { SpanKind.XmlDoc },
/// };
/// </code>
/// </example>
[JsonConverter(typeof(JsonStringEnumConverter<SpanKind>))]
public enum SpanKind
{
    /// <summary>
    /// Raw source bytes indexed as code. Set on every hit served from the code
    /// surface regardless of source language. Carries no <see cref="MatchSpan"/>.
    /// </summary>
    [JsonStringEnumMemberName("code")]
    Code = 0,

    /// <summary>
    /// Content from a Markdown file (<c>.md</c>, <c>.markdown</c>). The entire
    /// file is treated as one span.
    /// </summary>
    [JsonStringEnumMemberName("markdown")]
    Markdown = 1,

    /// <summary>
    /// Content from a plain-text file (<c>.txt</c>, <c>.rst</c>, <c>.adoc</c>).
    /// The entire file is treated as one span.
    /// </summary>
    [JsonStringEnumMemberName("plain-text")]
    PlainText = 2,

    /// <summary>
    /// Tag-stripped prose extracted from a C# XML documentation comment
    /// (<c>///</c>-prefixed block or <c>/** */</c>-style doc comment).
    /// Produced by the Roslyn extractor in <c>Indexed.Extractors</c>.
    /// </summary>
    [JsonStringEnumMemberName("xml-doc")]
    XmlDoc = 3,

    /// <summary>
    /// A contiguous run of single-line comments (<c>//</c>, <c>#</c>,
    /// <c>--</c>, etc.) collapsed into one span. Interior newlines and leading
    /// comment markers are stripped from the indexed content.
    /// </summary>
    [JsonStringEnumMemberName("line-comment-block")]
    LineCommentBlock = 4,

    /// <summary>
    /// A block comment (<c>/* */</c>, <c>(* *)</c>, <c>&lt;# #&gt;</c>, HTML
    /// <c>&lt;!-- --&gt;</c>, etc.) as a single span. Delimiters are stripped.
    /// </summary>
    [JsonStringEnumMemberName("block-comment")]
    BlockComment = 5,
}
