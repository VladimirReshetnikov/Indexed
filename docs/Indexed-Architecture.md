# Indexed — Architecture

- Created (UTC): 2026-04-15T17:00:00Z
- Repository HEAD: cd463ca87356b067e49fe274a1ebcb6e92376c1d
- Status: Current-state architecture for the Indexed full-text search service. Covers Stages 0–2, 4–5 as implemented. Stage 3 (prose extraction) is documented as future work where it affects the schema.

## 1. System overview

Indexed is a background-indexed full-text search service for a single local git repository. It runs as a long-lived daemon process (`Indexed.Service`) with an HTTP/JSON surface on `127.0.0.1`, and a thin CLI client (`idx`).

Primary consumers are AI coding agents. The service is designed for:

- **Millisecond-class code search** via SQLite FTS5 with trigram tokenization.
- **Regex search** via a Russ Cox–style trigram narrowing + .NET `Regex` verification.
- **Eventually-consistent incremental updates** via `FileSystemWatcher`, git HEAD polling, and periodic reconciliation.
- **Crash-safe persistence** via SQLite WAL mode with single-writer concurrency.

### Architecture diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                          idx CLI (Indexed.Cli)                      │
│   Argument parsing → DaemonClient (HTTP) → Output formatting       │
└───────────────────────────────┬─────────────────────────────────────┘
                                │ HTTP/JSON on 127.0.0.1:<port>
┌───────────────────────────────▼─────────────────────────────────────┐
│                     DaemonHost (Indexed.Service)                     │
│                                                                      │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────────────────┐   │
│  │  /status      │  │  /search     │  │  /rescan  /shutdown      │   │
│  │  GET          │  │  POST        │  │  POST                    │   │
│  └──────┬───────┘  └──────┬───────┘  └──────────┬───────────────┘   │
│         │                 │                      │                    │
│         │     ┌───────────▼──────────┐           │                   │
│         │     │  SqliteSearchBackend  │           │                   │
│         │     └───────────┬──────────┘           │                   │
│  ┌──────▼─────────────────▼──────────────────────▼──────────────┐   │
│  │                      Indexed.Core                             │   │
│  │                                                               │   │
│  │  ┌────────────────┐  ┌──────────────┐  ┌─────────────────┐   │   │
│  │  │  CodeQuery      │  │  SqliteIndex  │  │  Incremental    │   │   │
│  │  │  Planner +      │  │  (WAL mode)   │  │  Indexer        │   │   │
│  │  │  Executor       │  │               │  │  (bg worker)    │   │   │
│  │  └────────┬───────┘  └───────┬───────┘  └────────┬────────┘   │   │
│  │           │                  │                    │            │   │
│  │  ┌────────▼──────────────────▼────────────────────▼────────┐  │   │
│  │  │  RegexTrigrams   FullScanIndexer   DebouncingEventQueue │  │   │
│  │  │  TextDecoder     PathGlob          RepoWatcher          │  │   │
│  │  │  LanguageGuess   MatchExtraction   HeadPoller           │  │   │
│  │  │  FileContent     ExcludeFilter     IndexOptimizer       │  │   │
│  │  │  Provider                          ReconciliationSched  │  │   │
│  │  └─────────────────────────────────────────────────────────┘  │   │
│  └───────────────────────────────────────────────────────────────┘   │
│                                                                      │
│  ┌────────────────────────────────────────────────────────────────┐  │
│  │                      Indexed.Git                               │  │
│  │  GitProcess (retry, timeout, env sanitization)                 │  │
│  │  GitRepository (ls-files, diff-tree, check-attr, rev-parse)   │  │
│  └────────────────────────────────────────────────────────────────┘  │
│                                                                      │
│  ┌───────────────────────┐                                          │
│  │  IdleExitTimer         │  Sleep-resilient monotonic idle timer    │
│  └───────────────────────┘                                          │
└─────────────────────────────────────────────────────────────────────┘
```

## 2. Project structure and dependencies

Five source projects and five test projects:

```
Indexed.Abstractions  ← no dependencies; DTOs only
    ↑
Indexed.Git           ← references Abstractions
    ↑
Indexed.Core          ← references Abstractions + Git
    ↑                    NuGet: Microsoft.Data.Sqlite 9.0.8
    │                           Microsoft.Extensions.Logging.Abstractions 9.0.0
    ↑
Indexed.Service       ← references Abstractions + Core + Git
    ↑                    NuGet: Microsoft.Extensions.Logging 9.0.0
    │                           Microsoft.Extensions.Logging.Console 9.0.0
    ↑
Indexed.Cli           ← references Abstractions + Git + Service
```

All projects target `net10.0-windows`, with nullable reference types enabled and warnings treated as errors in Release.

### Layer ownership

| Layer | Project | Owns | Must NOT |
|-------|---------|------|----------|
| Contracts | `Indexed.Abstractions` | All DTOs, enums, JSON context | Depend on other Indexed projects |
| Git adapter | `Indexed.Git` | `git.exe` invocation, repo operations | Know about FTS5, trigrams, or SQL |
| Index engine | `Indexed.Core` | SQLite schema, FTS5 wrapper, query planning, full/incremental indexing, debouncing, file watching | Call HTTP, know about daemon lifecycle |
| Service | `Indexed.Service` | Daemon bootstrap, HTTP surface, idle-exit, lifecycle | Contain query or indexing logic |
| CLI | `Indexed.Cli` | Argument parsing, daemon launch, output formatting | Contain any indexing logic |

## 3. Data model

### 3.1 Repository identity

Each repository gets a stable identifier:

```
repoId = SHA1( absolutePath(repoRoot) + "\0" + firstCommitSha )[0:12]
```

The first-commit SHA anchors identity across worktree moves. State lives under:

```
%LOCALAPPDATA%\Indexed\<repoId>\
    daemon.json     Port, PID, startup time, shutdown token
    index.db        SQLite database: files, FTS5 tables, metadata
    logs/           Daily-rotated structured logs
```

### 3.2 SQLite schema (version 2)

One database per repository. WAL journal mode for concurrent reads during writes.

```sql
-- Authoritative file-to-path mapping and content identity
CREATE TABLE files (
    file_id     INTEGER PRIMARY KEY,
    path        TEXT UNIQUE NOT NULL,   -- repo-relative POSIX path
    mtime_utc   INTEGER NOT NULL,       -- Unix timestamp
    size_bytes  INTEGER NOT NULL,
    sha256      BLOB NOT NULL,          -- content hash for change detection
    language    TEXT,                    -- advisory: 'csharp', 'python', etc.
    indexed_at  INTEGER NOT NULL        -- Unix timestamp
);
CREATE INDEX files_path_glob ON files(path);

-- Code index: contentless trigram FTS5 (schema v2)
-- Rowid = file_id; the posting list is kept, the text itself is not stored.
-- Snippets are rehydrated from the working tree at query time via
-- FileContentProvider.
CREATE VIRTUAL TABLE code_fts USING fts5(
    content,
    content = '',
    contentless_delete = 1,
    tokenize = 'trigram'
);

-- Prose index: porter+unicode61 tokenizer for stemmed word search
-- One row per extracted prose span (Stage 3). Content stored.
CREATE VIRTUAL TABLE prose_fts USING fts5(
    content,
    kind         UNINDEXED,
    start_line   UNINDEXED,
    end_line     UNINDEXED,
    file_id      UNINDEXED,
    tokenize = 'porter unicode61'
);

-- Small KV for schema version, repo identity, indexed HEAD, timestamps
CREATE TABLE meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
```

Meta keys:

| Key | Value |
|-----|-------|
| `schema_version` | `"2"` |
| `repo_id` | 12-hex-char repository identifier |
| `indexed_head` | 40-char SHA of the last fully indexed HEAD commit |
| `last_full_scan_at` | Unix timestamp of last full scan completion |

Schema changes are breaking: `SqliteIndex` detects version mismatch, deletes `index.db`, and recreates from scratch.

Schema version history:

| Version | Change | Rationale |
|---------|--------|-----------|
| 1 | Initial: `code_fts` stored full content. | — |
| 2 | `code_fts` becomes contentless (`content = ''`, `contentless_delete = 1`). | Cuts index size by ~source tree size. Requires SQLite 3.43+ for `contentless_delete`. Snippets come from disk at query time. |

### 3.3 File set

The indexed file set is defined by git:

```
FileSet = (git ls-files) ∪ (git ls-files --others --exclude-standard)
```

Including untracked-but-not-ignored files ensures that new files written by an agent are searchable before they are committed.

A file is excluded from indexing if any of:

1. Size exceeds 50 MiB (`MaxIndexableFileBytes`).
2. First 8 KiB contains a NUL byte (binary heuristic).
3. `git check-attr binary` reports `binary: set`.
4. Path matches the daemon's exclude glob list.

Exclude globs are composed at daemon startup from two sources:

- **Built-in default list** (`ExcludeFilter.DefaultBinaryAdjacentGlobs`, enabled unless `--no-default-excludes`): lockfiles (`package-lock.json`, `yarn.lock`, `pnpm-lock.yaml`, `Cargo.lock`, `Gemfile.lock`, `composer.lock`, `poetry.lock`), minified JS/CSS bundles (`*.min.js`, `*.min.css`), source maps (`*.map`), generated C# (`*.Designer.cs`, `*.g.cs`, `*.g.i.cs`), and binary-adjacent tooling noise. Cuts the FTS5 trigram index substantially on repos that check in any of these (see `Indexed-Size-Reduction-SafeNearTerm-Plan.md §Workstream A`).
- **User-supplied globs** via `--exclude-index <glob>` (repeatable).

The two lists are concatenated (defaults first) and applied uniformly by the full-scan indexer, the incremental indexer, and the repo watcher, so a newly dropped lockfile is ignored at every entry point.

## 4. Indexing pipeline

### 4.1 Full scan (startup)

`FullScanIndexer` runs on first startup when the index is empty:

1. Enumerate files via `GitRepository.EnumerateFiles()` (tracked + untracked).
2. Batch binary detection via `git check-attr -z binary --stdin`.
3. For each non-binary, non-excluded file:
   - Compute SHA-256; skip if unchanged from `files.sha256`.
   - Read bytes; decode via `TextDecoder` (BOM-aware: UTF-32 LE/BE, UTF-16 LE/BE, UTF-8 BOM, UTF-8 fallback).
   - UPSERT into `files` + `code_fts` within a transaction.
4. Batch size: 200 files or 250 ms per transaction.
5. On completion, write `indexed_head` and `last_full_scan_at` to `meta`.

### 4.2 Incremental indexer (background worker)

`IncrementalIndexer` is a single background `Task` that drains `DebouncingEventQueue`. It is the sole writer to `SqliteIndex` after startup.

Event types:

| Event | Source | Processing |
|-------|--------|------------|
| `FileChanged(path)` | FSW, reconciliation | Read bytes, SHA-compare, UPSERT if changed |
| `FileDeleted(path)` | FSW, reconciliation | Look up `file_id`, DELETE from `files` + `code_fts` |
| `HeadMoved(old, new)` | HeadPoller | `git diff-tree -r --name-status -z`, expand A/M/D/R/C entries to file events |
| `ReconciliationRequested` | ReconciliationScheduler, FSW error, `/rescan` | Path-set diff: git files vs index; emit corrective events |

Transaction model: deletes and upserts each run in their own `WriterScope`. If an exception occurs, `scope.Fail()` triggers rollback. The `indexed_head` meta update runs in a dedicated scope.

### 4.3 Event sources

#### FileSystemWatcher (`RepoWatcher`)

- Recursive, 64 KB internal buffer.
- Excludes `.git/` and configured exclude globs.
- Normalizes paths to repo-relative POSIX (forward slashes).
- Rename events emit `FileDeleted(oldPath)` + `FileChanged(newPath)`.
- On FSW error (buffer overflow), enqueues `ReconciliationRequested` as fallback.

#### HEAD poller (`HeadPoller`)

- 1-second timer.
- Cheap path: stats `.git/index` mtime first — `git rev-parse HEAD` is spawned only when mtime changes.
- Compares HEAD SHA against `_lastKnownHead`; emits `HeadMoved` on difference.
- Logarithmic backoff on consecutive errors (warns at power-of-2 counts).

#### Reconciliation scheduler (`ReconciliationScheduler`)

- 5-minute timer that enqueues `ReconciliationRequested`.
- Safety net for dropped FSW events, external git operations, files modified while daemon was stopped.

### 4.4 Debouncing (`DebouncingEventQueue`)

Two cooperating windows:

- **Per-path** (250 ms default): rapid saves to the same file collapse into one event; last event wins.
- **Global batch** (500 ms default): after the first event, waits up to the batch window for more events before emitting. Maximum batch size: 200 events.

`HeadMoved` and `ReconciliationRequested` bypass per-path debouncing and are forwarded to the consumer first — before per-path events — so that diff-tree results supersede stale FSW events.

Thread-safe `Enqueue` via `Channel<IndexEvent>`; single-consumer `DequeueAsync`.

## 5. Query engine

### 5.1 Code query planning (`CodeQueryPlanner`)

Converts a `SearchRequest` into a `CodeQueryPlan`:

- **Literal pattern**: Extract all 3-byte trigram windows from the lowercase pattern, AND them into an FTS5 MATCH expression.
- **Regex pattern**: Parse the regex into an AST (`RegexParser`), walk it with `TrigramAnalyzer` (Russ Cox's algorithm) to compute required trigram sets, emit as a `TrigramExpr` boolean tree. If no trigrams can be extracted (e.g., `.*`), flag as full scan.
- Output: FTS5 MATCH expression + optional compiled `Regex` for verification.

### 5.2 Regex-to-trigram analysis (`RegexTrigrams`)

A Russ Cox-style analyzer in four components:

1. **`RegexParser`** — Hand-rolled recursive-descent parser producing a minimal AST: `Literal`, `CharClass`, `AnyChar`, `Concat`, `Alt`, `Repeat`, `Anchor`, `Opaque`. Unsupported features (backreferences, lookaround) become `OpaqueNode`.

2. **`TrigramAnalyzer`** — Walks the AST computing exact/prefix/suffix string sets per node. At concat junctions, crosses boundary strings to extract trigrams. Result is a `TrigramExpr` (boolean tree).

3. **`TrigramExpr`** — Boolean expression tree: `Literal(string)`, `And(exprs)`, `Or(exprs)`, `True` (sentinel). `ToFts5MatchExpression()` emits FTS5 `MATCH` syntax.

4. **`BoundedStringSet`** — Fixed-capacity set (max 64 strings, max 32 chars each) with `Union`, `CrossConcat`, and `Infinite` sentinel. Prevents combinatorial explosion on complex alternations.

### 5.3 Code query execution (`CodeQueryExecutor`)

1. If the plan has an FTS5 expression: query `code_fts` for candidate `file_id` values.
2. Fetch `(file_id, path, sha256)` rows in batches of 256 files. No content column — schema v2 is contentless.
3. If a path glob is specified: filter candidates against `files.path`.
4. For each candidate: read the file from disk via `FileContentProvider` (rooted at the repo root). If the file is missing, unreadable, or oversize (> `MaxIndexableFileBytes`), drop the candidate and enqueue a `FileChanged` repair so the incremental indexer converges.
5. Run the compiled `Regex` (regex mode) or `string.IndexOf` (literal mode) against the live on-disk content.
6. Extract line, column, byte offset, context lines via `MatchExtraction`.
7. Apply per-file cap (`maxMatchesPerFile`, default 20) and global cap (`maxMatches`, default 200).
8. Enforce timeout (`timeoutMs`, default 2000 ms).

The FTS5 posting list is a *candidate oracle*, not the source of truth for match text. Three staleness classes are bounded:

- **Stale candidate** (index ahead of disk edits): the disk scan naturally returns zero hits for content that no longer exists. Caller sees fewer matches; the indexer catches up.
- **Missing file** (index references a deleted/renamed file): executor drops the candidate and enqueues `FileChanged(path)` so the incremental indexer deletes the row on its next batch.
- **Fresh edit** (file modified after the last successful index): the scanner operates on the new content; trigrams that previously matched may no longer be present, or vice versa. No regression from v1.

### 5.4 Search modes

| Mode | Engine | Status |
|------|--------|--------|
| `code` | FTS5 trigram → candidate files → regex/literal scan | Implemented |
| `prose` | FTS5 porter+unicode61 → stemmed word search | Returns `NotImplemented` (Stage 3) |
| `auto` | Merge code + prose results | Returns `NotImplemented` for prose component |

## 6. Daemon lifecycle (`DaemonHost`)

### 6.1 Startup sequence

1. **Open repository**: `GitRepository.Open(repoRoot)` walks up to find `.git`.
2. **Compute repo ID**: `SHA1(abspath + "\0" + firstCommitSha)[0:12]`.
3. **Resolve paths**: `%LOCALAPPDATA%\Indexed\<repoId>\{daemon.json, index.db, logs/}`. The local (non-roaming) application data folder is used because `index.db` is a machine-specific, reconstructible-from-source derived artifact that must not replicate across devices.
4. **Acquire mutex**: `Global\Indexed-<repoId>` named mutex (Windows). Handles `AbandonedMutexException` from crashed predecessors.
5. **Bind listener**: `TcpListener` on `127.0.0.1:0` for OS-assigned ephemeral port; transfer to `HttpListener`.
6. **Open index**: `SqliteIndex.OpenOrCreate(index.db)`. On corruption or schema mismatch: delete and recreate.
7. **Full scan** (if index empty): run `FullScanIndexer`.
8. **Construct the content provider**: `FileContentProvider` rooted at the repo. Contentless FTS5 means the search backend rehydrates match text from disk, not from the index.
9. **Start incremental pipeline**:
   - `DebouncingEventQueue` (event broker; also receives repair events from the query executor)
   - `IncrementalIndexer` (background worker)
   - `RepoWatcher` (FSW)
   - `HeadPoller` (1-second timer)
   - `ReconciliationScheduler` (5-minute timer)
10. **Start the background optimizer** (`IndexOptimizer`): a 15-minute timer (configurable via `DaemonOptions.OptimizerInterval`) that runs bounded FTS5 segment merges. Each tick calls `SqliteIndex.RunFts5MergeAsync(pageBudget)` (default 512 pages) — enough to reclaim fragmentation from recent batch commits without stalling the writer. Skipped entirely when the writer has been idle since the previous tick.
11. **Write `daemon.json`**: atomic temp-file + rename. Contains port, PID, repo info, shutdown token.
12. **Start idle-exit timer**: fires callback after 30 minutes of no activity.

### 6.2 Request loop

HTTP dispatch on `127.0.0.1:<port>`:

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/status` | GET | Health check: daemon version, schema version, PID, freshness |
| `/search` | POST | Query execution (JSON body: `SearchRequest`) |
| `/rescan` | POST | Enqueue `ReconciliationRequested` event |
| `/shutdown` | POST | Authenticated graceful shutdown (`X-Indexed-Shutdown-Token` header) |

Every `/search` response includes a `freshness` block:

```json
{
  "indexedHead": "abc123...",
  "currentHead": "abc123...",
  "pendingFileCount": 0,
  "lastFullScanAt": "2026-04-15T10:00:00Z",
  "isStale": false
}
```

Staleness rule: `isStale = (pendingFileCount > 0) || (indexedHead != currentHead)`.

### 6.3 Shutdown sequence

1. Cancel the shutdown CTS.
2. Stop the HTTP listener.
3. Delete `daemon.json`.
4. Dispose idle-exit timer.
5. Stop FSW, HEAD poller, reconciliation scheduler.
6. Stop the `IndexOptimizer` (fires one final generous merge — `pageBudget = 1024` — to consolidate the segment tree before the WAL checkpoint).
7. Drain the incremental indexer (wait for current batch to commit or abort).
8. Dispose the event queue.
9. Dispose `SqliteIndex` (runs `PRAGMA wal_checkpoint(TRUNCATE)` to bound WAL file size).
10. Release and dispose the singleton mutex.

### 6.4 Idle-exit timer (`IdleExitTimer`)

Sleep-resilient timer using monotonic `Environment.TickCount64`:

- `Poke()` resets the countdown. Called on every HTTP request and every `BatchCommitted` event.
- `OnTimer()` checks actual elapsed time since last poke against the window. If the machine slept through the deadline (elapsed < window), reschedules for the remaining duration instead of firing.
- The callback fires exactly once; subsequent pokes are no-ops.

## 7. Concurrency model

- **Single-writer serialization**: `IncrementalIndexer` and `IndexOptimizer` are the only writers. Both acquire `SqliteIndex`'s writer semaphore via `BeginWriteAsync`, so their transactions are strictly serialized even though they run on independent timers. A tick from the optimizer cannot interleave with an indexer batch.
- **Concurrent readers**: WAL-mode readers proceed concurrently with the writer. Each `/search` query opens a short-lived read transaction that sees a consistent snapshot.
- **Thread-safe event ingestion**: `DebouncingEventQueue.Enqueue()` is safe to call from any thread (FSW callbacks, timer callbacks, HTTP threads, and the query executor when it produces repair events for missing files).
- **Eventual consistency**: `freshness.isStale` tells callers whether the index is behind. There is no query-time wait for indexing to catch up.

## 8. Git process management (`GitProcess`)

All `git.exe` invocations go through `GitProcess`, which provides:

- **Environment sanitization**: Removes poisonous env vars (`GIT_DIR`, `GIT_WORK_TREE`, `GIT_INDEX_FILE`, `GIT_OBJECT_DIRECTORY`, `GIT_ALTERNATE_OBJECT_DIRECTORIES`, `GIT_CEILING_DIRECTORIES`, `GIT_COMMON_DIR`) that could misdirect the subprocess.
- **UTF-8 forcing**: Sets `GIT_OUTPUT_ENCODING=utf-8` to ensure consistent output encoding.
- **Lock retry**: Detects `index.lock` contention (another git process holding the lock) and retries up to 4 times with exponential backoff (100 ms, 200 ms, 400 ms, 800 ms).
- **Process timeout**: 60-second hard cap with `WaitForExit(timeout)` and process kill on timeout.
- **NUL-delimited output**: Most git commands use `-z` for unambiguous parsing of paths containing special characters.

## 9. Text decoding (`TextDecoder`)

BOM detection order (matching ICU conventions):

1. UTF-32 LE: `FF FE 00 00`
2. UTF-32 BE: `00 00 FE FF`
3. UTF-16 LE: `FF FE` (when not followed by `00 00`)
4. UTF-16 BE: `FE FF`
5. UTF-8 BOM: `EF BB BF`
6. Fallback: UTF-8 without BOM

Non-decodable bytes in the UTF-8 fallback path produce U+FFFD replacement characters, ensuring every file produces a valid string.

## 10. Productionization hardening (Stage 5)

### DB corruption recovery

`SqliteIndex.OpenOrCreate` catches `SqliteException` during initial writer-connection open and pragma application. On failure: deletes the corrupt `index.db` and recreates from scratch.

### WAL checkpoint on shutdown

Runs `PRAGMA wal_checkpoint(TRUNCATE)` during `SqliteIndex.DisposeAsync()` to bound the `-wal` file size and ensure clean state for next startup.

### Transactional rollback

Both delete and upsert scopes in `IncrementalIndexer` wrap their work in `try/catch`. On exception, `scope.Fail()` rolls back the SQLite transaction, preserving the previous consistent state.

### Abandoned mutex recovery

`DaemonHost.AcquireMutex()` catches `AbandonedMutexException` (from a predecessor that was killed without releasing the mutex), logs a warning, and proceeds — the mutex is now owned by this process.

### Error resilience in event sources

- `HeadPoller`: logarithmic backoff on consecutive errors; only warns at power-of-2 counts to avoid log flooding.
- `ReconciliationScheduler`: catches `InvalidOperationException` when the channel is completed during shutdown.
- `RepoWatcher`: on FSW error, enqueues `ReconciliationRequested` instead of crashing.

## 11. Failure modes and recovery

| Failure | Detection | Recovery |
|---------|-----------|----------|
| Database corruption | `SqliteException` on open/pragma | Delete `index.db`; full rescan on next startup |
| Schema version mismatch | `meta.schema_version` != current | Delete `index.db`; recreate from scratch |
| Crash mid-indexing | WAL checkpoint on next open | Last committed transaction is durable; startup rescan catches in-flight work |
| FSW buffer overflow | `FileSystemWatcher.Error` event | Enqueue `ReconciliationRequested`; 5-minute periodic safety net |
| HEAD change (branch switch, reset, rebase) | `.git/index` mtime + `git rev-parse HEAD` | `git diff-tree` to compute minimal changeset |
| `git index.lock` contention | Error message detection | Exponential backoff retry (up to 4 attempts) |
| File locked by another process | `IOException` / `UnauthorizedAccessException` | Skip with diagnostic log; reconciliation picks up later |
| Process timeout (git) | 60-second `WaitForExit` | Kill process; report error to caller |
| Sleep/resume messing with timers | `IdleExitTimer` checks actual elapsed time | Reschedules for remaining duration instead of firing |
| Daemon crash (power loss, taskkill /f) | Stale `daemon.json` + abandoned mutex | CLI probes `/status`; refused → launches new daemon; mutex handles `AbandonedMutexException` |
| Disk full during commit | `SqliteException` during transaction | Abort commit; previous state preserved |

## 12. Security model

- **Localhost-only binding**: `HttpListener` on `127.0.0.1`.
- **No authentication** for read endpoints (`/status`, `/search`).
- **Shutdown token**: `/shutdown` requires `X-Indexed-Shutdown-Token` header matching a 32-byte random token generated at startup and written to `daemon.json`.
- **Path containment**: Only reads files under the repo root.
- **No outbound network**: No remote connections.
- **Read-only repo access**: Files opened read-only; writable state under `%LOCALAPPDATA%\Indexed\<repoId>\`.

## 13. Performance targets

| Workload | Target |
|----------|--------|
| Literal identifier query, < 100 matches | ≤ 10 ms |
| Regex with strong literal anchor (`class\s+FooBar`) | ≤ 30 ms |
| Regex with weak trigrams (`..foo..`) | ≤ 250 ms |
| Cold startup with warm `index.db` | ≤ 2 s |
| Cold rebuild from scratch | ≤ 60 s for this repo |
| Index size | ≤ 1.2× indexed source bytes (contentless FTS5 + default excludes + idle-time optimizer) |
| Single-file save → queryable | ≤ 2 s |
| Branch switch (50-file diff) → queryable | ≤ 5 s |

## 14. Test coverage

313 tests across 5 test projects:

| Test project | Tests | Coverage |
|-------------|-------|----------|
| `Indexed.Abstractions.Tests` | 32 | JSON serialization round-trips for all DTOs |
| `Indexed.Git.Tests` | 26 | Diff-tree parsing, untracked file listing, index mtime, rename handling |
| `Indexed.Core.Tests` | 165 | SQLite operations (incl. contentless FTS5 shadow-table invariants), full scan, incremental indexer (FSW, HEAD, reconciliation), query planning, regex trigrams, debouncing, text decoder, path glob, exclude filter, index optimizer, disk-read snippet rehydration |
| `Indexed.Service.Tests` | 54 | HTTP contract, daemon info, idle-exit timer, repo ID, default-exclude wiring, optimizer integration |
| `Indexed.Cli.Tests` | 36 | Argument parsing, output formatting, exclude-default flag plumbing |

Integration tests in `Indexed.Core.Tests`, `Indexed.Git.Tests`, and `Indexed.Service.Tests` spin up real git repositories in temp directories and exercise end-to-end scenarios.

## 15. Future work (Stage 3 and beyond)

### Stage 3 — Prose extraction

- `Indexed.Extractors` project with Roslyn C# extractor + regex extractors.
- XML doc comment stripping (tag names removed, inner text preserved, `cref` targets as tokens).
- `prose_fts` table populated with per-span rows.
- `mode: "prose"` and `mode: "auto"` fully operational.

### Stage 6 (optional)

- Tree-sitter extractors for languages where regex false positives hurt.
- String-literal extraction for stemmed search of error messages.
- Lucene.NET migration if per-field relevance becomes necessary.
- AOT publishing for the CLI.
