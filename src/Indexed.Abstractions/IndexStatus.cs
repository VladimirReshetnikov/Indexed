using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Indexed.Abstractions;

/// <summary>
/// Index-level health and configuration fields returned by <c>GET /status</c>.
/// </summary>
public sealed record IndexStatus(
    long IndexedFileCount,
    long MaxIndexableFileBytes,
    bool InitialScanInProgress,
    IReadOnlyList<string>? IncludeGlobs = null,
    IReadOnlyList<string>? ExcludeGlobs = null,
    IndexSkipStats? Skips = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InitialScanError = null);

/// <summary>
/// Aggregated non-indexable file telemetry.
/// </summary>
public sealed record IndexSkipStats(
    long Total,
    IReadOnlyList<IndexSkipReasonCount> ByReason,
    IReadOnlyList<IndexSkipSample> Samples);

public sealed record IndexSkipReasonCount(string Reason, long Count);

public sealed record IndexSkipSample(
    string LogicalPath,
    string Reason,
    long? SizeBytes,
    string? Detail,
    DateTimeOffset ObservedAt);
