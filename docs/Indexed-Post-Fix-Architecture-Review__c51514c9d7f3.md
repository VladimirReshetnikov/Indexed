# Indexed — Post-Fix Code and Architecture Review

- Created (UTC): 2026-04-16T18:30:00Z
- Repository HEAD: 468c8153b543b26d48008624d31d8ffd6930003d
- Scope: full re-review of `src/Indexed` at HEAD, with emphasis on (a) state of findings from the three prior reviews after the `10b8cf312` and `d4352af68` fix passes, (b) findings that survived or emerged from those passes, and (c) architectural direction for the S3+ horizon.
- Prior reports:
  - [Indexed-Code-Review__4f8a1c3b7e02.md](Indexed-Code-Review__4f8a1c3b7e02.md) (broad 47-finding sweep)
  - [Indexed-PR1-3-Code-Review__7c41a290d68f.md](Indexed-PR1-3-Code-Review__7c41a290d68f.md) (PR 1–3 targeted)
  - [Indexed-Code-and-Architecture-Review__c0883d924cbd.md](Indexed-Code-and-Architecture-Review__c0883d924cbd.md) (contract-truthfulness pass)

## Executive summary

The `Address code review findings` commit pair (`10b8cf312`, `d4352af68`) closed the overwhelming majority of prior findings. Verified fixes include: HeadPoller's `.git/index` canary was removed (H1 → closed), `DaemonClient` now does a two-phase PID/start-time liveness probe before deleting `daemon.json` (H2 → closed), the DTO surface was reconciled against runtime reality by aligning documentation and rejecting unsupported options up front (H3, M1 → closed), freshness now folds `IsProcessingBatch` (M2 → closed), `CodeQueryExecutor` enqueues `FileDeleted`/`FileChanged` by classified status (M3, PR1-3 M2 → closed), `DebouncingEventQueue` coalesces `ReconciliationRequested` and enforces `_maxBatchSize` across the whole emitted batch (M4 → closed), `FileContentProvider` gained path-traversal defense and returns a classified `FileReadOutcome` (M5, PR1-3 H1/H2 → closed), and `IndexLimits.MaxIndexableFileBytes` is now a single source of truth (PR1-3 M4 → closed).

After this pass, Indexed is in materially better shape than either prior review found it. The remaining surface is narrower and mostly about **observability, error-code taxonomy, administrative fast paths, and disciplined memory-model usage**. This review surfaces 12 findings: 0 high, 5 medium, 5 low, 2 nits, plus 5 architecture-level proposals. None block a Stage-3 cut.

## Status of prior-review findings at HEAD

### Main (c0883d924cbd) — all closed

| ID | Finding | Status at HEAD |
|----|---------|----------------|
| H1 | `.git/index` mtime canary can miss HEAD moves | **Closed.** `HeadPoller.OnTick` now calls `GetHeadSha()` every tick; the historical shortcut is gone with a pointed comment explaining why. |
| H2 | `DaemonClient` deletes `daemon.json` after a transient 2 s probe timeout | **Closed.** `DaemonClient.PingWithLivenessAsync` retries with a 10 s window and verifies PID/StartTime before deciding the daemon is gone. |
| H3 | Public request contract accepts options the runtime ignores | **Closed** (by rejection, not by implementation). `SqliteSearchBackend` treats `Auto` as `Code`, rejects `Prose` with `not-implemented`, rejects `SortBy.Relevance`, and `CodeQueryExecutor.KindFilterIncludesCode` short-circuits empty-result filters. |
| M1 | Response contract semantics drift from runtime | **Closed.** `SearchResponse.TotalMatches`, `SearchRequest.TimeoutMs`, and `Match.ByteOffset` now document the Stage-2 reality precisely (ByteOffset as historical-name, TotalMatches as lower-bound, timeout as no-partial). |
| M2 | Freshness reports `isStale=false` while a batch is being committed | **Closed.** `IncrementalIndexer.IsProcessingBatch` folds `_batchesInFlight` into `BuildFreshness()`. |
| M3 | Query-time repair enqueues `FileChanged` for missing files | **Closed.** `FileReadOutcome.Missing` → `FileDeleted`; other outcomes → `FileChanged`. |
| M4 | `DebouncingEventQueue` does not coalesce globals and ignores `maxBatchSize` for them | **Closed.** `ContainsReconciliation` coalesces, `Flush` applies the cap before and during per-path emission. |
| M5 | `FileContentProvider` lacks containment and still turns invalid paths into 500s | **Closed.** `Path.GetFullPath` canonicalization + case-aware prefix check on Windows; every documented exception class now returns a classified outcome. |
| L1 | `WriterScope` double-dispose releases writer lock twice | **Closed.** Idempotent dispose via `Interlocked.Exchange(ref _disposed, 1)`. |

### Broad (4f8a1c3b7e02) — critical/high bucket

| ID | Finding | Status |
|----|---------|--------|
| C1 | `SqliteIndex` writer connection accessed without holding the writer lock | **Closed.** Private-cache `_syncReader` serves sync reads; `GetMeta`/`TryGetShaByPath`/`LookupFileIdByPath` go through the dedicated reader guarded by `_syncReaderGate`. |
| C2 | `BindListener` TOCTOU port race | **Closed.** 3-attempt retry loop with a re-discovery of the ephemeral port per attempt; final attempt's exception propagates to `StartAsync`. |
| H1 | `GetDiffTree` parser desynchronizes on `U` status | **Closed.** `case 'U'` produces `DiffStatus.Unmerged` with the correct 2-field advance. (`B`/`X` are not explicitly handled but the `default` case still skips one field, which desynchronizes if they appear — see **N1** below.) |
| H2 | `OutputFormatter` context-line numbering | **Closed.** `beforeLine` and `afterLine` are computed relative to `match.Line` and incremented correctly. |
| H3 | No request concurrency limit | **Closed.** `_requestGate` with capacity 8. (See **M1** below for the remaining starvation edge.) |
| H4 | No request body size limit on `/search` | **Closed.** `LengthLimitingStream` caps the body at 64 KB. |
| H5 | `IsFaulted` set on normal shutdown | **Closed.** `queueDrained` bool distinguishes `Complete()` exit from exception exit; `IsFaulted` is only set on the latter. |
| H6/H7 | Zero tests for `MatchExtraction` / `SqliteSearchBackend` validation | **Closed.** `MatchExtractionTests` and `SqliteSearchBackendTests` exist. |

### PR1-3 (7c41a290d68f) — all P0/P1/P2 closed

| ID | Finding | Status |
|----|---------|--------|
| H1/H2 | `FileContentProvider` path traversal / `ArgumentException` | **Closed** (see M5 above). |
| M1 | Disposal flag check-and-set not atomic | **Closed.** Both `IndexOptimizer` and `IncrementalIndexer` use `Interlocked.Exchange(ref _disposed, 1) == 1`. |
| M2 | `FileChanged` vs `FileDeleted` for missing | **Closed** (see M3 above). |
| M3 | `IndexOptimizer.DisposeAsync` unbounded final merge | **Closed.** `DefaultShutdownTimeout = 10 s`; bounded gate + merge. |
| M4 | `MaxIndexableFileBytes` duplicated | **Closed.** `IndexLimits.MaxIndexableFileBytes`. |
| L1 | No direct `FileContentProvider` tests | **Closed.** `FileContentProviderTests.cs` exists. |
| L2 | Unbounded reconciliation events | **Closed.** (see M4 above.) |
| L6 | `DaemonInfo.TryDelete` bare catch | **Closed.** Narrow catch list (`IOException`, `UnauthorizedAccessException`, `ArgumentException`, `NotSupportedException`). |

### Still open from prior reviews

| From | ID | Finding | Note |
|------|----|---------|------|
| Broad | L9 | `Match.ByteOffset` name carries UTF-16 char offset, not byte offset | Documented rather than corrected — see **M2** below. |
| Broad | M11 | `/shutdown` 403 uses `IndexedErrorCode.Unavailable` | Still returns `unavailable` on bad token — see **M3** below. |
| Broad | L18 | `TimeoutExceeded` maps to HTTP 504 rather than 408 | No change; debatable and low impact. |
| Broad | L15 | `GitProcess` retry uses `Thread.Sleep` | Still sync; CT-unaware — see **L2** below. |
| Broad | M1 (M1) | `StandardOutputEncoding` + `BaseStream` in `RunBytesCore` | Not verified corrected; see **L3** below. |
| PR1-3 | L5 | Three serialized writer scopes per batch | Still three; see **L1** below. |
| PR1-3 | L7 | `SqliteIndex.DisposeAsync` reader-lock / wal_checkpoint have no deadline | Still open — see **M4** below. |
| PR1-3 | N4 | `/status` does not surface optimizer counters | Partially addressed (`OutputFormatter.WriteStatusText` prints `merges` when present) — see also architecture proposal §3. |

## New or evolved findings

### Medium (5)

#### M1. `/status` can still be queued behind long `/search` requests on the 8-slot gate

**File:** `src/Indexed/src/Indexed.Service/DaemonHost.cs:204-239` (request loop + `_requestGate`), `DaemonHost.cs:352-415` (`HandleRequestAsync` takes the gate before dispatching to any endpoint).

The prior H2 fix in `DaemonClient` is a good defensive layer: a slow `/status` no longer causes the CLI to delete `daemon.json` and orphan the daemon. But the server-side symptom — that `/status` competes with `/search` for the 8-slot `_requestGate` — is still present. Under sustained 8-concurrent `/search` load each holding SQLite read connections, an administrative probe can wait seconds. That's harmless with the current CLI; it becomes a real availability issue when Stage 3 adds prose queries that are substantially slower than code queries, or when an agent is holding open 8 long-running searches and an operator tries to `idx status` / `idx stop`.

**Proposed fix:** split administrative endpoints (`/status`, `/shutdown`, `/rescan`) from data endpoints (`/search`). Either:

1. a second `SemaphoreSlim` with capacity 2 for admin requests, or
2. no gate at all for `/status` + `/shutdown` (both are constant-time paths that do not contend for SQLite or CPU), leaving the existing gate exclusively for `/search`.

Option 2 is the cleaner change because `/status` should never allocate a long-lived resource.

#### M2. `Match.ByteOffset` is documented-but-misleading on the wire

**Files:** `src/Indexed/src/Indexed.Abstractions/Match.cs:17-25,46-50`, `src/Indexed/src/Indexed.Core/CodeQueryExecutor.cs` (hit population).

The field is a 0-based UTF-16 character offset, not a byte offset. The XML docs now explain this and flag the Stage-3 plan to either add a new field or re-semanticize under the same name. The prior reviews flagged it, and it was resolved by documentation. That is **correct for the current stage** but accumulates a trap for anyone who uses `byteOffset` to skip into a mmap'd file — a perfectly reasonable consumer pattern — and ends up off by a factor of 2 on UTF-16 files or by however many bytes the multi-byte prefix took on UTF-8.

**Proposed fix (pick one path; do not re-document forever):**

1. **Rename the DTO field now, before external consumers bake the name in.** The DTO is still stage-local (there is no released external CLI/agent contract that would break), and renaming `byteOffset` → `charOffset` on the wire is a one-line JSON rename plus one comment update. Cost: a DTO tweak. Benefit: no future surprise.
2. **Keep the wire name and produce the true UTF-8 byte offset.** This requires carrying the raw bytes (not the decoded string) through `MatchExtraction`, or a second decode pass that computes UTF-16 → UTF-8 byte positions. More work, and Stage 3 has an extractor pipeline that will change how content is materialized anyway.

The **architectural** version of this finding is: Stage-2 deferred a real DTO decision by renaming the semantics rather than the field. If Indexed does cut a 1.0 wire contract before Stage 3, this should be resolved first.

#### M3. Shutdown 403 uses the wrong error code

**File:** `src/Indexed/src/Indexed.Service/DaemonHost.cs:433-450`, `src/Indexed/src/Indexed.Abstractions/IndexedErrorCode.cs` (enum missing `Forbidden`).

When a `/shutdown` request arrives with a missing or bad `X-Indexed-Shutdown-Token`, the daemon returns HTTP 403 with `{code: "unavailable", message: "shutdown token missing or invalid"}`. HTTP 403 semantics are "I know who you are and I'm denying the action"; the body code `unavailable` suggests "busy or down, retry". These contradict each other.

This is not a vulnerability (the token check is correct), but it is a contract-integrity problem: an agent looking at `error.code` to decide whether to retry should not retry an authentication failure, and `unavailable` normally **is** retryable.

**Proposed fix:** add a new enum value:

```csharp
[JsonStringEnumMemberName("forbidden")]
Forbidden = 7,
```

Add `IndexedErrorCode.Forbidden => 403` to `MapErrorCodeToHttp` and use it at the shutdown-token check. This is a low-risk additive change — the `JsonStringEnumConverter` remains forward-compatible because the enum is a closed set already acknowledged by docs as "new codes may be added in minor releases".

#### M4. `SqliteIndex.DisposeAsync` can still hold shutdown indefinitely if a reader leaks

**File:** `src/Indexed/src/Indexed.Core/SqliteIndex.cs` (dispose path).

The prior reviews noted that `_readerLock.WaitAsync()` on the shutdown path has no deadline, and the final `PRAGMA wal_checkpoint(TRUNCATE);` runs on a background thread with a 5 s shield. That's a good improvement, but the `_readerLock.WaitAsync()` itself is still unbounded. Under normal operation the HTTP listener stops first so no new leases are requested — but any bug that leaks a reader (e.g., a future `await` path that forgets to return via `IAsyncDisposable`) will wedge `SqliteIndex` dispose until process exit.

**Proposed fix:** accept a shutdown deadline on `SqliteIndex.DisposeAsync` and pass it to `_readerLock.WaitAsync(ct)`. If it elapses, log and proceed — leaked readers will be torn down by CLR finalization when the process exits, which is acceptable for a shutdown path.

Optional belt-and-braces: have `RentReaderAsync` return an `IAsyncDisposable` that fails fast (no-op) if `_disposed == 1`, so callers cannot acquire a reader after dispose starts.

#### M5. `_lastKnownHead` lacks explicit memory ordering

**File:** `src/Indexed/src/Indexed.Core/HeadPoller.cs:37-45,112-122`.

`_lastKnownHead` is a `string?` field written by the timer-thread callback and read by request threads via `LastKnownHead`. Reference reads are atomic on the CLR memory model, so a reader cannot observe a torn reference — but without `Volatile.Read` / `Volatile.Write` there is no happens-before edge, so a reader may observe a stale value for an unbounded time on weak-memory architectures (ARM64). In practice the timer runs at 1 Hz so "unbounded stale" resolves in ≤1 s, but the invariant is nowhere in the code.

**Proposed fix:**

```csharp
public string? LastKnownHead => Volatile.Read(ref _lastKnownHead);
// and on writes:
Volatile.Write(ref _lastKnownHead, currentHead);
```

Both sides are cheap. The same reasoning applies to `HeadPoller._consecutiveErrors` (read by `LastError` via plain access if added; currently internal only). The cost is two lines and documents the concurrency contract that is currently only inferred.

### Low (5)

#### L1. `IncrementalIndexer.ProcessBatchAsync` opens up to three writer scopes per batch

**File:** `src/Indexed/src/Indexed.Core/IncrementalIndexer.cs:183-...` (delete scope, upsert scope, `indexed_head` meta-write scope).

Previously called out in PR1-3 L5. Each scope is a `BEGIN IMMEDIATE / COMMIT` round-trip under the writer lock. On a HEAD-movement batch with both deletes and upserts, the meta write for `indexed_head` could ride along in the upsert scope without violating the "deletes apply even if upserts fail" property (the meta write only runs on success anyway). That saves one writer-lock acquire + one commit fsync per batch.

**Proposed fix:** move the `indexed_head` `SetMeta` call to the end of the upsert scope; fall back to its own scope only when the upsert list is empty (pure-delete batch).

#### L2. `GitProcess` retry-on-index-lock uses `Thread.Sleep` and ignores cancellation

**File:** `src/Indexed/src/Indexed.Git/GitProcess.cs:111`.

The 4-retry exponential back-off (`100 ms → 800 ms`) uses `Thread.Sleep(delay)` inside an otherwise async-friendly surface. The calling path is a pool thread, so this parks a worker for up to ~1.5 s cumulative even under cancellation. The retry loop also cannot observe the caller's `CancellationToken` during the sleep — a Ctrl+C during a lock-contention window delays shutdown by up to 800 ms.

**Proposed fix:** swap to `await Task.Delay(delay, cancellationToken).ConfigureAwait(false);` inside the retry loop, propagating `OperationCanceledException`. The method is already `async`, so there is no awkward sync-context plumbing.

#### L3. `GitProcess.RunBytesCore` still sets `StandardOutputEncoding`

**File:** `src/Indexed/src/Indexed.Git/GitProcess.cs` (process setup).

Prior broad M1: setting `StandardOutputEncoding = Encoding.UTF8` creates a `StreamReader` wrapper that may pre-buffer from the pipe, while `RunBytesCore` then reads from `process.StandardOutput.BaseStream` — risking a torn read of the first few bytes if the reader drained any. In practice the drain has not happened because `RunBytesCore` reads before any call to `StandardOutput` properties, but the invariant is fragile and the fix is free.

**Proposed fix:** gate `StandardOutputEncoding` behind the `RunText` path only. For `RunBytesCore`, leave `StandardOutputEncoding` at its default — the pipe is read as raw bytes via `BaseStream`.

#### L4. `ExcludeFilter` scan is linear; large exclude lists pay O(N·M) per file

**File:** `src/Indexed/src/Indexed.Core/ExcludeFilter.cs`.

Each `IsExcluded` call evaluates every glob's compiled `Regex` against the path. For the default-only set (17 globs) this is harmless; once a user adds a long `--exclude-index` list (lockfile mirrors, large vendor trees), the scan becomes an inner loop of `FullScanIndexer` and `IncrementalIndexer`.

**Proposed fix (not urgent):** partition globs into two buckets at compile time — globs that are pure suffixes (`*.min.js`, `*.map`) and compound globs. Check the suffix bucket with a trie or a sorted array of interned suffix strings before the regex loop. Keep the general regex scan as a fallback.

#### L5. `DebouncingEventQueue.PendingCount` reads dictionary/list `Count` without synchronization

**File:** `src/Indexed/src/Indexed.Core/DebouncingEventQueue.cs:73`.

```csharp
public int PendingCount => _pendingPaths.Count + _pendingGlobal.Count;
```

`Dictionary<,>.Count` and `List<>.Count` are plain int reads, so no tearing. But the dictionary's internal bucket array is mutated by the single consumer and observed by concurrent request threads without a memory barrier. The usual argument (consumer-only mutation) does not license **arbitrary** concurrent reads of non-atomic state in the dictionary implementation; `Count` happens to be just the entry count stored as `int`, which is safe, but this is only true by implementation accident.

**Proposed fix:** keep a separate `int _pendingCount` bumped on `Enqueue`/`Absorb`/`Flush` via `Interlocked.Increment/Decrement`. Expose it via `Volatile.Read`. This detaches `PendingCount` from the internal state of the collection and matches the pattern used for `_batchesInFlight` in `IncrementalIndexer`.

### Nits (2)

#### N1. `GetDiffTree` default case silently skips unknown status characters

**File:** `src/Indexed/src/Indexed.Git/GitRepository.cs:407-409` (default arm of the switch).

The `U` (unmerged) fix is in, but `B` (broken pairing) and `X` (unknown; used when a diff-tree driver does not know a format) still fall through to the default `i++` skip. Git documentation says `B`/`X` can appear in `diff-tree` output when `--find-copies-harder` is combined with broken renames or when a custom diff driver returns an unknown status. If they appear in real output, the parser desyncs the same way the old `U` code did.

**Proposed fix:** treat `B` like `R` (two-field `old -> new`), and treat `X` as "skip one field, warn" rather than default-skip. Add a log warning any time the default arm is hit.

#### N2. `DaemonHost.MapErrorCodeToHttp` default-returns 500 for unknown values

**File:** `src/Indexed/src/Indexed.Service/DaemonHost.cs:621-631`.

The switch's `_ => 500` is right for forward-compat, but if `IndexedErrorCode.Forbidden` is added per M3 it must be wired here too — easy to forget. Consider expressing the table as a `Dictionary<IndexedErrorCode, int>` with a completeness check (`Debug.Assert(all enum values present)`) in Release the assert is stripped, but the dictionary catches the editor-miss the next time.

## Architecture-level proposals

### A1. Split `/search` and `/admin` dispatch paths

The daemon today routes every HTTP request through a single `_requestGate` and a single handler (`HandleRequestAsync`). That works for Stage 2 where all endpoints have similar cost profiles, but `/status`/`/shutdown`/`/rescan` are inherently different: they are administrative, they do not consume the SQLite reader pool, and they should be immune to starvation by search traffic.

A small refactor:

```csharp
private readonly SemaphoreSlim _searchGate = new(8, 8);

// In HandleRequestAsync:
if (IsAdminEndpoint(path)) {
    await HandleAdminAsync(context).ConfigureAwait(false);
    return;
}
await _searchGate.WaitAsync(ct).ConfigureAwait(false);
try { await HandleSearchAsync(context, ct).ConfigureAwait(false); }
finally { _searchGate.Release(); }
```

This is additive, backward-compatible, and makes M1 above a one-line change.

### A2. Promote the daemon discovery/control-plane types into a shared project

`Indexed.Cli` currently references `Indexed.Service` purely to read `DaemonInfo`, `DaemonPaths`, and `RepoId`. That couples the client to server implementation details — any future protocol change drags `Indexed.Cli` along for the ride, and makes it harder to ship the CLI as a standalone tool that could adopt an already-running daemon built from a different SHA.

A new project `Indexed.Protocol` (or `Indexed.Discovery`) owning:

- `DaemonInfo` (`Port`, `ShutdownToken`, `Pid`, `StartedAt`)
- `DaemonPaths.ForRepo(string repoRoot, string? appData)`
- `RepoId.Compute(...)`

would let `Indexed.Cli` reference only `Indexed.Abstractions` + `Indexed.Protocol`, and `Indexed.Service` remain the sole owner of `DaemonHost`/`DaemonOptions`/`Program`. The CLI then has no transitive dependency on the HTTP framework or SQLite. This matches the "clean up the client/server boundary" proposal from the prior review but scoped to a concrete refactor.

### A3. Surface operator-relevant counters in `/status`

The daemon already collects several live counters that do not reach `StatusResponse`:

- `IndexOptimizer.MergeCount`, `LastMergeAtUtc`, `LastMergeElapsedMs` (partially surfaced).
- `IncrementalIndexer._batchesInFlight`, total batches committed (not tracked yet).
- FSW error counts (`_consecutiveErrors` in both `RepoWatcher` and `HeadPoller`).
- `GitProcess` invocation counts / retry-contention counts (not tracked).
- SQLite reader pool depth and wait-times.

A `StatusResponse.Metrics` sub-object with these fields is cheap and makes the daemon self-diagnostic. Agents can detect "the indexer is falling behind" (batch depth trending up) or "git is contended" (retry counts climbing) without parsing logs. This is a net architectural win and does not need to wait for Stage 3.

### A4. Disentangle "staleness" from "work in flight"

`Freshness.IsStale` today conflates three distinct conditions:

1. `indexedHead != currentHead` (HEAD moved but not yet patched).
2. `_eventQueue.PendingCount > 0` (debounced events waiting).
3. `_incrementalIndexer.IsProcessingBatch` (batch mid-commit).

Consumers cannot tell from a single boolean which of the three is true, and the three have very different half-lives (HEAD-move divergence may be seconds; PendingCount is milliseconds; in-flight is typically tens of ms). For a human reading `idx status`, one boolean is fine. For an agent deciding whether to retry a search that missed, it matters.

**Proposed evolution:** add three numeric staleness indicators to `Freshness` alongside the boolean:

```csharp
public sealed record Freshness(
    string? IndexedHead,
    string? CurrentHead,
    bool IsStale,
    int PendingFileCount,
    int BatchesInFlight,          // new
    long HeadDivergenceMs,        // new: how long HEAD has been ahead
    string? Note);
```

The boolean stays for compatibility; the numeric channels let callers make better decisions.

### A5. Establish a monotonic sequence token for cross-component ordering

Today the pipeline has several ad-hoc "has X been applied yet" signals: `_lastKnownHead` in `HeadPoller`, `PendingCount` in `DebouncingEventQueue`, `_batchesInFlight` in `IncrementalIndexer`, `indexed_head` in SQLite. Each component uses its own idiom and its own memory-ordering story.

A single monotonic `long _indexSequence` that is incremented under the writer lock after each commit — exposed via `SqliteIndex.CurrentSequence` and included in both `StatusResponse` and `SearchResponse.Freshness` — would give callers a single "am I looking at data at least as fresh as sequence N" test. Stage 3 will almost certainly want this for agentic callers that want to trigger a retry only after the index has moved. Cheap to add now; wiring it through later means revisiting every DTO.

## Tests and observability gaps

### Tests still worth adding

- `DaemonClient` liveness-probe tests — mock the HTTP handler and simulate: (a) fast `/status` succeeds, (b) slow `/status` followed by alive PID, (c) `/status` failure + dead PID, (d) `/status` failure + alive PID with mismatched `StartedAt`. Current coverage is indirect.
- `DaemonLauncher.ResolveServiceExecutable` tests — env-var override, sibling layout, build-tree climb; easy mock via `IFileSystem` seam.
- `MapErrorCodeToHttp` theory test — one `InlineData` per enum value + unknown. Prevents the M3/N2 wiring miss from regressing.
- `GitProcess` timeout test — starts a stalled git subprocess (e.g., `git reset` on a locked index) and asserts bounded wall-clock behavior + CT responsiveness. Currently no such test exists.
- `RepoWatcher` FSW-error path — `InternalsVisibleTo` + direct call to `OnError` asserts the single `ReconciliationRequested` enqueue. Prior review M4 coalesces them, but the watcher's role in triggering a single event is untested.

### Property tests worth adding

- `TrigramAnalyzer` superset invariant over random regex + corpus pairs. This is high-ROI because a false-negative in the trigram planner is silent — the index answers "no matches" when a true match exists.
- `PathGlob.Matches` vs a reference `fnmatch` — the `**/` vs bare `**` split still has gitignore-semantics drift (see prior review L-level). A property test surfaces that directly.
- `MatchExtraction` round-trip — for a random `(content, offset)` pair, assert `LineTextAt(LineAndColumnOf(offset).Line)` contains the character at `offset`. CRLF/UTF-16/UTF-8-with-BOM corpora.

### Observability

In operator mode (`idx status`), the current text view is already good. Three additions would close the remaining gaps:

- `batchesInFlight` (see A4).
- `mergesFailed` (currently only `mergeCount` is surfaced).
- `lastReconciliationAtUtc` and `lastReconciliationDurations` (helps answer "did the 5-min safety-net sweep run recently").

None require protocol-breaking changes: all land as new optional fields on `StatusResponse`/`Freshness`.

## Prioritized recommendations

| Priority | Item | Effort | Risk |
|---------|------|--------|------|
| P1 | M1 — split admin from search gate | ~20 lines + test | Low |
| P1 | M3 — add `IndexedErrorCode.Forbidden`, use for shutdown 403 | ~10 lines + test | Minimal |
| P1 | M5 — `Volatile.Read`/`Volatile.Write` for `_lastKnownHead` | 2 lines | Minimal |
| P2 | M4 — shutdown deadline for `SqliteIndex.DisposeAsync` | ~15 lines | Minimal |
| P2 | L2 — async-aware `GitProcess` retry (`Task.Delay` + CT) | ~10 lines | Low |
| P2 | L1 — collapse `indexed_head` meta-write into upsert scope | ~10 lines | Low |
| P2 | A3 — expose `MergeCount`/`BatchesInFlight`/`lastReconciliation*` in `/status` | ~30 lines | Minimal |
| P3 | M2 — DTO decision: rename `byteOffset` or implement true UTF-8 offset | 1 line rename or 2 h redesign | Low or Medium |
| P3 | A1 — admin/search endpoint split (same change as M1 but also protocol-evolution) | 20 lines | Low |
| P3 | A2 — `Indexed.Protocol` shared project for discovery types | 30 lines + sln edit | Low |
| P4 | N1 — `B`/`X` status handling in `GetDiffTree` | 10 lines | Minimal |
| P4 | L3 — drop `StandardOutputEncoding` from `RunBytesCore` | 2 lines | Minimal |
| P4 | L4 — suffix bucket in `ExcludeFilter` | ~40 lines | Low |
| P4 | L5 — atomic counter for `DebouncingEventQueue.PendingCount` | ~15 lines | Minimal |
| P5 | A4 — split staleness booleans into numeric channels | 20 lines DTO + 1 test | Low |
| P5 | A5 — monotonic sequence token | 40 lines across core | Low |

## Verification

- `git rev-parse HEAD` = `468c8153b543b26d48008624d31d8ffd6930003d`.
- Prior-review status matrix cross-checked against current source at HEAD: every fix attributed to `10b8cf312`/`d4352af68` was verified by inspecting the current code for the named mitigation (see inline evidence in each status row).
- No code changes proposed are load-bearing for Stage-3 cut. Stage 3 depends on prose-mode implementation (`QueryMode.Prose`), extractor pipeline (XML docs / Markdown), and BM25 `SortBy.Relevance` — none of which are findings in this review.

## Notes to future reviewers

1. The next review should start by re-running the matrix in "Status of prior-review findings" and regenerating it — the prior reviews were thorough enough that "what remains open" is the natural entry point.
2. The three prior reports + this one overlap significantly. If this becomes a recurring activity, consider a single rolling doc with a "history" section at the bottom rather than four chronological files. CLAUDE.md's "current-state, not change-log" rule argues for that.
3. `CLAUDE.md` mandates provenance metadata (`Created (UTC)`, `Repository HEAD`) on every new doc and a 12-hex content-hash suffix when name collisions are likely. Both apply here.
