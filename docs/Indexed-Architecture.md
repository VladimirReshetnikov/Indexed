# Indexed — Architecture

- Created (UTC): 2026-04-15T17:00:00Z
- Repository HEAD: cd463ca87356b067e49fe274a1ebcb6e92376c1d
- Status: Current-state architecture for the Indexed full-text search service. Covers Stages 0–5 as implemented, including prose extraction and truthful `auto` mode.

## 1. System overview

Indexed is a background-indexed full-text search service for a single local workspace target. A target can be:

- a git repository (`git-repo`);
- a standalone directory tree (`directory-tree`);
- an explicit set of disjoint labeled roots (`directory-set`).

It runs as a long-lived daemon process (`Indexed.Service`) with an HTTP/JSON surface on `127.0.0.1`, and a thin CLI client (`idx`).

Primary consumers are AI coding agents. The service is designed for:

- **Millisecond-class code search** via SQLite FTS5 with trigram tokenization.
- **Regex search** via a Russ Cox–style trigram narrowing + .NET `Regex` verification.
- **Eventually-consistent incremental updates** via `FileSystemWatcher`, optional git HEAD polling, and periodic reconciliation.
- **Crash-safe persistence** via SQLite WAL mode with single-writer concurrency.

### Architecture diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                          idx CLI (Indexed.Cli)                      │
│  Argument parsing → target selection → DaemonClient → formatting   │
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
│  │  │  TextDecoder     PathGlob          DirectoryWatcher     │  │   │
│  │  │  LanguageGuess   MatchExtraction   HeadPoller           │  │   │
│  │  │  FileContent     ExcludeFilter     IndexOptimizer       │  │   │
│  │  │  Provider                          ReconciliationSched  │  │   │
│  │  └─────────────────────────────────────────────────────────┘  │   │
│  └───────────────────────────────────────────────────────────────┘   │
│                                                                      │
│  ┌────────────────────────────────────────────────────────────────┐  │
│  │                    Indexed.Targets                             │  │
│  │  TargetSpec, TargetId, directory targets, logical-path rules  │  │
│  └────────────────────────────────────────────────────────────────┘  │
│                                                                      │
│  ┌────────────────────────────────────────────────────────────────┐  │
│  │                      Indexed.Git                               │  │
│  │  GitProcess (retry, timeout, env sanitization)                 │  │
│  │  GitRepository + GitIndexTarget                                │  │
│  │  (ls-files, diff-tree, check-attr, rev-parse)                  │  │
│  └────────────────────────────────────────────────────────────────┘  │
│                                                                      │
│  ┌───────────────────────┐                                          │
│  │  IdleExitTimer         │  Sleep-resilient monotonic idle timer    │
│  └───────────────────────┘                                          │
└─────────────────────────────────────────────────────────────────────┘
```

## 2. Project structure and dependencies

Seven source projects and six test projects:

```
Indexed.Targets       ← no dependencies; target contracts and identity
    ↑
Indexed.Abstractions  ← references Targets; DTOs + JSON context
    ↑
Indexed.Extractors    ← references Abstractions
    ↑                    NuGet: Microsoft.CodeAnalysis.CSharp 4.14.0
    ↑
Indexed.Git           ← references Targets
    ↑
Indexed.Core          ← references Abstractions + Extractors + Git + Targets
    ↑                    NuGet: Microsoft.Data.Sqlite 9.0.8
    │                           Microsoft.Extensions.Logging.Abstractions 9.0.0
    ↑
Indexed.Service       ← references Abstractions + Core + Git + Targets
    ↑                    NuGet: Microsoft.Extensions.Logging 9.0.0
    │                           Microsoft.Extensions.Logging.Console 9.0.0
    ↑
Indexed.Cli           ← references Abstractions + Git + Service
```

All projects target `net10.0-windows`, with nullable reference types enabled and warnings treated as errors in Release.

### Layer ownership

| Layer | Project | Owns | Must NOT |
|-------|---------|------|----------|
| Targets | `Indexed.Targets` | Target contracts, canonical target specs, target ids, directory targets, logical-path mapping | Depend on Git or SQLite |
| Contracts | `Indexed.Abstractions` | All DTOs, enums, JSON context | Depend on Core, Service, or Git |
| Extraction | `Indexed.Extractors` | Roslyn and regex-based prose extraction, span normalization | Know about daemon lifecycle or SQL |
| Git adapter | `Indexed.Git` | `git.exe` invocation, repo operations, git-backed target implementation | Know about FTS5, trigrams, or SQL |
| Index engine | `Indexed.Core` | SQLite schema, FTS5 wrapper, query planning, full/incremental indexing, debouncing, file watching | Call HTTP, know about daemon lifecycle |
| Service | `Indexed.Service` | Daemon bootstrap, HTTP surface, idle-exit, lifecycle | Contain query or indexing logic |
| CLI | `Indexed.Cli` | Argument parsing, daemon launch, output formatting | Contain any indexing logic |

## 3. Data model

### 3.1 Target identity

Each served target gets a stable identifier:

- **Legacy git-default compatibility case**: `repoId = SHA1( absolutePath(repoRoot) + "\0" + firstCommitSha )[0:12]`
- **All other targets**: `targetId = SHA1(canonical-target-spec-byte-stream)[0:12]`

The target-spec byte stream encodes target kind, normalized roots, exclude settings, and the directory-default switch. State lives under:

```
%LOCALAPPDATA%\Indexed\<targetId>\
    daemon.json     Port, PID, target metadata, shutdown token
    index.db        SQLite database: roots, files, FTS5 tables, metadata
    logs/           Daily-rotated structured logs
```

For compatibility, git-default targets expose both `targetId` and `repoId`, and they are equal.

### 3.2 SQLite schema (version 3)

One database per target. WAL journal mode for concurrent reads during writes.

```sql
CREATE TABLE roots (
    root_id        INTEGER PRIMARY KEY,
    root_name      TEXT,
    absolute_path  TEXT UNIQUE NOT NULL,
    is_primary     INTEGER NOT NULL
);

CREATE TABLE files (
    file_id        INTEGER PRIMARY KEY,
    root_id        INTEGER NOT NULL REFERENCES roots(root_id),
    relative_path  TEXT NOT NULL,
    logical_path   TEXT UNIQUE NOT NULL,
    mtime_utc      INTEGER NOT NULL,
    size_bytes     INTEGER NOT NULL,
    sha256         BLOB NOT NULL,
    language       TEXT,
    indexed_at     INTEGER NOT NULL,
    UNIQUE(root_id, relative_path)
);

CREATE VIRTUAL TABLE code_fts USING fts5(
    content,
    content = '',
    contentless_delete = 1,
    tokenize = 'trigram'
);

CREATE VIRTUAL TABLE prose_fts USING fts5(
    content,
    kind         UNINDEXED,
    start_line   UNINDEXED,
    end_line     UNINDEXED,
    file_id      UNINDEXED,
    tokenize = 'porter unicode61'
);

CREATE TABLE meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
```

Meta keys:

| Key | Value |
|-----|-------|
| `schema_version` | `"3"` |
| `target_id` | 12-hex-char target identifier |
| `repo_id` | 12-hex-char repository identifier when the target is a compatible git default |
| `indexed_head` | Git HEAD SHA of the last fully indexed revision (git targets only) |
| `last_full_scan_at` | Unix timestamp of last full scan completion |
| `last_reconciliation_at` | Unix timestamp of the last reconciliation pass |

Schema changes are breaking: `SqliteIndex` detects version mismatch, deletes `index.db`, and recreates from scratch.

Schema version history:

| Version | Change | Rationale |
|---------|--------|-----------|
| 1 | Initial: `code_fts` stored full content. | — |
| 2 | `code_fts` becomes contentless (`content = ''`, `contentless_delete = 1`). | Cuts index size by ~source tree size. Requires SQLite 3.43+ for `contentless_delete`. Snippets come from disk at query time. |
| 3 | Add `roots`, replace path-only `files` identity with `root_id + relative_path + logical_path`. | Enables directory-tree and directory-set targets while preserving the git logical-path namespace. |

### 3.3 File set

The indexed file set depends on target kind:

- **Git repository**: `(git ls-files) ∪ (git ls-files --others --exclude-standard)`
- **Directory tree**: recursive filesystem walk rooted at the selected directory
- **Directory set**: recursive filesystem walk unioned across all selected roots

Including untracked-but-not-ignored git files ensures that new files written by an agent are searchable before they are committed. Directory targets do not depend on git at all.

A file is excluded from indexing if any of:

1. Size exceeds 50 MiB (`MaxIndexableFileBytes`).
2. First 8 KiB contains a NUL byte (shared binary heuristic).
3. `git check-attr binary` reports `binary: set` (git targets only).
4. Logical path matches the daemon's exclude glob list.

Exclude globs are composed at daemon startup from up to three sources:

- **Built-in index-shaping list** (`ExcludeFilter.DefaultBinaryAdjacentGlobs`, enabled unless `--no-default-excludes`): lockfiles, minified bundles, source maps, generated C#, and other trigram-index bloat.
- **Built-in directory-mode safety/perf list** (`ExcludeFilter.DefaultDirectoryModeExcludes`, enabled for directory targets unless `--no-default-directory-excludes`): VCS metadata, dependency caches, IDE state, common build outputs, and platform noise.
- **User-supplied globs** via `--exclude-index <glob>` (repeatable).

The lists are concatenated and applied uniformly by the full-scan indexer, the incremental indexer, and `DirectoryWatcher`, so a newly created excluded file is ignored at every entry point.

## 4. Indexing pipeline

### 4.1 Full scan (startup)

`FullScanIndexer` runs on first startup when the index is empty:

1. Enumerate files via `IIndexTarget.EnumerateFilesAsync()`.
2. If the target exposes explicit binary overrides (git mode via `.gitattributes`), batch them before content reads.
3. For each non-binary, non-excluded file:
   - Resolve `root_id`, `relative_path`, and `logical_path`.
   - Compute SHA-256; skip if unchanged from `files.sha256`.
   - Read bytes; decode via `TextDecoder` (BOM-aware: UTF-32 LE/BE, UTF-16 LE/BE, UTF-8 BOM, UTF-8 fallback).
   - Run the extension-mapped extraction pipeline (XML docs, line comments, block comments, Markdown, plain text).
   - UPSERT into `files` + `code_fts`, then replace that file's `prose_fts` rows within the same transaction.
4. Batch size: 200 files or 250 ms per transaction.
5. On completion, write `indexed_head` / revision token (when present) and `last_full_scan_at` to `meta`.

### 4.2 Incremental indexer (background worker)

`IncrementalIndexer` is a single background `Task` that drains `DebouncingEventQueue`. It is the sole writer to `SqliteIndex` after startup.

Event types:

| Event | Source | Processing |
|-------|--------|------------|
| `FileChanged(path)` | FSW, reconciliation | Read bytes, SHA-compare, UPSERT if changed |
| `FileDeleted(path)` | FSW, reconciliation | Look up `file_id`, DELETE from `files` + `code_fts` |
| `HeadMoved(old, new)` | HeadPoller (git targets only) | `git diff-tree -r --name-status -z`, expand A/M/D/R/C entries to file events |
| `ReconciliationRequested` | ReconciliationScheduler, FSW error, `/rescan` | Path-set diff: target enumeration vs index; emit corrective events |

Transaction model: deletes and upserts each run in their own `WriterScope`. If an exception occurs, `scope.Fail()` triggers rollback. The `indexed_head` meta update runs in a dedicated scope.

### 4.3 Event sources

#### FileSystemWatcher (`DirectoryWatcher`)

- Recursive, 64 KB internal buffer.
- One watcher per target root.
- Excludes configured globs and, for directory targets, the built-in directory-mode defaults.
- Normalizes paths to logical POSIX paths (forward slashes).
- Rename events emit `FileDeleted(oldPath)` + `FileChanged(newPath)`.
- On FSW error (buffer overflow), enqueues `ReconciliationRequested` as fallback.

#### HEAD poller (`HeadPoller`)

- 1-second timer.
- Git targets only.
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
2. Fetch `(file_id, logical_path, sha256)` rows in batches of 256 files. No content column — the code index is contentless.
3. If a path glob is specified: filter candidates against `files.logical_path`.
4. For each candidate: read the file from disk via `FileContentProvider` (rooted at the selected target). If the file is missing, unreadable, or oversize (> `MaxIndexableFileBytes`), drop the candidate and enqueue a repair event so the incremental indexer converges.
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
| `prose` | FTS5 porter+unicode61 → stemmed word search over extracted spans | Implemented |
| `auto` | Run both surfaces when meaningful, then merge with prose-preferred same-line dedupe | Implemented |

## 6. Daemon lifecycle (`DaemonHost`)

### 6.1 Startup sequence

1. **Resolve target**: git repository, directory tree, or directory set from `DaemonOptions`.
2. **Compute target ID**: legacy `repoId` for the default git case; otherwise the canonical target-spec hash.
3. **Resolve paths**: `%LOCALAPPDATA%\Indexed\<targetId>\{daemon.json, index.db, logs/}`. The local (non-roaming) application data folder is used because `index.db` is a machine-specific, reconstructible-from-source derived artifact that must not replicate across devices.
4. **Acquire mutex**: `Global\Indexed-<targetId>` named mutex (Windows). Handles `AbandonedMutexException` from crashed predecessors.
5. **Bind listener**: `TcpListener` on `127.0.0.1:0` for OS-assigned ephemeral port; transfer to `HttpListener`.
6. **Open index**: `SqliteIndex.OpenOrCreate(index.db)`. On corruption or schema mismatch: delete and recreate.
7. **Construct the content provider**: `FileContentProvider` rooted at the selected target. Contentless FTS5 means the search backend rehydrates match text from disk, not from the index.
8. **Start incremental pipeline**:
   - `DebouncingEventQueue` (event broker; also receives repair events from the query executor)
   - `IncrementalIndexer` (background worker)
   - `DirectoryWatcher` (armed before the initial scan)
   - optional `HeadPoller` (git targets only)
   - `ReconciliationScheduler` (5-minute timer)
9. **Full scan** (if index empty): run `FullScanIndexer`.
10. **Start the background optimizer** (`IndexOptimizer`): a 15-minute timer (configurable via `DaemonOptions.OptimizerInterval`) that runs bounded FTS5 segment merges. Each tick calls `SqliteIndex.RunFts5MergeAsync(pageBudget)` (default 512 pages) — enough to reclaim fragmentation from recent batch commits without stalling the writer. Skipped entirely when the writer has been idle since the previous tick.
11. **Write `daemon.json`**: atomic temp-file + rename. Contains port, PID, target info, repo compatibility metadata (if any), and shutdown token.
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
- `DirectoryWatcher`: on FSW error, enqueues `ReconciliationRequested` instead of crashing.

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
- **Path containment**: Only reads files under the selected target roots; every logical path is re-resolved and checked to remain under its owning root before opening.
- **No outbound network**: No remote connections.
- **Read-only target access**: Files opened read-only; writable state under `%LOCALAPPDATA%\Indexed\<targetId>\`.
- **Directory-mode caveat**: Read endpoints are intentionally unauthenticated, so pointing `--root` at sensitive directories effectively makes them searchable by other local processes that can reach loopback HTTP. Indexed documents this and leaves root choice to the operator.

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

Coverage is organized across six test projects:

| Test project | Coverage |
|-------------|----------|
| `Indexed.Abstractions.Tests` | JSON serialization round-trips for DTO evolution, including target metadata and freshness compatibility fields |
| `Indexed.Extractors.Tests` | Roslyn and regex extractor behavior, normalization, and extension-to-extractor mapping |
| `Indexed.Git.Tests` | Diff-tree parsing, untracked file listing, index mtime, rename handling, and git-target behavior |
| `Indexed.Core.Tests` | SQLite operations (including schema v3 roots/files layout), full scan, incremental indexing, directory targets, reconciliation, query planning, regex trigrams, debouncing, text decoding, globs, excludes, and disk-read snippet rehydration |
| `Indexed.Service.Tests` | HTTP contract, daemon info, idle-exit timer, target selection, daemon catalog, default-exclude wiring, and end-to-end git/directory host scenarios |
| `Indexed.Cli.Tests` | Argument parsing, root-selection grammar, daemon-list formatting, and status/search output formatting |

Integration tests in `Indexed.Core.Tests` and `Indexed.Service.Tests` spin up real directory trees and git repositories in temp directories and exercise end-to-end scenarios for both git and non-git targets.

## 15. Future work

### Stage 6 (optional)

- Tree-sitter extractors for languages where regex false positives hurt.
- String-literal extraction for stemmed search of error messages.
- Lucene.NET migration if per-field relevance becomes necessary.
- AOT publishing for the CLI.
