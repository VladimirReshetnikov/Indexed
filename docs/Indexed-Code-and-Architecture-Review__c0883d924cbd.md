# Indexed — Code and Architecture Review

- Created (UTC): 2026-04-16T16:05:17Z
- Repository HEAD: a7855ce7f570c722db69a147a0c8e1736183e081
- Scope: current-HEAD review of `src/Indexed` source projects, tests, and active docs, with emphasis on runtime correctness, contract fidelity, freshness/liveness semantics, daemon lifecycle robustness, and architectural direction.
- Verification:
  - `dotnet test src/Indexed/Indexed.sln`
  - Local git-behavior spot check confirming that `.git/index` mtime does not change on `git reset --soft HEAD~1`

## Executive summary

Indexed is already in good shape structurally. The project split is clean, the tests are substantial, the incremental indexing pipeline is understandable, and the current docs are better than average for a young service. The live problems are concentrated in two areas:

1. freshness/liveness bookkeeping is looser than the API and docs imply; and
2. the public DTO/documentation surface has advanced past what the runtime actually implements.

This review found 8 live findings: 3 high and 5 medium. The highest-priority fixes are:

1. replace the HeadPoller `.git/index` canary strategy;
2. stop deleting `daemon.json` after a single failed probe;
3. make the HTTP/DTO contract truthful for the current implementation stage.

## High findings

### H1. HeadPoller can miss real HEAD moves because it only probes HEAD after `.git/index` mtime changes

**Files:** `src/Indexed/src/Indexed.Core/HeadPoller.cs:93-127`, `src/Indexed/src/Indexed.Git/GitRepository.cs:433-488`

`HeadPoller.OnTick` uses `.git/index` mtime as the sole gate before it runs `git rev-parse HEAD`. That optimization is unsound. HEAD can move without touching the index at all, for example:

- `git reset --soft`
- direct `git update-ref`
- some ref-only operations during advanced workflows

In those cases `HeadPoller` never calls `GetHeadSha()`, never enqueues `HeadMoved`, and leaves `_lastKnownHead` stale indefinitely. The practical consequence is worse than delayed convergence:

- `DaemonHost.BuildFreshness()` trusts `_headPoller.LastKnownHead` first (`DaemonHost.cs:474-479`), so the daemon can report `freshness.isStale = false` while actually serving a different HEAD than the one on disk.
- The incremental indexer never sees the `HeadMoved` event, so the only recovery path is the 5-minute reconciliation sweep.

I verified the blind spot locally: after `git reset --soft HEAD~1`, `.git/index` mtime stayed unchanged, which means the current poller would miss the HEAD move entirely.

**Proposed fix:** stop treating `.git/index` as the authoritative HEAD canary. The simplest correct fix is to read HEAD every tick. If the extra process spawn is considered too expensive, use `.git/HEAD` plus the resolved ref file as the canary set, not `.git/index`.

### H2. DaemonClient can orphan a live daemon by deleting `daemon.json` after a transient 2-second ping timeout

**Files:** `src/Indexed/src/Indexed.Cli/DaemonClient.cs:77-99,165-179`, `src/Indexed/src/Indexed.Service/DaemonHost.cs:57-58,229-239`

`DaemonClient.CreateAsync` reads `daemon.json`, probes `/status`, and deletes the file immediately when the probe fails. That is safe only if "failed probe" means "daemon is dead." In the current service it does not.

`DaemonHost` runs every request, including `/status`, through the shared `_requestGate` with capacity 8. Under load, `/status` can sit behind long-running search requests. `PingAsync` times out after 2 seconds and returns `false`, which makes `CreateAsync` delete `daemon.json` for a daemon that may still be perfectly alive.

That creates a bad failure mode:

1. a live daemon is slow to answer `/status`;
2. the client deletes `daemon.json`;
3. a replacement daemon launch loses the singleton mutex race and never becomes discoverable;
4. the original daemon keeps running, but future clients have no discovery file to adopt it.

This is a serious robustness bug because it turns a transient queueing delay into a broken local control plane.

**Proposed fix:** never delete `daemon.json` after a single failed HTTP probe. Confirm liveness first by combining several signals:

- retry `/status` with backoff and a materially longer timeout;
- verify whether `Pid` is still alive and matches `StartedAt`;
- only remove the discovery file after the process is confirmed dead.

It would also help to reserve a fast lane for `/status` so administrative liveness checks cannot be starved behind search traffic.

### H3. The public request contract is not authoritative: unsupported defaults and options are accepted but not honored

**Files:** `src/Indexed/src/Indexed.Abstractions/SearchRequest.cs:48-105`, `src/Indexed/src/Indexed.Service/SqliteSearchBackend.cs:55-71`, `src/Indexed/src/Indexed.Core/CodeQueryExecutor.cs:95-151`, `src/Indexed/docs/Indexed-Usage-Guide.md:199-215`

The DTOs and usage guide advertise a richer request surface than the runtime actually supports:

- `SearchRequest.Mode` defaults to `QueryMode.Auto` (`SearchRequest.cs:94`), but the backend rejects both `Auto` and `Prose` with `not-implemented` (`SqliteSearchBackend.cs:55-60`).
- `KindFilter` is documented as an active post-retrieval filter (`SearchRequest.cs:48-52`, `Indexed-Usage-Guide.md:207`), but the executor never consults it anywhere in the request path (`CodeQueryExecutor.cs:95-151`).
- `SortBy` is documented as controlling response ordering (`SearchRequest.cs:75-77`, `Indexed-Usage-Guide.md:214`), but the executor never sorts after collecting matches and never branches on `request.SortBy`.

This makes the HTTP surface fragile for direct callers:

- a minimal JSON request that omits `mode` defaults to a mode the service does not implement;
- callers can send `kindFilter` or `sortBy` and receive a seemingly valid response that silently ignores them.

That is a contract-integrity problem, not just a missing feature, because the DTO layer implies the options are supported now.

**Proposed fix:** make the contract truthful for the current stage. Short-term options:

1. set the default mode to `code` until `auto` really works, or map `auto` to `code` while prose is unavailable;
2. reject unsupported `kindFilter` and `sortBy` combinations with `bad-request`;
3. only keep fields in `SearchRequest` that the runtime can currently honor.

## Medium findings

### M1. Response contract semantics do not match runtime behavior

**Files:** `src/Indexed/src/Indexed.Abstractions/SearchRequest.cs:78-82`, `src/Indexed/src/Indexed.Abstractions/SearchResponse.cs:10-25`, `src/Indexed/src/Indexed.Abstractions/IndexedErrorCode.cs:40-42`, `src/Indexed/src/Indexed.Abstractions/Match.cs:17-20,42-45`, `src/Indexed/src/Indexed.Service/SqliteSearchBackend.cs:83-103`, `src/Indexed/src/Indexed.Core/CodeQueryExecutor.cs:135-145,197-205`

Several response-level behaviors are documented one way and implemented another:

- `SearchRequest.TimeoutMs` says timeouts return partial results with `truncated = true` when anything has already been found, but `SqliteSearchBackend` always returns `timeout-exceeded` on timeout.
- `SearchResponse.TotalMatches` says the value is capped at `maxMatches + 1`, but `CodeQueryExecutor` adds the full-file hit count to `total` before enforcing the global cap, so a single dense file can produce arbitrarily larger values.
- `Match.ByteOffset` is documented as a raw byte offset including BOMs, but the executor stores `hit.Index`, which is a UTF-16 character offset. The inline comment in `CodeQueryExecutor.cs:201` explicitly acknowledges the mismatch.

These are not cosmetic doc nits. They change how agents page results, how editors reopen locations, and how callers reason about truncation.

**Proposed fix:** choose one truth source and align everything to it. The least risky short-term path is to update the DTO/docs to the current behavior, then implement the stronger semantics later with tests.

### M2. Freshness can report `isStale = false` while a batch is actively being applied

**Files:** `src/Indexed/src/Indexed.Core/DebouncingEventQueue.cs:73,140-207`, `src/Indexed/src/Indexed.Core/IncrementalIndexer.cs:116-128,150-335`, `src/Indexed/src/Indexed.Service/DaemonHost.cs:472-515`, `src/Indexed/docs/Indexed-Usage-Guide.md:291-296`

`DaemonHost.BuildFreshness()` uses only two dynamic signals:

- `indexedHead != currentHead`
- `_eventQueue.PendingCount > 0`

That misses the in-flight phase. Once `DequeueAsync()` flushes a batch, `PendingCount` drops to zero immediately, but `IncrementalIndexer.ProcessBatchAsync()` may still be reading files, hashing, and writing SQLite for a meaningful amount of time. During that window the daemon can report:

- `pendingFileCount = 0`
- `isStale = false`

even though the index is actively catching up to edits.

For contentless FTS this is important: until the write commits, new trigrams may still be absent from the candidate oracle, so a "fresh" answer can still miss the latest hit.

**Proposed fix:** promote "work in flight" to a first-class freshness signal. The incremental indexer should expose either:

- a boolean `IsBusy`, or
- an outstanding-work counter that includes claimed-but-uncommitted batches.

`freshness.isStale` should include that signal.

### M3. Query-time repair keeps deleted files in the index until reconciliation

**Files:** `src/Indexed/src/Indexed.Core/CodeQueryExecutor.cs:122-132`, `src/Indexed/src/Indexed.Core/IncrementalIndexer.cs:168-170,229-237,384-409`

When a query hits a stale row whose file is now missing, `CodeQueryExecutor` enqueues `new FileChanged(row.Path)`. That is the wrong repair signal for the common "file is gone" case.

`IncrementalIndexer` handles `FileChanged` by trying to upsert. If the file does not exist, it increments `skipped` and leaves the stale DB row in place. The row then survives until a later reconciliation or HEAD move, and every intervening search pays the same useless cycle:

1. stale candidate rowid
2. read row path
3. disk read fails
4. enqueue another `FileChanged`

**Proposed fix:** distinguish missing from unreadable/oversize in `FileContentProvider` and enqueue `FileDeleted` for missing files. A small discriminated return type would make the repair path precise.

### M4. DebouncingEventQueue amplifies reconciliation work and ignores `maxBatchSize` for global events

**Files:** `src/Indexed/src/Indexed.Core/DebouncingEventQueue.cs:149-207`, `src/Indexed/src/Indexed.Service/DaemonHost.cs:422-429`, `src/Indexed/src/Indexed.Core/RepoWatcher.cs:119-125`

`DebouncingEventQueue` treats `ReconciliationRequested` and `HeadMoved` as global events and appends every instance to `_pendingGlobal`. `Flush()` then does:

- `result.AddRange(_pendingGlobal)` first; and only
- applies `_maxBatchSize` while draining per-path events.

That means:

- repeated `/rescan` requests are never deduped;
- repeated FSW errors are never deduped;
- a batch can exceed `maxBatchSize` by an arbitrary amount if the pressure is in global events.

Since each reconciliation does a full git-files vs index-paths diff, this can turn harmless request bursts into repeated expensive full-tree scans.

**Proposed fix:** make idempotent global work idempotent in the queue. `ReconciliationRequested` should be coalesced to one pending bit, and `maxBatchSize` should apply to the whole emitted batch, not just per-path entries.

### M5. FileContentProvider lacks containment hardening and still turns invalid paths into 500s

**Files:** `src/Indexed/src/Indexed.Core/FileContentProvider.cs:67-90`, `src/Indexed/src/Indexed.Core/CodeQueryExecutor.cs:122-131`

`FileContentProvider.ReadAsync` combines `_repoRoot` with `relPath` textually and then opens the result. It never canonicalizes the combined path or proves that it remains under `_repoRoot`. It also does not catch `ArgumentException`, so malformed path values can still bubble out as 500s.

Today the immediate caller passes paths from the index, and the index is normally populated from git-relative paths, so the bug is mostly latent. But this is still a trust-boundary issue:

- a manually seeded/corrupt DB row can escape the repo root;
- a malformed path can turn one bad row into an unhandled request failure instead of a skipped candidate.

**Proposed fix:** canonicalize with `Path.GetFullPath`, enforce a repo-root prefix check, and catch `ArgumentException` alongside the existing I/O exceptions. The method contract already says hostile paths should return `null`, so the behavior change is aligned with the existing API.

## Lower-priority code issues

### L1. WriterScope double-dispose can release the writer semaphore out of order

**Files:** `src/Indexed/src/Indexed.Core/SqliteIndex.cs:897-916`

If `WriterScope.DisposeAsync()` is called twice, the second call sees `_transaction == null` and releases the writer lock again. In the best case that becomes a `SemaphoreFullException`; in the worst case it can release the semaphore after another writer has already acquired it, which breaks the single-writer invariant.

**Proposed fix:** guard disposal with an `Interlocked.Exchange` flag and make repeated disposal a no-op.

## Architecture improvement proposals

### 1. Make `Indexed.Abstractions` truthful for the current stage

The DTO layer should describe the behavior the daemon actually guarantees today, not the behavior planned for later stages. That means:

- default `mode` should not be an unsupported value;
- unsupported filters/sort modes should be rejected explicitly rather than silently ignored;
- `ByteOffset`, timeout behavior, and `TotalMatches` semantics should be aligned across code, docs, and tests.

### 2. Make freshness and liveness state explicit rather than inferred

Right now the daemon infers freshness from queue depth and inferred HEAD state. That is too lossy. A better model is:

- exact current HEAD/ref tracking;
- explicit in-flight indexing state;
- `/status` fast path that is not starved behind general search traffic;
- daemon discovery that uses PID/start-time verification before mutating `daemon.json`.

### 3. Treat maintenance work as coalescible state, not as an append-only event stream

`ReconciliationRequested` is a state bit, not a payload-carrying event. The queue will behave better if it models that explicitly. The same principle can later extend to other coarse maintenance operations.

### 4. Clean up the client/server boundary

The CLI currently depends on `Indexed.Service` types for daemon discovery (`DaemonInfo`, `DaemonPaths`, `RepoId`). That works, but it also couples the client to server implementation details. A small shared daemon-protocol/discovery project would make packaging, publishing, and future protocol evolution cleaner.

## Test additions worth making next

- `HeadPollerTests`: add `git reset --soft` and direct ref-update scenarios.
- `DaemonClient`/`DaemonHost` integration: pin the "live but slow `/status`" path so discovery-file deletion cannot regress back in.
- `SqliteSearchBackend` and `CodeQueryExecutor`: add tests for omitted `mode`, `sortBy`, `kindFilter`, timeout semantics, and `TotalMatches` bounds.
- `FileContentProviderTests`: absolute path, `..` traversal, invalid path characters, and structured miss reasons.
- `DebouncingEventQueueTests`: repeated `ReconciliationRequested` coalescing and batch-size enforcement across global events.

## Suggested fix order

1. H1: replace the HEAD canary strategy.
2. H2 plus M2: harden daemon discovery and freshness so `/status` is trustworthy under load.
3. H3 plus M1: make the DTO/docs match the runtime before more direct HTTP clients depend on them.
4. M3 through M5: tighten self-healing, queue backpressure, and file-read trust boundaries.
