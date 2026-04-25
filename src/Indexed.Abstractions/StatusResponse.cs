using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Indexed.Targets;

namespace Indexed.Abstractions;

/// <summary>
/// Response body for <c>GET /status</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>/status</c> is the canonical liveness probe. The CLI also uses it during
/// daemon auto-start to distinguish "no daemon running" (TCP refused) from
/// "daemon running and healthy" (HTTP 200 with this shape) from "stale
/// port-file" (TCP refused after a previous <c>daemon.json</c> wrote a port).
/// </para>
/// <para>
/// This response intentionally never fails for non-fatal reasons: an index
/// that is being rebuilt, a rescan in flight, or a full pending-file queue
/// all produce a valid <see cref="StatusResponse"/> with
/// <see cref="Freshness.IsStale"/> set. Callers consult the freshness block,
/// not the HTTP status, to decide whether to wait.
/// </para>
/// </remarks>
/// <param name="DaemonVersion">
/// Informational version string for the running daemon assembly. Format is not
/// part of the contract; callers should treat it as opaque.
/// </param>
/// <param name="SchemaVersion">
/// The <c>meta.schema_version</c> value of the currently-open index database,
/// or <c>0</c> when no index is present (Stage 1 ripgrep-backed mode).
/// </param>
/// <param name="Pid">Operating-system process ID of the daemon.</param>
/// <param name="RepoRoot">
/// Absolute path to the repository root the daemon is serving, normalized
/// with backslash separators on Windows.
/// </param>
/// <param name="RepoId">
/// Twelve-hex-character repository identifier,
/// <c>SHA1(abspath + "\0" + firstCommitSha)[:12]</c>. Stable across worktree
/// moves; the CLI uses this to locate <c>%LOCALAPPDATA%\Indexed\&lt;repoId&gt;\</c>.
/// </param>
/// <param name="StartedAt">UTC time the daemon process entered its run loop.</param>
/// <param name="Freshness">Current index-staleness snapshot.</param>
/// <param name="Optimizer">
/// Background FTS5 optimizer counters. <c>null</c> when the daemon is
/// running without an optimizer (test backend override, or the
/// configuration explicitly disabled background merges). Absent from the
/// JSON payload when null — old clients that pre-date this field see a
/// response shape identical to what they saw before.
/// </param>
public sealed record StatusResponse(
    string DaemonVersion,
    int SchemaVersion,
    int Pid,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? RepoRoot,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? RepoId,
    DateTimeOffset StartedAt,
    Freshness Freshness,
    OptimizerStats? Optimizer = null,
    IndexStatus? Index = null,
    TargetKind TargetKind = TargetKind.GitRepository,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? TargetId = null,
    IReadOnlyList<TargetRoot>? Roots = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] TargetRoot? PrimaryRoot = null);
