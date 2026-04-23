# Indexed Workspace Targets — Phase 02 Implementation Plan

- Created (UTC): 2026-04-23T19:04:05Z
- Repository HEAD: 10b104a4d33770ef498b57c64923116f4edad489
- Status: Planned
- Phase scope: Schema v3, target-aware index/storage plumbing, target-based content rehydration, and refactoring the full/incremental indexers off raw `GitRepository` path assumptions.
- Depends on:
  - [Phase 01 plan](./Indexed-Workspace-Targets-Phase-01-Implementation-Plan.md)
  - [Workspace targets proposal](../Indexed-Workspace-Targets-Proposal.md)
  - Current code changes introducing `Indexed.Targets`, target ids, and target-aware daemon metadata

## 1. Objectives

Phase 02 makes the index itself understand targets rather than repositories. After this phase, the daemon may still be serving only git-backed targets in production, but the storage and indexing engine must no longer assume that `files.path` is always repo-relative.

Normative goals:

1. Introduce schema v3 with explicit roots and logical paths.
2. Change `SqliteIndex`, `FullScanIndexer`, `IncrementalIndexer`, and `FileContentProvider` to operate on logical paths plus target roots rather than a single repo root.
3. Extract the shared binary heuristic into target-neutral code.
4. Keep git-specific HEAD diffing and `.gitattributes` binary overrides available through optional target capabilities instead of hard references from core to `GitRepository`.
5. Preserve the current git behavior and rebuild policy while creating the runtime shape needed for directory targets.

Non-goals:

- directory-tree enumeration itself;
- multi-root CLI UX;
- default directory-mode exclude policy;
- watcher-before-scan startup ordering for non-git targets.

## 2. Storage design for this phase

Schema v3 target shape:

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
```

Design intent:

- `logical_path` is the search/query namespace;
- `root_id + relative_path` is the per-root physical identity;
- `absolute_path` does not live in `files`; it is resolved from roots plus logical/relative path at runtime through the target;
- the previous explicit secondary index on path/logical path is not recreated because `UNIQUE` already provides it.

## 3. Planned code changes

### 3.1 `SqliteSchema` and `SqliteIndex`

- bump schema version from 2 to 3;
- add root metadata table and root-writing helpers;
- rename path-oriented helpers to logical-path-oriented names where that materially reduces ambiguity;
- preserve reader/writer concurrency behavior unchanged.

Required API work:

- ability to store/update the target roots at daemon startup;
- `GetAllPathsWithShaAsync()` and `GetAllPathsWithStatAsync()` continue to return logical-path keyed dictionaries;
- row fetches used by `CodeQueryExecutor` continue to surface the logical path in the `Path` slot that search responses already use.

### 3.2 Full scan

Refactor `FullScanIndexer` to consume `IIndexTarget`:

- enumeration comes from `EnumerateFilesAsync`;
- root metadata is written before the first file batch;
- upserts use `EnumeratedFile.Root`, `RelativePath`, and `LogicalPath`;
- git-specific head metadata is written only when the target exposes a revision token.

Progress policy:

- git targets may still report an exact total;
- other targets are allowed to report a null/unknown total until a later optimization adds estimation.

### 3.3 Incremental indexing

Refactor `IncrementalIndexer` to consume:

- `IIndexTarget` for path mapping and absolute-path resolution;
- optional `IRevisionDiffTarget` for `HeadMoved` expansion;
- optional `IExplicitBinaryPathProvider` for `.gitattributes`-style overrides;
- target-neutral binary heuristic from core.

Required semantic checks:

- file-change/delete events are keyed by logical path;
- reconciliation diffs logical paths, not repo-relative paths;
- git-specific HEAD advancement remains supported but optional.

### 3.4 Live-content reads

Replace the repo-root-only `FileContentProvider` contract with a target-aware provider:

- input remains the logical path stored in the index;
- resolution uses the target, not string concatenation against one repo root;
- out-of-root defense remains mandatory and target-root-aware.

### 3.5 Search execution

Audit the query path so every place that assumes `files.path` means "repo-relative path" instead treats it as "logical path returned to the caller".

Expected touch points:

- `CodeQueryExecutor`
- `SqliteSearchBackend`
- any repair events enqueued by live-content read failures

## 4. Tests for this phase

### 4.1 Schema and index tests

- schema version bump triggers rebuild as designed;
- roots table is populated correctly for a git target;
- logical-path uniqueness and `(root_id, relative_path)` uniqueness both behave as intended.

### 4.2 Full-scan tests

- git-target full scan still indexes the same files and updates indexed-head metadata;
- logical paths in stored rows remain repo-relative for git mode;
- root metadata survives daemon restart.

### 4.3 Incremental tests

- file change/delete events addressed by logical path still converge correctly in git mode;
- reconciliation uses logical paths and remains correct after schema v3;
- `HeadMoved` still applies git diffs and advances the indexed revision token.

### 4.4 Content-provider tests

- logical path resolution remains confined under the owning root;
- malformed logical paths do not escape the selected root;
- missing/unreadable/oversize repair behavior remains intact after the provider refactor.

## 5. Risks and mitigations

### Risk: schema churn plus runtime refactor hides regressions

Mitigation:

- keep git-mode integration tests green while schema changes land;
- reuse the existing rebuild-on-version-mismatch policy rather than introducing in-place migration complexity.

### Risk: path ambiguity in mixed old/new code

Mitigation:

- rename APIs to `LogicalPath` terminology where ambiguity would otherwise remain;
- avoid partially-updated helpers that still silently expect repo-relative paths.

### Risk: contentless FTS path breaks during path rename

Mitigation:

- keep `CodeQueryExecutor` reading the logical path from `files`;
- update `FileContentProvider` in the same phase as schema v3 so search and storage stay aligned.

## 6. Exit criteria

Phase 02 exits when:

1. schema v3 is live and builds cleanly;
2. full scan and incremental indexing run against `IIndexTarget` rather than raw `GitRepository`;
3. current git integration tests still pass against schema v3;
4. directory targets can be added next without another storage-model rewrite.

## 7. Expected size

Planned implementation magnitude for phase 02:

- roughly 600-1100 LOC across 10-18 files;
- one schema bump plus one round of widespread path-API renaming;
- 15-30 updated or new tests across core, service, and abstractions layers.
