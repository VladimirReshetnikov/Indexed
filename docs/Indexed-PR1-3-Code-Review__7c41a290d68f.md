# Indexed — PR 1–3 Code Review Report

- Created (UTC): 2026-04-16T15:40:33Z
- Repository HEAD: cdfd6547b16415ad02c832fd92ce89ac967231a6
- Scope: Targeted review of the three workstreams implemented from
  `Indexed-Size-Reduction-SafeNearTerm-Plan.md`:
  - **PR 1** — Default index excludes (`ExcludeFilter.DefaultBinaryAdjacentGlobs`,
    wiring in `DaemonHost`, `DaemonOptions.UseDefaultIndexExcludes`, CLI forwarding).
  - **PR 2** — Background FTS5 segment merger (`IndexOptimizer`,
    `SqliteIndex.RunFts5MergeAsync`, `DaemonHost` lifecycle wiring).
  - **PR 3** — Contentless FTS5 + disk-read snippet rehydration
    (`SqliteSchema` v2, `FileContentProvider`, `CodeQueryExecutor` refactor,
    `SqliteSearchBackend` plumbing, repair-event enqueue).
  - Default index location change from `%APPDATA%` to `%LOCALAPPDATA%`
    (`DaemonPaths`, `DaemonOptions`, doc updates).

## Executive summary

The three PRs land cleanly and are well-tested — 313 tests pass, and the
behavioral invariants are pinned (default globs match lockfiles/minified/
generated, contentless FTS5 drops the `content` bytes, disk-read confirms
every candidate). The code is defensive in the right places (atomic dirty
flag, dirty-flag restore on merge failure, post-read size re-check).

This review identified **19 findings**: 0 critical, 2 high, 5 medium, 7 low,
5 nits. The highest-priority items are (a) path-traversal hardening in
`FileContentProvider.ReadAsync` — currently unreachable via the on-disk
schema but cheap to defend, and (b) an uncaught `ArgumentException` path
from invalid path characters that would surface as HTTP 500. The medium
findings mostly concern convergence efficiency (stale rows not being
deleted promptly after a disk-read confirms "gone") and duplication of
the `MaxIndexableFileBytes` constant.

## Findings by severity

### High (2)

#### H1. `FileContentProvider.ReadAsync`: no path-traversal defense

**File:** `Indexed.Core/FileContentProvider.cs`, lines 67–91

```csharp
public async ValueTask<string?> ReadAsync(string relPath, CancellationToken cancellationToken)
{
    if (string.IsNullOrEmpty(relPath)) return null;

    var full = Path.Combine(_repoRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
    ...
}
```

`Path.Combine` is textual — if `relPath` contains `..` segments or is an
absolute path, the resulting `full` can escape `_repoRoot`. Today this is
practically unreachable because rows in `files.path` are populated from
`GitRepository.EnumerateFiles()` (which only yields repo-relative
forward-slash paths), so no `..` or absolute path ever reaches this code.
However, the invariant is enforced one level away, not here. Defense in
depth matters for a trust boundary that services HTTP requests: if the
DB is ever populated by a different code path (migration, import, test
seed) or corrupted by an external process, the current code would happily
open `C:\Windows\System32\config\SAM`.

**Proposed fix:** After computing `full`, canonicalize with
`Path.GetFullPath(full)` and verify the result is still rooted under
`_repoRoot` (with a trailing-separator-appended prefix comparison, as
`RepoWatcher.Normalize` already does at lines 135–141). Return `null`
otherwise. Cost: one `Path.GetFullPath` call per read, already effectively
free vs. the subsequent `File.ReadAllBytesAsync`.

```csharp
var full = Path.Combine(_repoRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
var canonical = Path.GetFullPath(full);
var root = _repoRoot.EndsWith(Path.DirectorySeparatorChar)
    ? _repoRoot : _repoRoot + Path.DirectorySeparatorChar;
if (!canonical.StartsWith(root, StringComparison.OrdinalIgnoreCase))
    return null;
```

#### H2. `FileContentProvider.ReadAsync`: `ArgumentException` bubbles to HTTP 500

**File:** `Indexed.Core/FileContentProvider.cs`, lines 73–91 catch list

The try/catch block catches `UnauthorizedAccessException`,
`NotSupportedException`, and `IOException`. It does **not** catch
`ArgumentException`, which `FileInfo` and `Path.Combine` can throw for
paths containing invalid characters (null bytes, trailing dots on
Windows, reserved device names like `CON`, `NUL` in rare contexts).
A stored `files.path` value with a malformed character — possible after
a partial delete or a pathological filename that slipped past
`GitRepository.EnumerateFiles` — raises `ArgumentException` out of
`ReadAsync`, which propagates through `CodeQueryExecutor.ExecuteAsync`
to `DaemonHost.HandleRequestSafelyAsync`, which returns HTTP 500 with
a generic "unhandled server error".

The contract promised by the XML doc is "any I/O issue yields `null`".
An invalid path is an I/O issue by any reasonable definition.

**Proposed fix:** Add `catch (ArgumentException) { return null; }` to the
catch list. Consider `PathTooLongException` — derives from `IOException`
on modern .NET so already covered; no extra catch needed. No test
currently exercises the `ArgumentException` path; add one that stores
a pathological path into the `files` table directly.

---

### Medium (5)

#### M1. `IndexOptimizer` and `IncrementalIndexer`: disposal flag check-and-set is not atomic

**Files:** `Indexed.Core/IndexOptimizer.cs` lines 194–197;
`Indexed.Core/IncrementalIndexer.cs` lines 90–93

Both classes implement `DisposeAsync` as:

```csharp
if (_disposed) return;
_disposed = true;
```

If two threads call `DisposeAsync` concurrently, both can observe
`_disposed == false`, both can set it to true, and both can execute the
full tear-down logic (double cancel of `CancellationTokenSource`, double
wait on `_cycleGate`, double dispose of `SemaphoreSlim`). This is
defensive-only in practice — `DaemonHost.DisposeAsync` is the single
caller and is itself called from the shutdown code path once — but the
invariant is fragile and cheap to fix.

**Proposed fix:** Use `Interlocked.Exchange(ref _disposedFlag, 1) == 1`
to turn the check into an atomic compare-and-set. `IndexOptimizer._dirty`
already uses this pattern.

```csharp
private int _disposed;   // 0 = live, 1 = disposed

public async ValueTask DisposeAsync()
{
    if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
    ...
}
```

#### M2. `CodeQueryExecutor`: disk-confirmed deletion enqueues `FileChanged`, not `FileDeleted`

**File:** `Indexed.Core/CodeQueryExecutor.cs`, lines 122–133

When `_contentProvider.ReadAsync` returns `null` the executor enqueues
`new FileChanged(row.Path)` for repair. This is correct for the
"unreadable" and "oversize" cases — the incremental indexer will try to
read, fail or see a still-oversize file, and skip. But for the "missing"
case (the common case), the file is genuinely gone; enqueueing
`FileChanged` causes the incremental indexer to hit
`if (!info.Exists) { skipped++; continue; }` and **leave the stale DB
row in place**. The row survives until the next
`ReconciliationScheduler` tick (default: 5 minutes) or HEAD move.

During that window the trigram posting list still points at a rowid the
`files` table still maps to a nonexistent path, so every subsequent
search pays the cost of "candidate → rowid → path → disk read fails →
drop". Bounded (the dead rowids are a small fraction) but real.

**Proposed fix (preferred):** Enrich `FileContentProvider.ReadAsync` to
distinguish "missing" from "unreadable/oversize" — either via out
parameter, discriminated return, or a second method. Have the executor
enqueue `FileDeleted` for missing, `FileChanged` for the others. A
minimal change keeps the API but adds:

```csharp
public async ValueTask<string?> ReadAsync(
    string relPath, CancellationToken ct, bool reportOutcome = false, ...);

// or:
public readonly record struct ReadOutcome(string? Content, FileMissReason Reason);
public enum FileMissReason { None, Missing, Oversize, Unreadable }
```

**Proposed fix (minimal):** Add a single `File.Exists` check in the
executor before the `_repairQueue.Enqueue` call and enqueue
`FileDeleted` when the file is gone. This re-stats the file (the
provider already did so), so the discriminated-return option is cleaner.

#### M3. `IndexOptimizer.DisposeAsync`: unbounded final merge on shutdown

**File:** `Indexed.Core/IndexOptimizer.cs`, lines 210–234

The final merge runs at `_pageBudget * 2` pages and has no timeout or
cancellation. With the default `pageBudget = 512`, that is 1024 pages
per FTS table — typically milliseconds, but the code path is:

1. `_cycleGate.WaitAsync()` — no cancellation token, no timeout.
2. `_index.RunFts5MergeAsync(…, CancellationToken.None)` — also no
   cancellation; the inner `BeginWriteAsync` can wait for the writer
   lock indefinitely.

The DaemonHost shutdown order does mitigate this (the incremental
indexer drains before the optimizer disposes, so the writer lock is
free by then), but any slow `PRAGMA wal_checkpoint(TRUNCATE);` in
`SqliteIndex.DisposeAsync` could still hold a lock for a surprising
amount of time. This means shutdown can, in pathological cases, block
the daemon past the client's patience (CLI timeout, `gh run` cancel,
Ctrl-C handler).

**Proposed fix:** Accept a `CancellationToken`/timeout from
`DaemonHost.DisposeAsync` and plumb it into the final merge. A
`TimeSpan.FromSeconds(10)` budget is generous for the typical case and
bounds the worst. Alternatively: skip the final merge if `_dirty` has
already been cleared (which it has, by the `CompareExchange` line 210),
which is the current behavior — but add a hard timeout on the
`_cycleGate.WaitAsync()` call.

#### M4. `MaxIndexableFileBytes` is duplicated across three call sites

**Files:**
- `Indexed.Core/FullScanIndexer.cs` line 56 — `public const long MaxIndexableFileBytes = 50L * 1024 * 1024;`
- `Indexed.Core/IncrementalIndexer.cs` line 48 — `private const long MaxIndexableFileBytes = 50L * 1024 * 1024;`
- `Indexed.Core/FileContentProvider.cs` lines 77, 81 — refers to
  `FullScanIndexer.MaxIndexableFileBytes` (correct).

If the limit ever changes, two places must be updated in lockstep. The
`IncrementalIndexer` copy is the odd one out — it should reference
`FullScanIndexer.MaxIndexableFileBytes` like `FileContentProvider` does.

**Proposed fix:** Delete the private const in `IncrementalIndexer` and
replace its three usage sites with `FullScanIndexer.MaxIndexableFileBytes`.
Consider promoting the constant to a dedicated `IndexLimits` static class
alongside `BatchFileCount` and `BatchTimeBudget` in a future cleanup;
not blocking for PR 3 cleanup.

#### M5. `SqliteIndex.UpsertFile`: `content` parameter name is misleading for v2

**File:** `Indexed.Core/SqliteIndex.cs`, lines 332–389

The signature advertises a `string content` parameter and the inline
SQL still does `INSERT INTO code_fts(rowid, content) VALUES($id, $content)`.
Under schema v2 (`content = ''`, i.e. contentless), FTS5 tokenizes the
`$content` value for the posting list but discards the text — the
stored bytes are zero. Readers of the code (including anyone chasing
disk-bytes accounting) can reasonably expect that a non-empty `content`
argument round-trips and is retrievable from `code_fts.content` — it
isn't.

**Proposed fix:** Rename the parameter to `contentForTokenization` (or
similar) and add a `<remarks>` block on `UpsertFile` explaining that for
schema v2 this is consumed only by the tokenizer and never persisted.
The `CREATE VIRTUAL TABLE` DDL already documents `content = ''`; make
the signature match. Optional: add a Debug.Assert that FTS5 stored no
content bytes for a sample row as a regression guard.

---

### Low (7)

#### L1. `FileContentProvider` has no direct unit tests

**Files:** `tests/Indexed.Core.Tests/`

`FileContentProvider` is covered only transitively via
`CodeQueryExecutorDiskReadTests`. A focused test file
(`FileContentProviderTests.cs`) would cover:

- Missing file → `null`.
- Oversize file (boundary: exactly `MaxIndexableFileBytes` bytes; one
  byte over).
- File that grew between stat and read (the post-read re-check branch
  at line 81).
- Invalid path characters (null byte, `:` on Linux — though the
  provider's surface is `relPath`, so the invalid-character cases are
  constrained).
- Backslash normalization (`src\foo.cs` vs. `src/foo.cs`).
- Empty `relPath` → `null`.
- After the H1 fix lands, a `../../..` relpath → `null` without
  reading.

#### L2. `RepoWatcher.OnError` can queue unbounded reconciliation events

**File:** `Indexed.Core/RepoWatcher.cs`, lines 119–126

Every FSW error (including transient access denied on short-lived
temp files, which is common on Windows during compilation) enqueues a
`ReconciliationRequested`. Each reconciliation walks the entire repo
path set (`_repo.EnumerateFiles()` + `_index.GetAllPathsWithShaAsync`)
— cheap, but under a noisy-FSW bug loop this could starve the
incremental indexer.

`DebouncingEventQueue.Absorb` stores `ReconciliationRequested` in
`_pendingGlobal` (a `List<IndexEvent>`), which does **not** dedupe —
N events become N queued reconciliations, each costing a full path-set
diff.

**Proposed fix:** Treat `ReconciliationRequested` as idempotent and
coalesce in `_pendingGlobal`: if an event of the same type is already
present, drop the new one. Add a `HashSet<Type>` on the side or
use pattern-matching in `Absorb`.

#### L3. `IndexOptimizer.MergeCount`: redundant Volatile.Read

**File:** `Indexed.Core/IndexOptimizer.cs`, line 69

```csharp
public int MergeCount => Volatile.Read(ref _mergeCount);
```

`_mergeCount` is only written by `Interlocked.Increment` — which
already provides acquire/release semantics sufficient for readers on
x86/x64 (where `int` reads/writes are atomic) and ARM64 (where
`Interlocked.Increment` issues the right fences). The `Volatile.Read`
is harmless but redundant. Keep or remove; it's not incorrect.

#### L4. `ExcludeFilter.Combine`: returns the reference of `defaults`, not a copy

**File:** `Indexed.Core/ExcludeFilter.cs`, lines 80–91

```csharp
if (aEmpty) return b;     // returns b by reference
if (bEmpty) return a;     // returns a by reference
```

`DefaultBinaryAdjacentGlobs` is a static `string[]` exposed as
`IReadOnlyList<string>`. If a caller were to cast back to `string[]`
and mutate, every future `Combine(null, defaults)` would see the
mutation. This is contrary to intuition for a property named
"Defaults". In practice no one does this, but the API surface is a bit
loose.

**Proposed fix:** Copy `b` into a fresh `string[]` in the `aEmpty` branch
(and `a` in the `bEmpty` branch). Cheap — the defaults list is ~17
strings. Alternatively: change
`DefaultBinaryAdjacentGlobs` to a `ReadOnlyCollection<string>` wrapper
so downcasts fail.

#### L5. `IncrementalIndexer.ProcessBatchAsync`: three serialized writer scopes per batch

**File:** `Indexed.Core/IncrementalIndexer.cs`, lines 150–335

A batch with both deletes, upserts, and a HEAD move opens three
separate `WriterScope`s (one for deletes, one for upserts, one for
`indexed_head`). Each scope is a `BEGIN/COMMIT` round-trip that
serializes against the optimizer's merges. The split exists so a
failure in the upsert loop doesn't roll back already-applied deletes —
which is a valid property — but the HEAD-meta update could be merged
into the upsert scope (or a combined scope) since it's a single meta
write.

**Proposed fix:** Collapse the `indexed_head` meta write into the
upsert scope (or append it to the delete scope if upserts were empty).
Saves one writer-lock acquire + one fsync per batch. Keep the
delete/upsert split to preserve the "deletes apply even if upsert
fails" property.

#### L6. `DaemonInfo.TryDelete`: swallows all exceptions during shutdown

**File:** `Indexed.Service/DaemonInfo.cs`, lines 107–111

```csharp
public static void TryDelete(string path)
{
    try { File.Delete(path); }
    catch { /* best-effort cleanup during shutdown */ }
}
```

The bare `catch` hides `OutOfMemoryException`, `StackOverflowException`,
`ThreadAbortException`, and (on .NET 10 — unlikely but possible)
`OperationCanceledException`. Best-effort cleanup is the right intent
for I/O, but the bare catch is broader than needed. Also, swallowing
`UnauthorizedAccessException` silently leaves a stale `daemon.json`
on disk — the next CLI run will find it, ping the stale port, fail,
delete it on its own via `DaemonClient.CreateAsync`. So the observable
behavior is fine, but a `catch (IOException) { } catch (UnauthorizedAccessException) { }`
would be more precise.

**Proposed fix:** Narrow the catch to `IOException` +
`UnauthorizedAccessException` + `System.Security.SecurityException`.

#### L7. `SqliteIndex.DisposeAsync`: reader cleanup loop isn't async all the way

**File:** `Indexed.Core/SqliteIndex.cs`, lines 689–723

The dispose path calls:

```csharp
await _readerLock.WaitAsync().ConfigureAwait(false);
```

— no cancellation token. If another thread still holds the reader lock
(e.g. a late `RentReaderAsync` call during shutdown), this blocks
forever. In practice the HTTP listener has already been stopped at
this point (DaemonHost line 263), so no new leases are requested, but
the invariant is enforced indirectly. A bounded `WaitAsync(TimeSpan)` or
a plumbed cancellation token would make shutdown deadlock-resistant.

**Proposed fix:** Accept a shutdown deadline from the caller and pass
it to both `_readerLock.WaitAsync` and the final `wal_checkpoint`
PRAGMA, which also has no timeout today.

---

### Nits (5)

#### N1. `IndexOptimizer.OnTick`: discard pattern on `Task` loses observability

**File:** `Indexed.Core/IndexOptimizer.cs`, lines 239–261

```csharp
private void OnTick(object? state)
{
    _ = RunTickAsync();
}
```

`RunTickAsync` catches exceptions internally, so this is safe — but
the discard hides the returned `Task`. A debugger (or a future
`TaskScheduler.UnobservedTaskException` handler) cannot observe
completions. Consider explicitly `.ContinueWith(…, TaskScheduler.Default)`
only if diagnostics grow to need it; today the comment at line 241
documents the choice adequately.

#### N2. `FullScanIndexer.MaxIndexableFileBytes` vs. `IncrementalIndexer.MaxIndexableFileBytes` vs. `FileContentProvider`

Captured in M4 above; duplicated here as a naming-consistency nit.
A single location (e.g. `IndexLimits.MaxIndexableFileBytes`) is less
error-prone than either of the current usages.

#### N3. `DaemonHost.StartAsync`: `launched` vs. `info` variable shadowing

**File:** `Indexed.Service/DaemonHost.cs`, lines 77–102 (actually
`DaemonClient.CreateAsync`)

The local `info` is overwritten by `launched` when the initial ping
fails. The names are fine, but the control flow ("info null? ping ok?
yes → Build; no → spawn → launched → Build") could benefit from a
dedicated `TryAdoptExistingAsync` helper. Low value.

#### N4. `StatusResponse` omits `IndexOptimizer.MergeCount` from observable state

**Files:** `Indexed.Abstractions/StatusResponse.cs`, `DaemonHost.BuildStatus`

Operators investigating "is the optimizer even running?" have no signal
in `/status`. Adding a `OptimizerStats { MergeCount, LastMergeAtUtc,
LastMergeElapsedMs }` field to `Freshness` or `StatusResponse` is
cheap and would make the PR-2 behavior observable from the outside.
Deferrable.

#### N5. `DebouncingEventQueue.Dispose`: only completes the writer; does not cancel pending delays

**File:** `Indexed.Core/DebouncingEventQueue.cs`, lines 143–147

`Dispose` calls `_incoming.Writer.TryComplete()`, which is enough for
`DequeueAsync` to exit on its next channel read. But if the consumer
is currently sleeping inside the global-batch `ReadAsync(delayCts.Token)`,
the channel-close exception fires promptly — good. No action needed;
captured as a nit in case a reader scans the dispose path expecting
more teardown.

---

## Cross-cutting observations

- **Test coverage is strong** for PR 1 and PR 2 (`ExcludeFilterDefaultGlobsTests`,
  `IndexOptimizerTests` — including the coexistence-with-indexer race test).
  PR 3 coverage exists in `CodeQueryExecutorDiskReadTests` for the three
  staleness classes, but a direct test suite for `FileContentProvider`
  would tighten the contract (see L1).

- **The `%LOCALAPPDATA%` default is already correct.** `DaemonPaths.ForRepo`
  now composes `LocalApplicationData` unconditionally when no override is
  supplied, and the `AppDataBase` property preserves the API-compatibility
  name. The XML docs explain the rationale (machine-specific, non-roaming
  derived artifact). No additional doc drift was found — `RepoId.cs`,
  `DaemonOptions.cs`, `Program.cs`, `DaemonInfo.cs`,
  `DaemonLauncher.cs`, `DaemonClient.cs`, and both architecture docs were
  updated in lockstep. Tests that override `AppDataBase = appData` remain
  transparent to the change.

- **Schema v2 migration path is correct.** `SqliteIndex.OpenOrCreate`
  already deletes `-wal`, `-shm`, `-journal` sidecars on schema mismatch
  (line 847–854), and a version-1 DB triggers a cold rebuild against v2
  DDL. No in-place migration needed; the usage-guide section 6.3 accurately
  describes user-facing behavior on upgrade.

- **Security model unchanged.** The `ShutdownToken` remains the only
  authenticated endpoint; disk reads by `FileContentProvider` are rooted
  at the repo (subject to H1 hardening). No new trust boundaries were
  introduced by PR 3.

## Recommendations ranked by impact

| Priority | Fix | Effort | Risk |
|----------|-----|--------|------|
| P0 | H1 — Canonicalize `full` path in `FileContentProvider.ReadAsync` and enforce root prefix | 10 lines | Minimal |
| P0 | H2 — Add `ArgumentException` to the provider's catch list | 1 line | Minimal |
| P1 | M2 — Distinguish missing-from-unreadable so executor can enqueue `FileDeleted` for missing | ~30 lines + test | Low — changes an event type |
| P1 | M3 — Plumb a shutdown timeout through `IndexOptimizer.DisposeAsync` | ~15 lines | Minimal |
| P2 | M1 — Replace `_disposed` bool with `Interlocked` flag in optimizer+indexer | 4 lines × 2 | Minimal |
| P2 | M4 — Deduplicate `MaxIndexableFileBytes` | 3 lines | Minimal |
| P2 | L1 — Add `FileContentProviderTests` | 100 lines | N/A (tests) |
| P3 | L2 — Coalesce `ReconciliationRequested` in `_pendingGlobal` | 5 lines | Low |
| P3 | L5 — Merge `indexed_head` meta write into the upsert scope | 10 lines | Low |
| P3 | L6 — Narrow catch in `DaemonInfo.TryDelete` | 3 lines | Minimal |
| P3 | L7 — Shutdown timeout for `SqliteIndex.DisposeAsync` | 10 lines | Minimal |
| P4 | M5 — Rename `UpsertFile` content parameter for clarity | 20 lines | Minimal (API rename) |
| P4 | N4 — Surface `MergeCount` / `LastMergeAt` in `/status` | 15 lines | Minimal |

## Verification

- Build: `dotnet build src/Indexed/Indexed.sln` — green.
- Tests: `dotnet test src/Indexed/Indexed.sln` — 313 tests pass, 0 skipped,
  0 failed.
- All changes to `%LOCALAPPDATA%` verified across:
  - `src/Indexed/src/Indexed.Service/DaemonPaths.cs`
  - `src/Indexed/src/Indexed.Service/RepoId.cs`
  - `src/Indexed/src/Indexed.Service/DaemonOptions.cs`
  - `src/Indexed/src/Indexed.Service/DaemonInfo.cs`
  - `src/Indexed/src/Indexed.Service/Program.cs`
  - `src/Indexed/src/Indexed.Cli/DaemonLauncher.cs`
  - `src/Indexed/src/Indexed.Cli/DaemonClient.cs`
  - `src/Indexed/src/Indexed.Abstractions/StatusResponse.cs`
  - `src/Indexed/docs/Indexed-Architecture.md`
  - `src/Indexed/docs/Indexed-Usage-Guide.md`

No findings in this report block the PR 1–3 merge; they are refinements
layered on top of a landing that is already correct and well-tested.
