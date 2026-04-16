# Indexed — Code and Architecture Review

- Created (UTC): 2026-04-16T18:28:32Z
- Repository HEAD: 468c8153b543b26d48008624d31d8ffd6930003d
- Scope: `src/Indexed` (all projects + docs + tests)

## Executive Summary

Indexed is already in a strong place: it has crisp layering (`Abstractions` → `Git` → `Core` → `Service` → `Cli`), an intentionally minimal dependency footprint, and a pragmatic “derived artifact” stance (WAL, rebuild-on-corruption, single-writer serialization) that matches the product’s goals (fast local code search for agents).

The main review theme is **contract and correctness hardening at the boundaries**:

- Several “correctness” invariants are currently held by convention (or by docs) rather than enforced in code (notably: gitattributes-driven binary exclusion in incremental indexing, `TimeoutMs` vs. regex runtime timeout behavior, and thread-safety of freshness/pending counters).
- Some DTO/XML-doc contract text is ahead of the current stage (Stage 2 uses `mode=auto` as an alias for `code`, but `QueryMode` and `IndexedErrorCode.NotImplemented` docs still describe the future Stage 3 behavior).

Most proposals below are relatively surgical and high-leverage, with explicit test additions suggested for each.

## Evidence: What Was Reviewed

- Source projects:
  - `src/Indexed/src/Indexed.Abstractions`
  - `src/Indexed/src/Indexed.Git`
  - `src/Indexed/src/Indexed.Core`
  - `src/Indexed/src/Indexed.Service`
  - `src/Indexed/src/Indexed.Cli`
- Test projects:
  - `src/Indexed/tests/*`
- Design docs:
  - `docs/Indexed-Architecture.md`
  - `docs/Indexed-Usage-Guide.md`
  - Prior review artifacts under `docs/Indexed-*-Review__*.md`

### Tests

- `dotnet test src/Indexed/Indexed.sln -c Release`
  - Observed one intermittent failure in `Indexed.Core.Tests.HeadPollerTests.HeadMoved_DetectedAndEnqueued` (timer-based flake).
  - Re-runs passed.
  - Recommendation: treat this as a “real” problem until the test is made deterministic; flakes destroy confidence and block CI.

## Architecture Review

### Layering and Ownership

The project structure matches the intent documented in `docs/Indexed-Architecture.md`:

- `Indexed.Abstractions` is dependency-free and declares wire DTOs + source-generated JSON context.
- `Indexed.Git` owns `git.exe` process execution + parsing, and does not “know about” SQLite or indexing.
- `Indexed.Core` owns schema + indexing and query planning/execution.
- `Indexed.Service` owns daemon lifecycle + HTTP routing + freshness projection.
- `Indexed.Cli` owns user UX, daemon adoption/launch, and formatting.

This is a good architecture for a tool that wants to stay nimble and repo-local.

### Concurrency Model

The “single-writer” story is coherent:

- `SqliteIndex.BeginWriteAsync` serializes writers via `_writerLock`.
- Background merges (`IndexOptimizer`) use the same writer gate as `IncrementalIndexer` commits.
- Queries run on reader connections in WAL mode.

Two small mismatches to address:

- `IncrementalIndexer` XML docs still claim it is “the only writer”, but `IndexOptimizer` is also a writer (serialized). See `src/Indexed/src/Indexed.Core/IncrementalIndexer.cs:22`.
- Some write-like operations currently bypass the writer lock by convention (notably `SqliteIndex.SetMeta`). See below.

### API Contract vs Current Stage

Stage 2 behavior is now well-implemented in `SqliteSearchBackend` (reject prose/relevance, alias auto → code), but the DTO docs still present Stage 3 behavior as current truth. This creates “footguns” for agent integrators: they will trust the DTO docs first.

Recommendation: make `Indexed.Abstractions` documentation explicitly stage-aware (without breaking wire shape).

## Findings and Proposals (Ranked)

Severity legend:

- P0: correctness/security risk; could cause wrong results, crashes, or contract breakage.
- P1: high-impact reliability/perf improvements.
- P2: maintainability/doc/UX improvements.
- P3: nits and micro-optimizations.

### P0. `PendingCount` is not thread-safe but is read from HTTP threads

**Evidence**

- `DebouncingEventQueue.PendingCount` reads `_pendingPaths.Count + _pendingGlobal.Count` with no synchronization.
  - `src/Indexed/src/Indexed.Core/DebouncingEventQueue.cs:73`
- `DaemonHost.BuildFreshness()` reads `PendingCount` on arbitrary request threads.
  - `src/Indexed/src/Indexed.Service/DaemonHost.cs` (freshness path around `PendingFileCount: pendingCount`)

**Risk**

`Dictionary<,>` and `List<>` are not safe for concurrent reads during writes. The window is small (writes happen inside `DequeueAsync`), but if `/status` or `/search` hits during that window, you can get:

- Undefined behavior (rare but real).
- Wrong freshness data at best, or crashes at worst.

**Proposal**

Make `PendingCount` *data-race free* by design:

- Maintain an `int _pendingCount` updated in `Absorb`/`Flush` using `Interlocked` (or a lock).
- Or: keep the current structures but guard count reads/writes behind a private lock used only inside the queue.

Also consider renaming semantics: the current value is “pending distinct events (paths + globals)” but DTO `Freshness.PendingFileCount` claims “pending file count”.

**Suggested tests**

- A multithreaded stress test that concurrently calls `Enqueue` and reads `PendingCount` while a consumer runs `DequeueAsync`, asserting no exceptions and monotonic-ish behavior.

### P0. Regex runtime timeouts can bypass `TimeoutMs` and can surface as 500s

**Evidence**

- Regex match timeout is fixed at 5 seconds in planner.
  - `src/Indexed/src/Indexed.Core/CodeQueryPlanner.cs:66`
- Backend maps only `OperationCanceledException` to `timeout-exceeded` and does not handle `RegexMatchTimeoutException`.
  - `src/Indexed/src/Indexed.Service/SqliteSearchBackend.cs:116`

**Risk**

- A pathological regex can run until the regex engine’s timeout and throw `RegexMatchTimeoutException`.
  - Today that escapes as 500 (`internal`) instead of a contract-aligned `timeout-exceeded`.
- A request with `timeoutMs` < 5000ms can still run longer than its budget because cancellation is not observed inside regex matching.

**Proposal**

1. Tie regex engine timeout to request budget:
   - Use `TimeSpan.FromMilliseconds(request.TimeoutMs)` (or a conservative fraction with a minimum floor).
2. Catch `RegexMatchTimeoutException` and map to `IndexedErrorCode.TimeoutExceeded`.
   - Prefer mapping at the backend boundary (`SqliteSearchBackend`) so executor stays “pure”.

**Suggested tests**

- A test with a known exponential regex (bounded) that reliably triggers `RegexMatchTimeoutException`, asserting the HTTP/backend error code is `timeout-exceeded`, not `internal`.
- A test asserting `timeoutMs=50` causes a timeout response even if the regex engine could run longer.

### P0. Incremental indexing ignores `binary` gitattributes, diverging from full scan and docs

**Evidence**

- Full scan checks `.gitattributes` via `GetBinaryAttrPaths()`:
  - `src/Indexed/src/Indexed.Core/FullScanIndexer.cs:95`
- Incremental indexing checks only `IsLikelyBinary` (size + NUL scan), not gitattributes:
  - `src/Indexed/src/Indexed.Core/IncrementalIndexer.cs:266`
- Architecture doc states `git check-attr binary` is part of exclusion logic (current-state).
  - `docs/Indexed-Architecture.md` (“A file is excluded… git check-attr binary…”)

**Risk**

You can end up indexing files explicitly marked as binary (but not caught by the NUL heuristic), causing:

- Index bloat.
- “Garbage” matches in decoded binary content.
- Divergence between “cold rebuild behavior” and “steady-state incremental behavior”.

**Proposal**

Make incremental binary classification respect `binary` attribute:

- Maintain a cached `HashSet<string>` of binary-by-attr paths in `IncrementalIndexer`.
  - Seed once at startup (`EnumerateFiles` → `GetBinaryAttrPaths(files)`).
  - Refresh on reconciliation ticks or when `.gitattributes` changes.
- Or: for each candidate upsert path, run a cheap single-path `git check-attr binary -- <path>` query (slower but correct; acceptable if changes are low volume).

Also: in `FullScanIndexer`, avoid a redundant `ls-files` by passing the already enumerated `files` into `GetBinaryAttrPaths(files, ...)`.

**Suggested tests**

- A test repo with `.gitattributes` marking `*.bin binary`, a small “binary” file with no NUL in first 8 KiB, and a modification event:
  - Assert full scan skips it.
  - Assert incremental updates also skip it.

### P1. `SqliteIndex.SetMeta` bypasses writer lock (and does not attach to caller’s transaction)

**Evidence**

- `SqliteIndex.SetMeta` uses `_writer.CreateCommand()` directly with no `_writerLock` acquisition and no `cmd.Transaction`.
  - `src/Indexed/src/Indexed.Core/SqliteIndex.cs:193`

**Risk**

Right now, this is “safe by convention” because callers tend to call it while a `WriterScope` is held (or during startup). But this is brittle:

- A future caller can call `SetMeta` from a different thread while the incremental writer scope is open and accidentally interleave commands on the same connection.
- The code also *reads* like it might be outside the current transaction even when called inside a `WriterScope` (the underlying provider behavior is subtle and not self-evident).

**Proposal**

- Make meta writes explicit and transaction-safe:
  - Add `SetMeta(WriterScope scope, string key, string? value)` and use `scope.Connection` + `scope.Transaction`.
  - Keep the existing `SetMeta` but implement it as:
    - Acquire a writer scope internally (or take a lock) and perform the write within it.
- In either approach, ensure the method cannot run concurrently with other writer operations on the same connection.

**Suggested tests**

- Concurrency test that performs `BeginWriteAsync` on one task and calls `SetMeta` concurrently, asserting no exceptions and serialized behavior.

### P1. Reconciliation does not detect “missed modify” events, only add/delete drift

**Evidence**

- Reconciliation does `A \\ B` and `B \\ A` over path sets and checks HEAD drift, but does not detect on-disk changes to existing paths.
  - `src/Indexed/src/Indexed.Core/IncrementalIndexer.cs:426-460`

**Risk**

If `FileSystemWatcher` misses “changed” events without firing `Error`, the index can become stale indefinitely for modified tracked files, producing false negatives (new content not searchable).

**Proposal**

Options, in increasing cost:

1. “Stat-based reconcile” on a budget:
   - Pull `(path, mtime, size)` from index, stat the file, enqueue `FileChanged` when mismatch.
   - Do this only on scheduler ticks, or only when FSW error occurred, or on a capped number of files per tick.
2. “Selective sha reconcile”:
   - Only compute SHA for candidates whose mtime/size mismatch.
3. Provide an explicit CLI verb / HTTP endpoint that does a full “mtime reconcile” and document it as the correctness backstop (cheaper than full rebuild).

**Suggested tests**

- An integration test that mutates a file without delivering a watcher event (simulate by not starting `RepoWatcher`, or by writing while watcher is stopped), then triggers reconciliation and asserts the updated content becomes searchable.

### P2. DTO and XML-doc drift: public contract claims Stage 3 behavior in Stage 2

**Evidence**

- `QueryMode` docs claim `Auto` runs “both plans in parallel”.
  - `src/Indexed/src/Indexed.Abstractions/QueryMode.cs:18,36`
- `IndexedErrorCode.NotImplemented` docs claim Stage 2 uses it for `QueryMode.Auto`.
  - `src/Indexed/src/Indexed.Abstractions/IndexedErrorCode.cs:66`
- `DaemonHost` XML docs still describe a pre-Stage-4 lifecycle.
  - `src/Indexed/src/Indexed.Service/DaemonHost.cs:30-35`

**Risk**

Agents and tools that integrate with Indexed will likely read the DTO docs and assume those semantics are implemented, leading to incorrect assumptions, retries, or overly complex integration code.

**Proposal**

Make the docs explicitly stage-aware:

- Add “Stage 2 note” blocks to DTO remarks:
  - `Auto` behaves as `Code` until Stage 3.
  - `Prose` is not implemented until Stage 3.
  - `SortBy.Relevance` is not implemented until Stage 3.
- Update the stale XML docs in `DaemonHost` and `IncrementalIndexer` so code comments match the implemented stage (especially because the repo already treats docs as first-class).

### P2. Truncation semantics + sorting: results are sorted after collection, not selected in sorted order

**Evidence**

- The executor stops scanning when `matches.Count >= request.MaxMatches`, then sorts the accumulated subset and returns.
  - `src/Indexed/src/Indexed.Core/CodeQueryExecutor.cs:165`

**Risk**

This is not “wrong”, but it can surprise callers who interpret `sortBy=path` as “I get the first N matches in path order”. With truncation, the system currently returns “first N matches discovered in candidate iteration order, then sorted”.

**Proposal**

Pick one and document it explicitly:

- Keep current behavior but document “truncation selection is iteration-order dependent”.
- Or: when `sortBy=path`, sort candidate paths before scanning so truncation yields a prefix in that ordering (more intuitive for paging and stable diffs).

### P3. Timer-based tests and timer callbacks are vulnerable to thread-pool contention

**Evidence**

- `HeadPollerTests.HeadMoved_DetectedAndEnqueued` uses real timers + real git processes and has shown flakiness in full-suite runs.
  - `src/Indexed/tests/Indexed.Core.Tests/HeadPollerTests.cs:68-110`

**Proposal**

Make timer-driven components testable without wall-clock sleeps:

- Add a `PollOnce()` method to `HeadPoller` (and similar for `ReconciliationScheduler` if needed), like `IndexOptimizer.RunCycleAsync`.
- In tests, call `PollOnce()` directly.
- Keep timer as a thin wrapper around that method.

## Suggested Fix Order

1. P0: Make `DebouncingEventQueue.PendingCount` thread-safe and reconcile its semantics with `Freshness.PendingFileCount`.
2. P0: Tie regex runtime timeout behavior to `TimeoutMs`, and map `RegexMatchTimeoutException` to `timeout-exceeded`.
3. P0: Ensure incremental indexing respects `binary` gitattributes (consistent with full scan and docs).
4. P1: Make `SqliteIndex.SetMeta` transaction/lock safe by API design.
5. P1: Improve reconciliation to catch missed modifications (even in a capped/budgeted form).
6. P2/P3: Doc drift cleanup and deterministic timer tests.

## Appendix: Notes on Strengths

- Great “derived artifact” approach: rebuild-on-corruption and WAL mode make operational sense.
- Good attention to local security (loopback binding, shutdown token, and now explicit path containment in `FileContentProvider`).
- `RegexTrigrams` implementation is a strong differentiator: it’s the right pragmatic approach for regex on a trigram index.
- The test strategy (real git repos, real sqlite) is the right kind of integration realism for this tool.

