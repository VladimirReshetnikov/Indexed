using System;
using System.Text.Json.Serialization;

namespace Indexed.Abstractions;

/// <summary>
/// Index-staleness metadata attached to every search and status response.
/// </summary>
/// <remarks>
/// <para>
/// Indexed makes eventual-consistency explicit: the service never blocks a
/// query waiting for indexing to catch up, and the caller learns whether the
/// response reflects the on-disk state via this block. Callers that require a
/// current-state guarantee should poll <c>GET /status</c> until
/// <see cref="IsStale"/> is <c>false</c>, then re-issue the search.
/// </para>
/// <para>
/// Staleness rule: <c>IsStale = (PendingFileCount &gt; 0) ||
/// (IndexedHead != CurrentHead)</c>. The service computes the value once per
/// response rather than letting callers recompute it — this keeps the
/// definition single-sourced even if the rule evolves.
/// </para>
/// <para>
/// During Stage 1 (ripgrep-backed fallback) <see cref="IsStale"/> is always
/// <c>true</c> and <see cref="IndexedHead"/> is <c>null</c>; the ripgrep backend
/// does not maintain an index. A human-readable <see cref="Note"/> surfaces the
/// reason.
/// </para>
/// </remarks>
/// <param name="IndexedHead">
/// The Git <c>HEAD</c> SHA at the time the most recent full rescan completed.
/// <c>null</c> when no rescan has completed yet or when no index is present.
/// </param>
/// <param name="CurrentHead">
/// The Git <c>HEAD</c> SHA observed while producing this response. <c>null</c>
/// only if the working tree is not a git repository (should never happen for a
/// successfully-started daemon, which requires a repo root).
/// </param>
/// <param name="PendingFileCount">
/// Number of files whose on-disk modifications the indexer has observed but not
/// yet committed to the index. Zero does not imply freshness by itself —
/// <see cref="IndexedHead"/> may still lag <see cref="CurrentHead"/>.
/// </param>
/// <param name="LastFullScanAt">
/// UTC timestamp of the most recent completed full rescan; <c>null</c> when no
/// rescan has completed yet.
/// </param>
/// <param name="IsStale">
/// Authoritative freshness flag. See the class-level remarks for the rule.
/// </param>
/// <param name="Note">
/// Optional free-form explanatory text for debuggability — for example
/// <c>"ripgrep-backed; no index present yet."</c> during Stage 1. Not intended
/// for programmatic parsing.
/// </param>
public sealed record Freshness(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? IndexedHead,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CurrentHead,
    int PendingFileCount,
    DateTimeOffset? LastFullScanAt,
    bool IsStale,
    string? Note = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? IndexedRevisionToken = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CurrentRevisionToken = null,
    RevisionKind RevisionKind = RevisionKind.None,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? LastReconciliationAt = null);
