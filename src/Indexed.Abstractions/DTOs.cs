namespace Indexed.Abstractions;

/// <summary>
/// Assembly marker for <c>Indexed.Abstractions</c>.
/// </summary>
/// <remarks>
/// <para>
/// Public DTOs for the Indexed service (search request and response types,
/// <c>Freshness</c>, <c>SpanKind</c>, <c>QueryMode</c>, and the error-response
/// shape) land in Stage 1 per
/// <c>src/Indexed/docs/Indexed-Implementation-Plan.md §1.1</c>.
/// </para>
/// <para>
/// This placeholder exists so the scaffolded project has at least one compile
/// unit during Stage 0; it does not carry any runtime contract and may be
/// removed without a breaking-change notice once real DTOs are added.
/// </para>
/// </remarks>
internal static class AbstractionsAssemblyMarker
{
}
