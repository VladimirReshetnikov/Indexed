# Indexed — Code Review Report

- Created (UTC): 2026-04-15T18:00:00Z
- Repository HEAD: 716380dee4f31965bd4b76cc609d215ad14f751c
- Scope: Full codebase review covering all 5 source projects and 5 test projects. Stages 0–2, 4–5 as implemented.

## Executive summary

The Indexed codebase is well-structured with good layering, proper shutdown ordering, sleep-resilient timers, and source-generated JSON. The review identified **47 findings** across all projects: 2 critical, 7 high, 18 medium, 20 low. The most impactful issues are thread-safety gaps in `SqliteIndex` (unlocked writer-connection access), a `GetDiffTree` parser desynchronization on unmerged files, a port-binding TOCTOU race, and a context-line numbering bug in the CLI output formatter.

## Findings by severity

### Critical (2)

#### C1. SqliteIndex: writer connection accessed without holding the writer lock

**Files:** `Indexed.Core/SqliteIndex.cs` — `GetMeta`, `SetMeta`, `TryGetShaByPath`, `LookupFileIdByPath`

These instance methods run SQL commands on the writer connection (`_writer`) without acquiring `_writerLock`. If called concurrently with an active `WriterScope` (which also uses `_writer`), the SQLite connection sees concurrent command execution — `Microsoft.Data.Sqlite` connections are not thread-safe for concurrent commands.

`GetMeta` and `TryGetShaByPath` are called from `BuildFreshness()` (HTTP request thread) and from within `IncrementalIndexer` batches. `SetMeta` is called inside `WriterScope` batches. The race is real: an HTTP `/status` request calling `BuildFreshness` → `GetMeta` while the incremental indexer holds a `WriterScope` will execute two commands on the same connection simultaneously.

**Proposed fix:** Route all reads through reader connections (`RentReaderAsync`), or acquire `_writerLock` around direct writer-connection reads. The cleanest approach: make `GetMeta`/`TryGetShaByPath`/`LookupFileIdByPath` use a reader connection.

#### C2. DaemonHost: port-binding TOCTOU race

**File:** `Indexed.Service/DaemonHost.cs`, `BindListener()` lines 458–469

The method opens a `TcpListener` on port 0 to discover an ephemeral port, immediately stops it, then binds a new `HttpListener` on that port. Between `tcp.Stop()` and `_listener.Start()`, another process can claim the port. The failure mode is a hard crash at startup with no retry.

**Proposed fix:** Retry `BindListener` up to 3 times with a new port each attempt. Alternatively, keep the `TcpListener` alive until `HttpListener` is confirmed started (though this requires careful coordination since both can't bind the same port simultaneously — the real fix is to accept the race and add retry logic).

---

### High (7)

#### H1. GetDiffTree parser desynchronizes on unmerged ('U') status entries

**File:** `Indexed.Git/GitRepository.cs`, `GetDiffTree` switch default case, lines 370–373

The `default` case increments `i` by 1 and skips. For `U` (unmerged) status entries, git emits `U\0path\0` — a two-field record. The parser only consumes one field, causing all subsequent entries to be parsed with shifted field alignment. This silently corrupts the diff result for any repo with merge conflicts.

**Proposed fix:** Handle `'U'` as a two-field entry (like `'A'`/`'M'`/`'D'`), mapping to a new `DiffStatus.Unmerged` variant, or skip with `i += 2`. Also handle `'X'` (unknown) and `'B'` (broken pairing) the same way.

#### H2. OutputFormatter: context-before lines use the match line number

**File:** `Indexed.Cli/OutputFormatter.cs`, lines 34–35

```csharp
foreach (var ctx in match.ContextBefore)
    writer.WriteLine($"{match.Path}-{match.Line}-{ctx}");
```

All context-before lines carry the match's own line number (e.g., `a.cs-10-before1`) instead of their own line numbers (`a.cs-8-before1`, `a.cs-9-before2`). This breaks ripgrep-compatible output that downstream tools parse. The same issue exists for context-after lines (line 39).

The test at `OutputFormatterTests.cs` lines 55–56 encodes this bug — it asserts the wrong line numbers.

**Proposed fix:** Compute context line numbers: `match.Line - match.ContextBefore.Count + index` for before-context, `match.Line + 1 + index` for after-context.

#### H3. No request concurrency limit in DaemonHost

**File:** `Indexed.Service/DaemonHost.cs`, `RunAsync()` line 196

Every incoming HTTP request spawns `Task.Run` with no concurrency limit. A burst of concurrent `/search` requests saturates the thread pool and creates unbounded concurrent SQLite reads.

**Proposed fix:** Add a `SemaphoreSlim` (e.g., capacity 8) around request dispatch. Requests beyond the limit wait or return 503.

#### H4. No request body size limit on /search

**File:** `Indexed.Service/DaemonHost.cs`, `HandleRequestAsync()` lines 296–303

JSON deserialization reads directly from `req.InputStream` with no size cap. A local process can send a multi-gigabyte POST body, causing unbounded memory allocation (denial of service). Although loopback-only, multi-user workstations or compromised processes could exploit this.

**Proposed fix:** Wrap `req.InputStream` in a length-limiting stream (e.g., 64 KB cap — search requests are small JSON).

#### H5. IncrementalIndexer: IsFaulted set incorrectly on normal shutdown

**File:** `Indexed.Core/IncrementalIndexer.cs`, lines 140–148

When the queue returns an empty batch via `Complete()` (normal shutdown), the worker breaks out of the loop and falls through to `IsFaulted = true` (line 145). The condition `if (ct.IsCancellationRequested)` only catches token-driven shutdowns, not `Complete()`-driven ones. This causes `BuildStatus` to report a degraded state after a clean shutdown.

**Proposed fix:** Track whether the exit was due to `Complete()` (empty batch at line 123) vs unexpected stop. Set `IsFaulted = true` only on unexpected exits.

#### H6. MatchExtraction has zero test coverage

**File:** `Indexed.Core/MatchExtraction.cs` — `ComputeLineOffsets`, `LineAndColumnOf`, `LineTextAt`, `ContextBefore`, `ContextAfter`

Every search match's line, column, byte offset, context-before, and context-after are computed by this code. Off-by-one errors here corrupt every search result. CRLF handling, empty files, single-line files, files ending without a newline, and multi-byte UTF-8 characters are all untested code paths.

**Proposed fix:** Add a dedicated `MatchExtractionTests` class with cases for: LF-only, CRLF, bare CR, empty content, single-line (no newline), match at first/last character, Unicode multi-byte characters, context that extends past start/end of file.

#### H7. SqliteSearchBackend validation is untested

**File:** `Indexed.Service/SqliteSearchBackend.cs` — request precondition checks

The backend enforces documented bounds (`MaxMatches` in [1, 10000], `TimeoutMs` in [1, 30000], etc.) but these are exercised only indirectly via a single integration test. An agent sending `MaxMatches: 0` or `TimeoutMs: -1` could get through without a proper error response if the validation code has a gap.

**Proposed fix:** Add unit tests for each validation boundary (0, -1, 10001, 30001, empty pattern, null pattern).

---

### Medium (18)

#### M1. GitProcess: StandardOutputEncoding + BaseStream conflict

**File:** `Indexed.Git/GitProcess.cs`, lines 130–131

Setting `StandardOutputEncoding = Encoding.UTF8` creates a `StreamReader` wrapper that may pre-buffer bytes. `RunBytesCore` then reads from `process.StandardOutput.BaseStream`, which could miss pre-buffered data under scheduling pressure.

**Fix:** Remove `StandardOutputEncoding` from the `RunBytesCore` path (only needed for `RunText`).

#### M2. GitProcess: stderrTask not awaited on timeout/kill

**File:** `Indexed.Git/GitProcess.cs`, lines 173–188

When the process times out or cancellation fires, the method kills the process and throws without awaiting `stderrTask`. The abandoned `Task` may throw `IOException` (from the killed process), producing unobserved task exceptions.

**Fix:** Add `try { await stderrTask; } catch { }` before throwing in the timeout and cancellation paths.

#### M3. GitRepository: no CancellationToken propagation

**File:** `Indexed.Git/GitRepository.cs` — all public methods

None of the `GitRepository` methods accept `CancellationToken`. The underlying `GitProcess.RunBytes` does, but `GitRepository` always passes `default`. This means git processes cannot be cancelled from the repository layer, causing delayed shutdown if git stalls.

**Fix:** Add `CancellationToken ct = default` to public methods and pass through to `GitProcess`.

#### M4. DebouncingEventQueue: PendingCount over-reports

**File:** `Indexed.Core/DebouncingEventQueue.cs`, lines 82–88

`Enqueue` increments `_pendingCount` on every call, but `Absorb` replaces per-path entries. If the same file changes 10 times, `PendingCount` reads 10 but only 1 entry exists. Only corrected on next `Flush()`. This is a status-display bug affecting freshness reporting.

**Fix:** Don't increment in `Enqueue`; instead, compute the count in the `PendingCount` getter as `_pendingPaths.Count + _pendingGlobal.Count` (requires lock or volatile reads of the dictionaries, but these are only mutated by the single consumer).

#### M5. SqliteIndex: BulkDeleteFiles is O(N) individual DELETEs

**File:** `Indexed.Core/SqliteIndex.cs`, `BulkDeleteFiles` method

Each file deletion issues 3 separate SQL commands with individual parameter binding. A large branch switch with thousands of deletions means 3N round-trips.

**Fix:** Batch with `DELETE ... WHERE file_id IN (...)`, chunked at 500-parameter batches per table.

#### M6. FullScanIndexer / IncrementalIndexer: duplicated file-processing logic

**Files:** `FullScanIndexer.cs` lines 125–196, `IncrementalIndexer.cs` lines 219–282

The file reading + SHA comparison + text decode + upsert pipeline is nearly identical. `CompileExcludes` and `IsExcluded` are duplicated verbatim across `FullScanIndexer`, `IncrementalIndexer`, `RepoWatcher`, and `CodeQueryExecutor` (four copies).

**Fix:** Extract a shared `FileProcessor` or `ExcludeFilter` helper class.

#### M7. FullScanIndexer / IncrementalIndexer: GC pressure from ReadAllBytesAsync

**Files:** `FullScanIndexer.cs` line 145, `IncrementalIndexer.cs` line 238

`File.ReadAllBytesAsync` allocates a fresh `byte[]` per file, up to 50 MB. For a full scan of thousands of files, this creates large object heap pressure. Anything above ~85 KB goes on the LOH.

**Fix:** Use `ArrayPool<byte>.Shared.Rent()` for file reads, or stream the SHA computation to avoid loading the entire file into memory at once.

#### M8. DaemonHost: BuildFreshness spawns git rev-parse HEAD per request

**File:** `Indexed.Service/DaemonHost.cs`, `BuildFreshness()` lines 392–394

`GetHeadSha()` spawns a subprocess. Called on every `/status` and `/search` response. Under heavy search load, this means a new `git` process per query. `HeadPoller` already tracks HEAD every second.

**Fix:** Read the cached HEAD from `HeadPoller._lastKnownHead` instead of spawning a new process.

#### M9. DaemonHost: 404 responses have no body

**File:** `Indexed.Service/DaemonHost.cs`, lines 374–375

Unknown routes return a bare 404 with no content type and no body, violating the project's own contract that all non-2xx responses carry an `ErrorResponse` JSON body.

**Fix:** Return a proper `ErrorResponse(IndexedErrorCode.BadRequest, "unknown route: {path}")` with 404 status.

#### M10. DaemonHost: HttpListenerException not logged

**File:** `Indexed.Service/DaemonHost.cs`, lines 187–189

When `HttpListenerException` is caught in the request loop, the daemon silently exits with no diagnostic output.

**Fix:** Log the exception before breaking.

#### M11. DaemonHost: /shutdown 403 uses IndexedErrorCode.BadRequest

**File:** `Indexed.Service/DaemonHost.cs`, line 360

A 403 HTTP status paired with a `"bad-request"` machine code in the JSON body is confusing for clients.

**Fix:** Add `IndexedErrorCode.Forbidden` to the enum, or use `Unavailable`.

#### M12. TextDecoder: UTF32Encoding allocated on every call

**File:** `Indexed.Core/TextDecoder.cs`, `GetUtf32Be()` method

`new UTF32Encoding(bigEndian: true, byteOrderMark: false)` is allocated each time a UTF-32-BE file is decoded.

**Fix:** Make it a `static readonly` field.

#### M13. CliApp: PingAsync conflates cancellation with daemon-not-responding

**File:** `Indexed.Cli/DaemonClient.cs`, `PingAsync` method

Catches `TaskCanceledException` but not `OperationCanceledException`. A Ctrl+C during the ping phase is silently treated as "daemon not responding," causing the CLI to attempt a daemon launch instead of exiting.

**Fix:** Re-throw `OperationCanceledException` when the original cancellation token is canceled.

#### M14. ArgumentParser: no validation of negative integers

**File:** `Indexed.Cli/ArgumentParser.cs`, `ParseInt` lines 194–199

`--max-matches -5` or `--context-before -1` succeeds parsing. The daemon rejects these, but the CLI could give faster, clearer errors.

**Fix:** Add `if (n < 0) throw new ArgumentParseException(...)` in `ParseInt`.

#### M15. GitRepository.GetBinaryAttrPaths spawns redundant git processes

**File:** `Indexed.Git/GitRepository.cs`, lines 268–297

Internally calls `EnumerateFiles()` (2 git processes) + `check-attr` (1 more) = 3 total. Callers who already have the file list pay for redundant enumeration.

**Fix:** Add an overload accepting `IReadOnlyList<string>` to avoid re-enumeration.

#### M16. RepoWatcher has zero test coverage

**File:** `Indexed.Core/RepoWatcher.cs`

Path normalization, `.git/` skip, exclude-glob filtering, and the `OnError → ReconciliationRequested` fallback are all untested.

**Fix:** Add unit tests for `Normalize`, exclude-glob filtering, and the error-enqueue path (can be tested by calling the internal methods directly via `InternalsVisibleTo`).

#### M17. HeadPoller has zero test coverage

**File:** `Indexed.Core/HeadPoller.cs`

The mtime-optimization path, error backoff, and logarithmic warning logic are untested.

**Fix:** Add unit tests with a mock `GitRepository` to verify the mtime-based short-circuit and error counting.

#### M18. DaemonClient and DaemonLauncher have zero test coverage

**Files:** `Indexed.Cli/DaemonClient.cs`, `Indexed.Cli/DaemonLauncher.cs`

The HTTP client auto-start-then-connect flow, error response deserialization, and the 3-level executable resolution logic are all untested.

**Fix:** Add tests with a mock HTTP handler for `DaemonClient`. Add tests for `ResolveServiceExecutable` with env var override.

---

### Low (20)

| ID | File | Finding | Fix |
|----|------|---------|-----|
| L1 | `SqliteIndex.cs` | `DisposeAsync` not thread-safe — `_disposed` flag without synchronization | Use `Interlocked.CompareExchange` |
| L2 | `SqliteIndex.cs` | `ReturnReader` uses synchronous `_readerLock.Wait()` — blocks thread pool | Use `ConcurrentBag` for pool |
| L3 | `SqliteIndex.cs` | Mixed static/instance method patterns (scope-requiring vs not) are confusing | Document or unify the pattern |
| L4 | `WriterScope` | Double-dispose releases `_writerLock` twice, potentially allowing concurrent writers | Guard with `_disposed` flag |
| L5 | `IncrementalIndexer.cs` | `LookupFileIdByPath` inside `WriterScope` doesn't set `cmd.Transaction` — reads outside transaction | Pass transaction to lookup |
| L6 | `DebouncingEventQueue.cs` | `CancellationTokenSource` created per loop iteration in batch window | Minor resource churn; acceptable |
| L7 | `HeadPoller.cs` | `_consecutiveErrors` integer overflow at `INT_MAX` (theoretical) | Cap at 1024 |
| L8 | `CodeQueryExecutor.cs` | Zero-length regex match yields confusing results | Document behavior or skip zero-length matches |
| L9 | `CodeQueryExecutor.cs` | `ByteOffset` is character offset, not byte offset for non-ASCII | Rename to `CharOffset` or compute true byte offset |
| L10 | `CodeQueryExecutor.cs` | `CompileOptionalGlob` has unused `include` parameter | Remove parameter |
| L11 | `RegexParser.cs` | `_ = isCapturing` is dead code | Remove variable |
| L12 | `TrigramExpr.cs` | `CanonicalKey()` allocates heavily for And/Or nodes | Cache or use `StringBuilder` |
| L13 | `PathGlob.cs` | `Matches(glob, path)` recompiles regex on every call | Document as convenience method; callers should use `Compile` |
| L14 | `GitProcess.cs` | `IsLockContention` "Unable to create" check is overly broad | Narrow to "Unable to create.*lock" |
| L15 | `GitProcess.cs` | `Thread.Sleep` in retry loop blocks thread pool thread | Accept (entire API is sync) or convert to async |
| L16 | `DaemonInfo.cs` | `WriteAtomic` leaves orphaned `.tmp-*` files on crash | Clean up stale `.tmp-*` on startup |
| L17 | `DaemonHost.cs` | `/rescan` returns 200 instead of 202 for async operation | Change to 202 Accepted |
| L18 | `DaemonHost.cs` | `TimeoutExceeded` maps to 504 (Gateway Timeout) instead of 408 | Change to 408 or document the choice |
| L19 | `IdleExitTimer.cs` | Comment inaccurately describes `TickCount64` sleep behavior | Fix comment |
| L20 | `OutputFormatter.cs` | `Pretty` `JsonSerializerOptions` field is dead code | Remove |

---

## Test coverage gaps summary

### Source files with zero test coverage

| File | Risk | Priority |
|------|------|----------|
| `MatchExtraction.cs` | High — load-bearing for every match result | P0 |
| `SqliteSearchBackend.cs` | High — request validation bounds | P0 |
| `CliApp.cs` | High — exit code contract | P1 |
| `DaemonClient.cs` | High — auto-start flow | P1 |
| `RepoWatcher.cs` | Medium — path normalization, error fallback | P1 |
| `HeadPoller.cs` | Medium — optimization correctness | P2 |
| `DaemonLauncher.cs` | Medium — executable resolution | P2 |
| `GitProcess.cs` | Medium — retry and timeout reliability | P2 |
| `ReconciliationScheduler.cs` | Low — trivial timer wrapper | P3 |
| `LanguageGuess.cs` | Low — advisory only | P3 |
| `DaemonPaths.cs` | Low — simple path composition | P3 |

### Missing test scenarios in existing test files

| Test file | Missing scenario |
|-----------|-----------------|
| `CodeQueryExecutorTests` | Case-sensitive matching, zero-length regex, non-ASCII ByteOffset, SortBy.Relevance, full-scan path |
| `IncrementalIndexerTests` | Error resilience (bad event doesn't kill worker), IsFaulted flag, HeadMoved→reconciliation fallback, exclude globs |
| `FullScanIndexerTests` | Binary skip, exclude globs, IOException skip, progress reporting, cancellation, batch boundary |
| `SqliteIndexTests` | Concurrent reader/writer, WriterScope.Fail rollback, ListFilesAsync, DisposeAsync double-call, WAL checkpoint, corrupt DB recovery |
| `DebouncingEventQueueTests` | Per-path debounce timing, Dispose unblocks DequeueAsync, FileChanged+FileChanged collapse, null event |
| `DaemonHostIntegrationTests` | /rescan endpoint, concurrent requests, idle-exit integration, MapErrorCodeToHttp branches |
| `IdleExitTimerTests` | Sleep-resume resilience, dispose during countdown, poke after fire |

### Top property-based testing candidates

1. **`PathGlob`** — random (glob, path) pairs checking match consistency with a reference `fnmatch`.
2. **`TrigramAnalyzer`** — random (regex, document) pairs checking the superset invariant (trigram-narrowed set always contains all true matches).
3. **`MatchExtraction`** — random (content, offset) pairs checking round-trip: `LineAndColumnOf(offset)` → `LineTextAt(line)` → content at offset is within the line.
4. **`SplitNullTerminated`** — random string lists joined with NUL, checking lossless round-trip.
5. **`JsonContractTests`** — random `SearchRequest` instances checking JSON round-trip stability.

---

## Recommended fix priority

### Phase 1 — Critical/High correctness (immediate)

1. **C1:** Fix `SqliteIndex` thread-safety — route `GetMeta`/`TryGetShaByPath`/`LookupFileIdByPath` through reader connections.
2. **H1:** Handle unmerged (`U`) status in `GetDiffTree` parser.
3. **H2:** Fix context-line numbering in `OutputFormatter` and update the test.
4. **H5:** Fix `IsFaulted` logic in `IncrementalIndexer` to not trigger on normal `Complete()` shutdown.

### Phase 2 — High robustness (next session)

5. **C2:** Add retry logic to `BindListener` for port-binding race.
6. **H3:** Add request concurrency limiter (`SemaphoreSlim`) in `DaemonHost`.
7. **H4:** Add request body size limit (64 KB) on `/search`.
8. **H6:** Write `MatchExtractionTests`.
9. **H7:** Write `SqliteSearchBackend` validation tests.

### Phase 3 — Medium improvements (subsequent)

10. **M1–M3:** GitProcess `StandardOutputEncoding` fix, `stderrTask` await, `CancellationToken` propagation.
11. **M4–M5:** Fix `PendingCount` accuracy, batch `BulkDeleteFiles`.
12. **M6–M7:** Extract shared file-processing helper, reduce GC pressure.
13. **M8:** Cache HEAD SHA from `HeadPoller` instead of spawning git per request.
14. **M9–M11:** HTTP response correctness (404 body, log listener errors, error code fix).
15. **M12–M15:** Minor efficiency fixes.
16. **M16–M18:** Fill test coverage gaps for `RepoWatcher`, `HeadPoller`, `DaemonClient`.
