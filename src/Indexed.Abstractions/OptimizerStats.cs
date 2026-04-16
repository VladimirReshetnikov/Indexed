using System;

namespace Indexed.Abstractions;

/// <summary>
/// Background FTS5 optimizer liveness and performance snapshot, surfaced via
/// <see cref="StatusResponse.Optimizer"/>.
/// </summary>
/// <remarks>
/// <para>
/// These counters let operators and tests confirm that the
/// <c>IndexOptimizer</c> is actually running — a silent optimizer would
/// produce an FTS5 data file that slowly grows 10-25% beyond its
/// merged-steady-state size without any <c>/search</c> regression visible
/// from the outside. Surfacing the counters makes that condition a simple
/// probe against <c>/status</c> rather than a SQLite inspection.
/// </para>
/// <para>
/// Zero-state semantics: on a fresh daemon with no dirty batches yet,
/// <see cref="MergeCount"/> is <c>0</c> and both
/// <see cref="LastMergeAtUtc"/> and <see cref="LastMergeElapsedMs"/> are
/// <c>null</c>. This is distinct from "optimizer disabled" — a disabled
/// optimizer is surfaced by <see cref="StatusResponse.Optimizer"/> being
/// <c>null</c> at the <see cref="StatusResponse"/> level.
/// </para>
/// </remarks>
/// <param name="MergeCount">
/// Total successful merges since daemon start. Monotonic; never decreases.
/// Includes both scheduled ticks and the final shutdown merge.
/// </param>
/// <param name="LastMergeAtUtc">
/// UTC timestamp of the most recent successful merge, or <c>null</c> when
/// no merge has completed yet.
/// </param>
/// <param name="LastMergeElapsedMs">
/// Wall-clock milliseconds spent inside the most recent successful merge,
/// or <c>null</c> when no merge has completed yet. Useful for spotting
/// pathological merge-time growth (expected steady-state is tens of ms on
/// SSDs at the default 512-page budget).
/// </param>
public sealed record OptimizerStats(
    int MergeCount,
    DateTimeOffset? LastMergeAtUtc,
    long? LastMergeElapsedMs);
