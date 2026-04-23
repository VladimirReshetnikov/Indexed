using System.Text.Json;
using System.Text.Json.Serialization;
using Indexed.Targets;

namespace Indexed.Abstractions;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for every Indexed DTO
/// exchanged over the HTTP surface.
/// </summary>
/// <remarks>
/// <para>
/// Using the source generator keeps the service and CLI free of reflection-based
/// <c>System.Text.Json</c> code paths, so the assemblies stay trim-safe and
/// AOT-friendly. All request/response and error types below are registered as
/// top-level serializable roots; nested types (for example <see cref="Match"/>
/// inside <see cref="SearchResponse"/>) do not need separate entries but are
/// declared anyway to make the contract explicit.
/// </para>
/// <para>
/// Serializer options embedded in the context:
/// </para>
/// <list type="bullet">
///   <item><description>Property names serialize as camelCase.</description></item>
///   <item><description><c>null</c> values are omitted from output to keep payloads compact.</description></item>
///   <item><description>Property name matching on read is case-insensitive for resilience against hand-authored requests.</description></item>
///   <item><description>Writes are indented only when <see cref="JsonSerializerOptions"/> overrides; the default output is dense.</description></item>
/// </list>
/// <para>
/// Thread-safety: <see cref="JsonSerializerContext"/> derivatives are immutable
/// after construction and safe to share across requests.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var json = JsonSerializer.Serialize(response, IndexedJsonContext.Default.SearchResponse);
/// </code>
/// </example>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SearchRequest))]
[JsonSerializable(typeof(SearchResponse))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(Match))]
[JsonSerializable(typeof(MatchSpan))]
[JsonSerializable(typeof(Freshness))]
[JsonSerializable(typeof(OptimizerStats))]
[JsonSerializable(typeof(QueryMode))]
[JsonSerializable(typeof(RevisionKind))]
[JsonSerializable(typeof(SpanKind))]
[JsonSerializable(typeof(SortBy))]
[JsonSerializable(typeof(TargetKind))]
[JsonSerializable(typeof(TargetRoot))]
[JsonSerializable(typeof(IndexedErrorCode))]
public sealed partial class IndexedJsonContext : JsonSerializerContext
{
}
