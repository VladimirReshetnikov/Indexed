# Indexed — Size Reduction Phase 1 (“Safe, Near-Term”) Implementation Plan

- Created (UTC): 2026-04-15T00:00:00Z
- Repository HEAD: 3c2dd76b5e368173bf8b0121b5cd779296d2d79d

## Source proposal

This plan implements §5.1 of
[`Indexed-Index-Size-Reduction-Strategies.md`](./Indexed-Index-Size-Reduction-Strategies.md).
The target is an on-disk `index.db` at **~1.0× the total indexed source bytes**
on canary repositories, down from today's ~1.8×–2.5×. The change set combines
three independently-shippable workstreams:

| # | Workstream | Ref | Ship order |
|---|---|---|---|
| A | Default exclude globs for lockfiles and generated/minified text | §3.1.3 | PR 1 |
| B | Background FTS5 segment merger (“optimizer”) | §3.2.4 | PR 2 |
| C | Contentless FTS5 + disk-read snippet rehydration | §3.1.1 | PR 3 |

Each PR is independently revertable. A and B do not change the schema and are
immediately reversible. C bumps `SqliteSchema.Version`; rollback is a second
version bump and a cold rebuild.

## Goals and non-goals

**Goals.**

- On the three canary repositories (see §8), `index.db` size ≤ 1.0 × total
  indexed source bytes in steady state.
- p50 `/search` latency regression ≤ 20% vs. the pre-Phase-1 baseline.
- p99 `/search` latency regression ≤ 50%.
- No correctness regressions: existing test suite green; all search results
  identical for queries over an unchanged working tree.
- Staleness surfaces as *no match* rather than *wrong snippet*; snippet text
  always reflects on-disk content at query time.

**Non-goals.**

- Changing the tokenizer (§3.2.3). That is Phase 3.
- Compressing content (§3.2.2). That is Phase 2.
- Replacing FTS5 (§3.3). Not in scope for the v1 schema cycle.
- Migrating existing `index.db` files in place. Version bump + rebuild is the
  only migration path (per `SqliteIndex.OpenOrCreate` policy).

## Success criteria / exit checklist

- [ ] New canary size-ratio numbers recorded in `docs/`, with before/after
      table and the specific git SHA of measurement.
- [ ] Existing test suite passes on Debug and Release.
- [ ] New tests listed in §7 all pass.
- [ ] `/search` latency measurement over a fixed 20-query corpus shows
      regression within budget.
- [ ] `Usage-Guide` updated to explain the first-run full rebuild after the
      schema bump and the new exclude defaults.

## Baseline measurement (pre-work, mandatory)

No optimization PR lands until the baseline exists and is committed.

**Canary repos.**

- This repository (`Tools`) — large, mixed C#/docs, moderate vendored text.
- A TypeScript-heavy repo with a `package-lock.json` (size-inflating).
- A Rust repo with a `Cargo.lock` and a `target/` equivalent already excluded.

**Measurement script** (to be added at `src/Indexed/tools/measure-index-size.ps1`):

```powershell
# Run the daemon through initial full-scan, then measure.
# Baseline procedure:
#   1. Delete %APPDATA%\Indexed\<repo-id>\index.db*
#   2. Run `indexed status` — triggers cold start and full scan.
#   3. Record: total source bytes (git ls-files | measure content),
#              index.db size, per-shadow-table size via dbstat.
#   4. Run a fixed 20-query corpus; record p50/p95.
```

**Required outputs.**

- `docs/Indexed-Size-Reduction-Baseline.md` with a table of:
  repo, commit SHA, source-bytes-indexed, `index.db` bytes, ratio,
  per-table breakdown (`SELECT name, SUM(pgsize) FROM dbstat GROUP BY name`),
  and the 20-query latency matrix.

Commit this file to `src/Indexed/docs/` before opening PR 1.

## Workstream A — Default exclude globs (PR 1)

### Intent

Add a curated default list of exclude globs targeting text that inflates the
trigram index without providing search value. User-supplied globs still apply
on top; users can opt out of the defaults entirely.

### Files touched

| File | Change |
|---|---|
| `src/Indexed.Core/ExcludeFilter.cs` | Add `DefaultBinaryAdjacentGlobs` constant list. Add `ExcludeFilter.WithDefaults(userGlobs, useDefaults)` factory. |
| `src/Indexed.Service/DaemonOptions.cs` | Add `bool UseDefaultIndexExcludes { get; init; } = true;`. |
| `src/Indexed.Service/DaemonHost.cs` | Merge `IndexExcludeGlobs` with defaults before passing to indexers/watchers. |
| `src/Indexed.Core/FullScanIndexer.cs`, `IncrementalIndexer.cs`, `RepoWatcher.cs` | No signature change — they already consume pre-merged globs. |
| `src/Indexed.Cli/CliArguments.cs`, `ArgumentParser.cs`, `CliApp.cs`, `DaemonLauncher.cs` | New `--no-default-excludes` flag; default true. |
| `src/Indexed/docs/Indexed-Usage-Guide.md` | Document defaults and opt-out. |

### Default list

```csharp
// ExcludeFilter.cs
public static IReadOnlyList<string> DefaultBinaryAdjacentGlobs { get; } = new[]
{
    // JS/TS lockfiles and minified bundles
    "**/package-lock.json",
    "**/yarn.lock",
    "**/pnpm-lock.yaml",
    "**/npm-shrinkwrap.json",
    "**/*.min.js",
    "**/*.min.css",
    "**/*.map",          // source maps

    // Other ecosystem lockfiles
    "**/Cargo.lock",
    "**/composer.lock",
    "**/Gemfile.lock",
    "**/Pipfile.lock",
    "**/poetry.lock",
    "**/go.sum",
    "**/packages.lock.json",

    // Build-tool generated text
    "**/*.generated.cs",
    "**/*.g.cs",
    "**/*.g.i.cs",
    "**/*.Designer.cs",

    // IDE / tooling caches that are occasionally checked in
    "**/.vs/**",
    "**/.idea/**",
};
```

Rationale for each entry goes in an XML doc comment on the field so the list
is self-documenting.

### Wiring

Single choke point in `DaemonHost.StartAsync`:

```csharp
var mergedExcludes = _options.UseDefaultIndexExcludes
    ? ExcludeFilter.Combine(_options.IndexExcludeGlobs, ExcludeFilter.DefaultBinaryAdjacentGlobs)
    : _options.IndexExcludeGlobs;

var indexer = new FullScanIndexer(_repo, _index, mergedExcludes, _logger);
// ... same merged list to IncrementalIndexer, RepoWatcher
```

`ExcludeFilter.Combine` is a trivial static helper that concatenates two
`IReadOnlyList<string>?` inputs and returns `null` when both are empty — no
behavior change from `null` passthrough.

### Test additions

- `ExcludeFilterTests.DefaultsExcludePackageLockJson`
- `ExcludeFilterTests.DefaultsExcludeMinifiedBundles`
- `ExcludeFilterTests.UserGlobsComposeWithDefaults`
- `ExcludeFilterTests.OptOut_ReturnsOnlyUserGlobs`
- `DaemonHostTests.IndexExcludeDefaults_AppliedWhenFlagTrue` — seeds a repo
  containing `package-lock.json`, confirms post-scan `files` row is absent.
- `DaemonHostTests.IndexExcludeDefaults_NotAppliedWhenOptedOut`.

### Rollback

Revert the PR. No schema change; no data migration.

## Workstream B — Background FTS5 segment merger (PR 2)

### Intent

FTS5 writes new segments every commit. Fragmented posting lists inflate
`code_fts_data` by 10–25% vs. a merged steady state. A low-priority background
task merges segments during idle windows.

### Files touched

| File | Change |
|---|---|
| `src/Indexed.Core/IndexOptimizer.cs` (new) | Timer-based merger. |
| `src/Indexed.Core/SqliteIndex.cs` | Add `RunFts5MergeAsync(int pageBudget, CancellationToken)` public method. |
| `src/Indexed.Service/DaemonHost.cs` | Construct, start, dispose `IndexOptimizer` alongside `IncrementalIndexer`. |
| `src/Indexed.Service/DaemonOptions.cs` | `TimeSpan OptimizerInterval { get; init; } = TimeSpan.FromMinutes(15);` and `int OptimizerPageBudget { get; init; } = 512;`. |
| Tests | See below. |

### Behavior

`IndexOptimizer` is a timer that, on each tick:

1. Checks whether an `IncrementalIndexer` batch commit has occurred since the
   last merge. If not, skip (nothing to do).
2. Acquires a `WriterScope` via `_index.BeginWriteAsync`.
3. Executes `INSERT INTO code_fts(code_fts) VALUES('merge', @pages);` with the
   configured page budget.
4. Executes the same against `prose_fts`.
5. Logs `merged fts pages={budget} elapsed={ms}`.
6. Releases the writer scope (commit).

Page budgets are small on purpose — a single merge call is bounded and cannot
stall the writer lock for more than a few hundred milliseconds. Over time the
background merges approach the effect of a full `optimize` without the stall.

`IncrementalIndexer.BatchCommitted` already exists. `IndexOptimizer` subscribes
to it and flips a `_dirty` flag; the timer tick checks the flag before merging.

### Shutdown behavior

On `DisposeAsync`, the optimizer performs one final merge with double the
normal page budget. This leaves the DB closer to a compact steady state for
the next daemon start. The merge is skipped if `_dirty == false`.

### Interaction with the singleton writer rule

The optimizer is a second writer against `SqliteIndex`. `BeginWriteAsync`
already serializes writers through `_writerLock`, so concurrency is correct.
The only risk is starving the incremental indexer: the optimizer must never
merge more than `OptimizerPageBudget` pages per scope to cap wall-clock
occupation.

### Test additions

- `IndexOptimizerTests.Merge_ReducesSegmentCount` — insert 200 files in
  separate transactions, assert segment count via
  `SELECT level, COUNT(*) FROM code_fts_segdir GROUP BY level` before/after
  an explicit merge.
- `IndexOptimizerTests.SkipsWhenClean` — tick twice without a
  `BatchCommitted`; assert second tick does not open a writer scope.
- `IndexOptimizerTests.CoexistsWithIncrementalIndexer` — run both, assert no
  deadlock (writers serialize correctly).
- `IndexOptimizerTests.DisposeRunsFinalMerge` — dirty flag set, dispose,
  assert a merge ran.

### Rollback

Revert the PR. The timer shutdown is cooperative; no on-disk format change.

## Workstream C — Contentless FTS5 + disk rehydration (PR 3)

This is the disruptive change. Expect a schema version bump and a cold rebuild
on first daemon start after deployment.

### Intent

Drop the FTS5 `_content` shadow table. `code_fts` becomes a contentless
inverted index; text for snippet rendering is read from the working tree at
query time. Expected size drop: **−1× of source** (the stored-content copy
disappears).

### Schema change

`src/Indexed.Core/SqliteSchema.cs`:

```csharp
public const int Version = 2;   // was 1

public const string Ddl = """
    CREATE TABLE files (
        file_id     INTEGER PRIMARY KEY,
        path        TEXT UNIQUE NOT NULL,
        mtime_utc   INTEGER NOT NULL,
        size_bytes  INTEGER NOT NULL,
        sha256      BLOB NOT NULL,
        language    TEXT,
        indexed_at  INTEGER NOT NULL
    );
    CREATE INDEX files_path_glob ON files(path);

    CREATE VIRTUAL TABLE code_fts USING fts5(
        content,
        content = '',            -- contentless: do not store indexed text
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
    -- prose_fts remains with stored content for now; extracted prose chunks
    -- are small and cannot be re-derived without re-running extractors.
    -- A later phase may make prose_fts contentless too.

    CREATE TABLE meta (
        key   TEXT PRIMARY KEY,
        value TEXT NOT NULL
    );
    """;
```

`SqliteIndex.OpenOrCreate` already handles the rebuild: on version mismatch
the DB and its WAL/SHM sidecars are deleted and DDL re-applied. No code
change needed there.

### Writer-side change

`SqliteIndex.UpsertFile` — the `INSERT INTO code_fts(rowid, content)` statement
is **unchanged**. In contentless mode FTS5 tokenizes the supplied value and
stores only posting lists; `content` becomes a write-only parameter.

```csharp
// Semantically identical to today; storage footprint drops dramatically.
cmd.CommandText = "INSERT INTO code_fts(rowid, content) VALUES($id, $content);";
```

`SqliteIndex.DeleteFile` and `BulkDeleteFiles` are unchanged — FTS5 contentless
still supports rowid-keyed deletion.

### Reader-side change

`SqliteIndex.GetFilesAsync` no longer joins against `code_fts` for content.
The method becomes:

```csharp
public async ValueTask<IReadOnlyList<FileRow>> GetFilesAsync(
    IReadOnlyList<long> fileIds,
    CancellationToken cancellationToken)
{
    // Batched SELECT path, file_id, sha256 from files.
    // Content is intentionally empty — callers must call ReadFromDisk.
}

public sealed record FileRow(long FileId, string Path, byte[] Sha256);
// BREAKING: FileRow no longer carries Content.
```

Callers affected:

- `CodeQueryExecutor.ExecuteAsync` — must read content from disk (see below).
- `FullScanIndexerTests.GetFilesAsync_...` — updated assertions.
- `SqliteIndexTests.GetFilesAsync_ReturnsContent` — renamed and reshaped.

### New helper: `FileContentProvider`

`src/Indexed.Core/FileContentProvider.cs` (new):

```csharp
internal sealed class FileContentProvider
{
    private readonly string _repoRoot;

    public FileContentProvider(string repoRoot) { _repoRoot = repoRoot; }

    /// <summary>
    /// Read file content at query time. Returns null when the file is
    /// missing, unreadable, or exceeds the indexable size cap.
    /// Returns the decoded text when readable.
    /// </summary>
    public async ValueTask<string?> ReadAsync(
        string relPath,
        CancellationToken ct)
    {
        var full = Path.Combine(_repoRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists) return null;
            if (info.Length > FullScanIndexer.MaxIndexableFileBytes) return null;
            var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
            return TextDecoder.Decode(bytes);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
```

### `CodeQueryExecutor` rewrite

Today's flow: ask index for candidates → `GetFilesAsync` returns `(path, content)`
→ scan content.

New flow: ask index for candidates → `GetFilesAsync` returns `(path, sha256)`
→ **read content from disk** → scan content.

```csharp
public sealed class CodeQueryExecutor
{
    private readonly SqliteIndex _index;
    private readonly FileContentProvider _contentProvider;
    private readonly DebouncingEventQueue? _repairQueue;

    public CodeQueryExecutor(
        SqliteIndex index,
        FileContentProvider contentProvider,
        DebouncingEventQueue? repairQueue = null)
    { ... }

    public async ValueTask<ExecuteResult> ExecuteAsync(...)
    {
        // ... candidates, per-batch loop unchanged ...
        var rows = await _index.GetFilesAsync(chunk, ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            if (!PathAllowed(row.Path, ...)) continue;

            var content = await _contentProvider.ReadAsync(row.Path, ct).ConfigureAwait(false);
            if (content is null)
            {
                // File gone or unreadable — enqueue repair so the index
                // self-heals. Do not produce a match.
                _repairQueue?.Enqueue(new FileChanged(row.Path));
                continue;
            }

            var hits = ScanFile(request, plan, row.Path, content, out var fileTruncated);
            // ... same cap/truncation logic ...
        }
    }
}
```

### Correctness contract (important)

The FTS5 MATCH has always been a **candidate set**, not a truth oracle. The
scanner verifies every match by running the actual pattern over the content.
Under contentless FTS5, the "content" that the scanner sees is the live
on-disk content, not the indexed-at-time snapshot.

**Behavioral consequence.** A file that was indexed yesterday, matched a
trigram query today, but has since been edited to no longer contain the
pattern, produces zero matches — exactly as it would today when the editor
saves before `/search` runs. The observable difference is a *narrower* class
of false-positive FTS5 candidates being silently dropped; users never see
this.

**Stale-candidate case (index ahead of disk edits).** The indexed trigram
list is for content that no longer exists on disk. Disk scan returns zero
hits for those candidates. Correct.

**Fresh-candidate case (disk ahead of index).** The file on disk contains
matches that the indexer has not yet tokenized. The candidate set may not
include this file. This is the *same* staleness class we live with today —
`/search` has never guaranteed indexing of in-flight edits. Not a
regression.

**Missing-file case.** `ReadAsync` returns `null`; executor enqueues a
`FileChanged` repair and moves on. The `IncrementalIndexer` will observe
the missing file and delete it from the index.

### `DaemonHost.StartAsync` wiring

```csharp
var contentProvider = new FileContentProvider(_repo.RepoRoot);
_backend = new SqliteSearchBackend(_index, BuildFreshness, contentProvider, _eventQueue);
```

`SqliteSearchBackend` plumbs `contentProvider` + `eventQueue` into
`CodeQueryExecutor`. No other consumer needed.

### Prose path (unchanged for now)

`prose_fts` keeps stored content. Prose chunks are:

- Small (paragraph-sized).
- Not trivially re-derivable (they come from an extraction pass — markdown
  section, code-block body, doc comment).
- A small fraction of total index size, so the cost of storing them twice is
  acceptable.

A later phase can make `prose_fts` contentless by persisting chunk spans in
an auxiliary table and re-running extraction at query time. Explicitly out
of scope here.

### Test additions

- `SqliteIndexTests.ContentlessFts_ContentShadowAbsent` — inspect
  `sqlite_master` or `dbstat` to confirm no `code_fts_content` table exists,
  or is ≤ 1 page.
- `SqliteIndexTests.UpsertFile_WritesContentlessFts5` — upsert, re-open, run
  a MATCH query, assert candidates contain the rowid; assert `GetFilesAsync`
  returns the row with no content column.
- `CodeQueryExecutorDiskReadTests.ReturnsLiveSnippetAfterEdit` — index a
  file, mutate the file on disk to swap two strings, search for the new
  string, assert it appears with the *new* line content.
- `CodeQueryExecutorDiskReadTests.MissingFile_IsSkippedAndRepaired` — index
  a file, delete from disk, search, assert no result produced, assert a
  `FileChanged(path)` was enqueued in the `DebouncingEventQueue`.
- `CodeQueryExecutorDiskReadTests.OversizeFile_IsSkipped` — index, grow
  on disk beyond `MaxIndexableFileBytes`, search, assert skipped.
- `CodeQueryExecutorPerfSmoke.LatencyBudgetNotExceeded` — (attribute
  `[Trait("Category","Perf")]`, excluded from default run) run the 20-query
  corpus and fail if p50 exceeds the baseline × 1.2. Runs in CI nightly,
  not per-PR.

### Existing-test adjustments

- `SqliteIndexTests.GetFilesAsync_ReturnsContent` — rename and rewrite to
  assert `(FileId, Path, Sha256)` are populated; drop content assertion.
- `FullScanIndexerTests.FindsContentByTrigram` — update the downstream
  assertion that currently inspects `rows[0].Content` to instead read the
  file from disk (mirroring executor behavior).
- `CodeQueryExecutorTests.*` — already operate on the public search
  contract, not on internal `FileRow.Content`. Should continue to pass
  without change, but each test must be audited.

### Rollback

Bump `SqliteSchema.Version` to 3 and restore the pre-PR DDL and executor
code. Daemons will cold-rebuild on next start.

## Schema migration

`SqliteIndex.OpenOrCreate` already enforces the “delete on version mismatch”
policy. On first start after PR 3 lands, existing `%APPDATA%\Indexed\<repoId>\index.db`
files are deleted and rebuilt from scratch. For the canary repos the rebuild
is < 60 s. For a 1-GB source tree it is a few minutes — acceptable, and
covered in the user-facing release note.

Document this in `Usage-Guide` before PR 3 merges:

```
After upgrading to v0.2.x, the first `indexed` invocation will perform a
one-time index rebuild. No action required; expect a slower-than-usual
first `/status`. Subsequent invocations return to normal latency.
```

## Measurement plan (post-implementation)

Re-run the same procedure from §4 against the three canary repos after each
PR lands. Expected deltas:

| Stage | Expected ratio | Largest win source |
|---|---|---|
| Baseline | 1.8–2.5× | — |
| After PR 1 | 1.5–2.2× | excluded lockfiles + minified bundles |
| After PR 2 | 1.4–2.0× | merged posting-list segments |
| After PR 3 | **0.9–1.1×** | contentless FTS5 |

Record results in an addendum to `docs/Indexed-Size-Reduction-Baseline.md`.
If any canary misses the expected band by more than 50%, stop and
investigate before proceeding.

## Test-suite additions summary

| File (new) | Purpose |
|---|---|
| `tests/Indexed.Core.Tests/ExcludeFilterTests.cs` | Default globs, composition, opt-out. (Some tests may already exist — extend.) |
| `tests/Indexed.Core.Tests/IndexOptimizerTests.cs` | Segment reduction, clean-skip, shutdown merge. |
| `tests/Indexed.Core.Tests/CodeQueryExecutorDiskReadTests.cs` | Live-snippet, missing-file repair, oversize skip. |
| `tests/Indexed.Core.Tests/SqliteIndexContentlessTests.cs` | Confirm `_content` shadow absent, MATCH still works. |
| `tests/Indexed.Service.Tests/DaemonHostExcludeDefaultsTests.cs` | End-to-end opt-in/opt-out wiring. |
| `tests/Indexed.Service.Tests/DaemonHostOptimizerIntegrationTests.cs` | Optimizer starts/stops with daemon; coexists with indexer. |

Each new test file must follow the existing xUnit + `Microsoft.Extensions.Logging.Abstractions` pattern used in sibling tests.

## Risk register

| Risk | Likelihood | Mitigation |
|---|---|---|
| PR 3 rebuild time exceeds acceptable range on large repos | Medium | Warn in release notes; profile on largest canary before merging. |
| Disk read at query time inflates p95 | Medium | Measure pre-PR and post-PR latency; if regression > 20% p50 or > 50% p95, add a bounded in-memory content cache keyed by `(path, sha256)` with LRU eviction. Out of scope by default; held in reserve. |
| Default excludes hide files users want searchable | Low | Opt-out flag `--no-default-excludes` is first-class; documented. |
| Optimizer starves the incremental indexer under write pressure | Low | Per-tick page budget is small; incremental writer holds the lock first. Covered by `CoexistsWithIncrementalIndexer` test. |
| Schema migration eats user work | Very low | `index.db` is a derived artifact; all content reconstructible from repo. Documented property. |

## New-surface sizing

Indicative LOC deltas (production + tests) per PR:

| PR | Implementation LOC | Tests LOC | Notes |
|---|---|---|---|
| 1 — default excludes | ~40–80 | ~40–80 | one new exclusion-list config path + fixtures |
| 2 — optimizer | ~100–200 | ~100–200 | new optimizer pass + its fixtures |
| 3 — contentless + rehydrate | ~200–400 | ~150–300 | contentless mode + rehydrate pipeline + fixtures |
| Baseline + post-measurement | ~50 | ~50 | ad-hoc benchmarks |
| **Total** | **~400–730 LOC implementation + ~340–630 LOC tests** | | Small-to-moderate new surface across three PRs. |

## Open questions (resolve before starting)

1. **Content cache?** If the latency regression from disk-read snippet
   rehydration exceeds budget, introduce a `ConcurrentDictionary<long, string>`
   LRU cache in `FileContentProvider`. Gate the decision on post-PR-3
   measurement.
2. **Prose contentless?** Deferred. Explicitly not in this plan. File an
   issue to reconsider in Phase 2.
3. **Custom page size?** `PRAGMA page_size = 8192` gives a small additional
   compression win (§3.1.2). Decision: include in PR 2 since it must be set
   before any writes to take effect, which aligns with the rebuild caused by
   PR 3's schema bump. One-line change in `ApplyConnectionPragmas` — however
   it only applies on fresh DBs, so the timing relative to PR 3's version
   bump is load-bearing. Document in PR 2 that the `page_size` line is
   effective starting with the next cold rebuild.
4. **Exclude-default telemetry?** Optional: emit an INFO log line on startup
   listing which default globs matched at least one file during the full
   scan. Useful for tuning the list in future. Small addition; include.
