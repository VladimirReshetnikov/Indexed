# Indexed Stage 4 — Incremental Indexer (Detailed Plan)

- Created (UTC): 2026-04-15T21:55:03Z
- Repository HEAD: 5c20ea5a456d352145953e95969f6e3ea9954a6b

## Revision to the original plan

The original Stage 4 plan (§Stage 4 in `Indexed-Implementation-Plan.md`) stated:

> HEAD changes trigger a full rescan.

This is unnecessarily expensive. Git tracks exactly which files changed between
any two tree-ish references. A branch switch that touches 3 files should index 3
files, not re-walk 7,500. This document replaces the full-rescan-on-HEAD-change
design with a **git-diff-based incremental indexer** that uses three progressively
broader strategies:

| Event | Strategy | Expected cost |
|---|---|---|
| FileSystemWatcher fires for `src/Foo.cs` | **Single-file upsert** — read, SHA, upsert if changed | < 50 ms |
| HEAD moves (commit, checkout, pull, rebase) | **Diff-based patch** — `git diff-tree` between `indexed_head` and new HEAD | proportional to diff size |
| Periodic reconciliation (every 5 min) | **Lightweight audit** — `git ls-files` vs index `files` table, fix discrepancies | seconds for path-set diff; no file I/O unless mismatches found |
| Schema mismatch or missing `indexed_head` | **Full rebuild** — existing `FullScanIndexer.RunAsync` | cold-start only |

The full rescan is the **fallback of last resort**, not the normal response to
HEAD changes.

---

## 1. Git operations to add to `Indexed.Git`

### 1.1 `DiffTreeEntry` and `GetDiffTree`

```
record DiffTreeEntry(DiffStatus Status, string Path, string? OldPath);
enum DiffStatus { Added, Modified, Deleted, Renamed, Copied }

IReadOnlyList<DiffTreeEntry> GetDiffTree(string fromCommit, string toCommit);
```

Implementation: `git diff-tree -r --name-status --no-commit-id -z <from> <to>`.
The `-z` flag gives NUL-delimited output, which handles paths with spaces or
special characters. For renames (`R100`), the old and new paths are both present.

This is the core primitive. A branch switch from commit A to commit B produces
exactly the set of files that differ — typically tens of files, not thousands.

### 1.2 `GetHeadSha` (already exists)

Returns the 40-hex HEAD. The incremental indexer polls this on a timer and after
FSW events settle.

### 1.3 `GetUntrackedFiles`

```
IReadOnlyList<string> GetUntrackedFiles();
```

Implementation: `git ls-files --others --exclude-standard -z`. Needed for the
reconciliation pass — untracked-but-not-ignored files should be indexed (they
are part of the developer's working tree even before `git add`).

### 1.4 `GetIndexMtime`

```
DateTimeOffset? GetIndexMtime();
```

Returns `File.GetLastWriteTimeUtc(".git/index")`. A cheap canary: if this
timestamp hasn't changed since the last check, no git operation has mutated the
index — HEAD hasn't moved, staging area hasn't changed. This avoids spawning
`git rev-parse HEAD` on every poll tick.

---

## 2. Incremental indexer worker (`IncrementalIndexer`)

### 2.1 Architecture

```
                    ┌───────────────────────┐
  FSW events ──────►│                       │
                    │  DebouncingEventQueue  │
  HEAD poll ───────►│                       │
                    │  (per-path 250ms       │
  Rescan timer ────►│   global 500ms batch)  │
                    └──────────┬────────────┘
                               │
                               ▼
                    ┌───────────────────────┐
                    │  IncrementalIndexer    │
                    │  (single background   │
                    │   Task, processes      │
                    │   batches serially)    │
                    └──────────┬────────────┘
                               │
                    ┌──────────┴────────────┐
                    │     SqliteIndex       │
                    │  (writer scope per    │
                    │   batch commit)       │
                    └───────────────────────┘
```

A single `Task.Run` loop drains batches from the queue. The worker is the
**only writer** to `SqliteIndex` after startup — no concurrent indexing. This
matches the architecture proposal §10 (single-threaded writer).

### 2.2 Event types

```csharp
abstract record IndexEvent;
record FileChanged(string RelativePath) : IndexEvent;
record FileDeleted(string RelativePath) : IndexEvent;
record HeadMoved(string OldHead, string NewHead) : IndexEvent;
record ReconciliationRequested : IndexEvent;
```

### 2.3 Processing logic

**`FileChanged`** (from FSW or extracted from diff):
1. Check exclude globs — skip if matched.
2. Check binary attrs / `IsLikelyBinary` — skip if true.
3. Read file bytes, compute SHA-256.
4. Compare against `TryGetShaByPath` — skip if unchanged.
5. Decode as UTF-8, upsert via `SqliteIndex.UpsertFile`.

**`FileDeleted`** (from FSW or extracted from diff):
1. Look up `file_id` by path in the index.
2. If present, `SqliteIndex.DeleteFile`.

**`HeadMoved(oldHead, newHead)`**:
1. Run `GetDiffTree(oldHead, newHead)`.
2. For each entry:
   - `Added` / `Modified` → emit `FileChanged(path)`.
   - `Deleted` → emit `FileDeleted(path)`.
   - `Renamed(oldPath, newPath)` → emit `FileDeleted(oldPath)` + `FileChanged(newPath)`.
   - `Copied(_, newPath)` → emit `FileChanged(newPath)`.
3. Process all emitted events in a single writer scope (one transaction).
4. Update `indexed_head` meta to `newHead`.

This is the key improvement: a `git checkout other-branch` that changes 5 files
produces 5 `FileChanged` + however many `FileDeleted` events — not a walk of
7,500 files.

**`ReconciliationRequested`** (periodic, every 5 minutes):
1. `git ls-files` union → set A (what git knows about).
2. `SqliteIndex.ListFilesAsync` → set B (what the index has).
3. `A \ B` → files missing from index → emit `FileChanged` for each.
4. `B \ A` → files in index but not in git → emit `FileDeleted` for each.
5. For files in `A ∩ B`, optionally spot-check a random sample's SHA against
   disk to catch silent corruption or missed FSW events.
6. Check HEAD: if `indexed_head != git rev-parse HEAD`, emit `HeadMoved`.

The reconciliation is the safety net. It handles:
- Dropped FSW events (FSW is inherently unreliable on all platforms).
- External git operations the daemon's poller missed.
- Files modified while the daemon was stopped.

### 2.4 Batching and transactions

Events are grouped into batches by the debouncing queue:
- **Per-path debounce**: 250ms. Rapid saves to the same file collapse into one event.
- **Global batch window**: 500ms. All events that arrived within the window are
  committed in one `WriterScope`.
- **Max batch size**: 200 files (matches `FullScanIndexer.BatchFileCount`).
  Larger diffs (e.g., a branch switch touching 500 files) split across multiple
  transactions to keep WAL size bounded.

### 2.5 Ordering guarantees

The single-writer design means events are applied in arrival order. When a
`HeadMoved` event arrives while `FileChanged` events from FSW are in-flight:
1. The debouncer drains pending FSW events first (they're already queued).
2. The `HeadMoved` handler runs `GetDiffTree`, which supersedes any FSW events
   for the same paths (the diff includes the final state).
3. Duplicate work is harmless — SHA-256 comparison short-circuits re-indexing.

---

## 3. Watcher components

### 3.1 `RepoWatcher` (new project: `Indexed.Watcher`)

Wraps `FileSystemWatcher` with:
- Recursive watch on the repo working tree.
- Exclude filter: `.git/` internal files, `node_modules/`, build output, and
  the configured `--exclude-index` globs.
- Normalization: backslash → forward-slash, absolute → repo-relative.
- Events: `FileChanged`, `FileDeleted` pushed to the debouncing queue.

FSW is a **best-effort hint**. It will miss events under high churn, across
network drives, and on some Linux kernel configurations. The reconciliation
timer is the correctness backstop.

### 3.2 `HeadPoller`

A timer (default: 1 second) that:
1. Checks `.git/index` mtime — if unchanged since last tick, skip.
2. Runs `git rev-parse HEAD`.
3. If HEAD differs from `indexed_head` in meta, pushes `HeadMoved(old, new)`.

The mtime check makes the common case (no git operation in the last second) a
single `stat()` call — no process spawn.

### 3.3 `ReconciliationScheduler`

A timer (default: 5 minutes) that pushes `ReconciliationRequested` into the
queue. The actual work happens in the indexer worker, not in the timer callback.

---

## 4. Edge cases

### 4.1 Branch switch with uncommitted changes

`git checkout other-branch` with a dirty working tree either:
- Succeeds (changes carry over) — `GetDiffTree` shows the commit diff; FSW
  separately catches any working-tree-only modifications.
- Fails (conflicts) — HEAD doesn't move, no `HeadMoved` event.

### 4.2 `git stash` / `git stash pop`

Stash creates a commit but doesn't move HEAD — no `HeadMoved`. The working tree
changes are caught by FSW. Stash pop similarly changes working-tree files
(FSW-visible) without a HEAD move.

### 4.3 `git rebase` (interactive or not)

Rebase rewrites commits. HEAD moves to each rewritten commit in sequence. The
HeadPoller fires once the rebase completes (or after each step if the poller is
fast enough). `GetDiffTree(indexed_head, new_head)` correctly captures the
cumulative diff.

### 4.4 `git pull --rebase` / `git pull --merge`

Both result in a HEAD move. The diff-tree between old HEAD and new HEAD captures
all incoming changes. FSW also fires for working-tree file updates, but the
HeadMoved handler's diff supersedes them.

### 4.5 Force push to a tracked remote (no local change)

No local HEAD change, no local file change. Nothing to index. If the user later
`git pull`s, that's a normal HEAD move.

### 4.6 `indexed_head` is missing or invalid

If `indexed_head` is null (fresh index) or points to a commit that no longer
exists (after `git gc` or a force-push reset), `GetDiffTree` will fail. Fallback:
run the existing `FullScanIndexer.RunAsync` — this is the cold-start path.

### 4.7 Shallow clones

`GetDiffTree` works on shallow clones as long as both commits are reachable. If
`indexed_head` was pruned by a `git fetch --depth`, fall back to full scan.

### 4.8 Submodules

Out of scope for Stage 4. Submodule paths are skipped by `IsLikelyBinary`
(the `.git` file inside the submodule directory) or by the binary-attr check.
Future: a separate indexer per submodule.

### 4.9 Large merges (e.g., merge of a long-lived branch)

`GetDiffTree` between the old HEAD and the merge commit returns the full set of
files changed by the merge. This could be hundreds of files. The batching
strategy (200 files per transaction) keeps memory and WAL size bounded. The
total time is proportional to the number of changed files, not the repo size.

### 4.10 Rapid successive HEAD moves

Example: `git rebase -i` rewrites 20 commits in quick succession. The
HeadPoller (1s interval) may observe only the final HEAD. That's correct:
`GetDiffTree(indexed_head, final_head)` captures the cumulative diff. No
intermediate states are indexed — they existed only transiently.

---

## 5. New `SqliteIndex` operations

### 5.1 `GetAllPathsWithSha`

```csharp
ValueTask<IReadOnlyDictionary<string, byte[]>> GetAllPathsWithShaAsync(CancellationToken ct);
```

Returns every `(path, sha256)` pair from the `files` table in one query. Used by
the reconciliation pass to diff against git's file set without N round-trips via
`TryGetShaByPath`.

### 5.2 `LookupFileIdByPath`

```csharp
long? LookupFileIdByPath(string path);
```

Returns the `file_id` for a path, or null. Used by `FileDeleted` processing to
resolve the ID before calling `DeleteFile`.

### 5.3 `BulkDeleteFiles`

```csharp
static void BulkDeleteFiles(WriterScope scope, IReadOnlyList<long> fileIds);
```

Batch-deletes multiple files in one transaction. Used when a branch switch
removes many files at once.

---

## 6. Freshness accounting

### 6.1 `pendingFileCount`

The debouncing queue exposes `int PendingCount` — the number of distinct paths
awaiting indexing. `BuildFreshness()` in `DaemonHost` reads this to populate
`SearchResponse.Freshness.PendingFileCount`.

### 6.2 `isStale` formula

```
isStale = (indexed_head != current_head) || (pendingCount > 0)
```

After the incremental indexer processes a `HeadMoved` event and updates
`indexed_head`, staleness clears — even if the watcher queue has pending FSW
events (those represent working-tree-only changes, not committed drift).

### 6.3 `indexed_head` update timing

`indexed_head` is updated **after** all files from the diff-tree are committed
to the index. If the daemon crashes mid-batch, the next startup sees a stale
`indexed_head` and re-runs the diff — idempotent because SHA-256 comparison
skips already-indexed files.

---

## 7. Service lifecycle changes

### 7.1 `DaemonHost.StartAsync`

After opening the index and running the cold-start full scan (if needed):
1. Start `RepoWatcher`.
2. Start `HeadPoller` (1s interval).
3. Start `ReconciliationScheduler` (5 min interval).
4. Start the `IncrementalIndexer` background task.

### 7.2 `DaemonHost.DisposeAsync`

1. Cancel the indexer worker's `CancellationToken`.
2. Stop the watcher, poller, and reconciliation timer.
3. Await the indexer task (drain remaining batch).
4. Dispose the `SqliteIndex`.

### 7.3 `POST /rescan`

Enqueue a `ReconciliationRequested` event. Return 200 immediately with the
current status — the reconciliation runs asynchronously.

### 7.4 Idle-exit interaction

The idle timer currently counts from the last HTTP request. Stage 4 extends
this: the timer also resets when the indexer worker commits a batch. A repo with
active development stays alive as long as changes flow, even if no queries
arrive.

---

## 8. Tasks

| ID | Task | Depends on |
|----|------|------------|
| 4.1 | Add `GetDiffTree`, `GetUntrackedFiles`, `GetIndexMtime` to `GitRepository` + tests | — |
| 4.2 | Add `GetAllPathsWithShaAsync`, `LookupFileIdByPath`, `BulkDeleteFiles` to `SqliteIndex` + tests | — |
| 4.3 | Scaffold `Indexed.Watcher` project: `RepoWatcher` (FSW wrapper with exclude filters) + tests | — |
| 4.4 | `DebouncingEventQueue` with per-path and global-batch debouncing + tests | — |
| 4.5 | `IncrementalIndexer` worker: drain queue, process events, batch commit | 4.1, 4.2, 4.4 |
| 4.6 | `HeadPoller` (1s timer, mtime check, rev-parse, emit `HeadMoved`) + tests | 4.1 |
| 4.7 | `ReconciliationScheduler` (5 min timer, path-set diff, emit events) + tests | 4.2, 4.5 |
| 4.8 | Wire watcher/poller/scheduler into `DaemonHost` lifecycle | 4.3, 4.5, 4.6, 4.7 |
| 4.9 | Freshness accounting: `pendingFileCount` from queue, `isStale` from indexed_head + queue | 4.5, 4.8 |
| 4.10 | Integration tests: single-file edit, branch switch, bulk pull, crash recovery | all above |

---

## 9. Exit criteria

- Single-file save → queryable in **≤ 2 s** (watcher → debounce → upsert → commit).
- `git checkout other-branch` touching 50 files → re-indexed in **≤ 5 s**
  (diff-tree + 50 upserts, no full scan).
- `git pull` bringing 200 new files → re-indexed in **≤ 10 s**.
- 100-file bulk edit (IDE refactor) → queryable in **≤ 10 s**.
- `isStale` transitions from `true` to `false` within 3 s of the last indexed
  batch commit.
- Reconciliation at 5-min intervals catches simulated FSW drops (kill-watcher
  test) within one interval.
- Daemon survives 10 minutes of continuous `while true; do echo x >> f; done`
  without unbounded memory growth (debouncer collapses events).

---

## 10. Risks and mitigations

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| FSW drops events under high churn | High on Windows with > 4K events/sec | 5-min reconciliation catches all gaps; debouncer collapses bursts |
| `GetDiffTree` fails (pruned commit, shallow clone) | Low | Fall back to `FullScanIndexer.RunAsync`; log warning |
| Rapid HEAD moves (interactive rebase) exhaust resources | Low | HeadPoller sees only the final HEAD; diff is cumulative |
| `.git/index` mtime unreliable on network drives | Medium | HeadPoller falls through to `rev-parse HEAD` when mtime is unsupported; reconciliation catches the rest |
| Large merge diff (1000+ files) overwhelms single-writer | Low | Batching (200/tx) bounds WAL size; total time is O(files), not O(repo) |
| Concurrent `POST /rescan` while indexer is mid-batch | Medium | Single-writer design serializes; reconciliation event queues behind current batch |
