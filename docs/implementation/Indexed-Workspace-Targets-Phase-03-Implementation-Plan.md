# Indexed Workspace Targets — Phase 03 Implementation Plan

- Created (UTC): 2026-04-23T19:33:11.8490864Z
- Repository HEAD: 10b104a4d33770ef498b57c64923116f4edad489
- Status: Planned
- Phase scope: directory-backed targets, shared watcher/revision-tracker composition, watcher-before-scan startup ordering, and daemon-host support for non-git targets without yet expanding the full CLI surface.
- Depends on:
  - [Phase 01 plan](./Indexed-Workspace-Targets-Phase-01-Implementation-Plan.md)
  - [Phase 02 plan](./Indexed-Workspace-Targets-Phase-02-Implementation-Plan.md)
  - [Workspace targets proposal](../Indexed-Workspace-Targets-Proposal.md)

## 1. Objectives

Phase 03 turns the new target-aware storage core into an actual directory indexing service.

Normative goals:

1. Implement `directory-tree` and `directory-set` targets that do not depend on git.
2. Replace the repo-only watcher with a target-aware `DirectoryWatcher` that can monitor one or many roots.
3. Preserve git revision tracking, but compose it as an optional capability rather than the central change-tracking model.
4. Start watcher(s) before the initial scan so edits during the cold scan are not lost.
5. Make `DaemonHost` capable of serving directory targets end-to-end when given a target selection directly.

Non-goals:

- the final end-user CLI flags and help text for `--root` / `idx daemons`;
- exhaustive documentation refresh across README/user docs;
- directory-target UX polish beyond what is needed to validate the runtime.

## 2. Runtime model for this phase

By the end of phase 03 the daemon’s change feed should be composed from three independent parts:

- `DirectoryWatcher(target roots)` for filesystem notifications;
- optional `IRevisionTracker` for git-backed revision drift (`HeadPoller` in the first cut);
- `ReconciliationScheduler` as the correctness backstop.

That composition is important because non-git targets need only the first and third pieces, while git targets keep all three.

## 3. Planned code changes

### 3.1 Directory target implementations

Add concrete target types under `Indexed.Targets`:

- `DirectoryTreeIndexTarget`
- `DirectorySetIndexTarget`

Required behavior:

- streaming recursive enumeration from the filesystem, not via git;
- deterministic logical-path rules:
  - single-root directory-tree: logical path is the root-relative POSIX path;
  - multi-root directory-set: logical path is `<label>/<relative-path>`;
- absolute-path mapping and logical-path reverse resolution;
- root overlap/nesting validation stays centralized in target-spec normalization.

### 3.2 Default directory-mode excludes

Introduce a separate `DefaultDirectoryModeExcludes` set in `ExcludeFilter` and keep it independent from the existing index-shaping defaults.

Intent:

- git mode retains today’s behavior by default;
- directory mode gets safety/perf defaults for obviously low-value or noisy trees such as VCS metadata, dependency caches, and common build outputs;
- the directory-default switch participates in target identity, so changing it naturally selects a different daemon/index.

### 3.3 Shared watcher layer

Replace `RepoWatcher` with a target-aware `DirectoryWatcher`.

Required behavior:

- one `FileSystemWatcher` per root;
- absolute-path-to-logical-path mapping via `IIndexTarget.TryMapAbsolutePath`;
- emit `FileChanged` / `FileDeleted` in logical-path space;
- skip target-excluded paths before enqueue;
- tolerate duplicate notifications and best-effort semantics exactly as today;
- keep the reconciliation enqueue-on-error behavior.

Compatibility note:

- a small `RepoWatcher` adapter may remain temporarily if tests or comments still name it, but the runtime should use the shared watcher.

### 3.4 Revision tracker composition

Adapt the git-only `HeadPoller` to implement `IRevisionTracker` so `DaemonHost` can depend on the abstraction for freshness/lifecycle while still using git-specific event generation underneath.

Expected changes:

- `HeadPoller.LastKnownHead` becomes an implementation detail or compatibility alias;
- `DaemonHost` stores an `IRevisionTracker?` rather than assuming git HEAD polling always exists;
- directory targets wire no revision tracker at all.

### 3.5 Daemon target selection surface

Make `DaemonHost` open targets from a service-side target-selection object instead of hard-coding `GitIndexTarget.Open(...)`.

This phase should support:

- existing git-target creation through the current `RepoRoot` path;
- direct construction of directory-tree / directory-set targets in tests and service wiring.

Reason for doing this in phase 03 instead of phase 04:

- the watcher and startup ordering need a real target instance before the CLI work exists;
- otherwise the directory runtime cannot be validated independently of CLI parsing.

### 3.6 Startup ordering

For non-test daemon starts:

1. resolve target;
2. create queue;
3. start `DirectoryWatcher`;
4. start optional `IRevisionTracker`;
5. start `ReconciliationScheduler`;
6. run initial full scan if needed;
7. start serving requests.

The important invariant is that watcher(s) are armed before the full scan begins.

## 4. Tests for this phase

### 4.1 Target abstraction tests

- directory-tree target enumerates regular files without git;
- directory-set target prefixes logical paths with labels;
- reverse logical-path resolution round-trips for both target kinds;
- overlapping/nested roots remain rejected.

### 4.2 Watcher tests

- shared watcher normalizes events correctly for a single root;
- shared watcher handles multi-root events and preserves labels;
- VCS metadata / directory-default excludes are skipped in directory mode;
- watcher error still enqueues `ReconciliationRequested`.

### 4.3 Daemon integration tests

- daemon can start against a non-git directory-tree target and answer search/status;
- edits made during or after the initial scan become queryable without restart;
- directory targets report `RevisionKind.None` and do not stay permanently stale once quiescent.

## 5. Risks and mitigations

### Risk: directory-mode defaults accidentally hide real user files

Mitigation:

- keep the default list conservative and document every pattern in code comments;
- make the directory-default switch explicit in target identity so opting out is deterministic and cache-safe.

### Risk: watcher-before-scan ordering introduces duplicate work

Mitigation:

- rely on existing queue debouncing plus SHA-based unchanged detection in the incremental indexer;
- prefer harmless duplicates over missed changes.

### Risk: service-host target selection becomes half-generic

Mitigation:

- move the actual target-opening logic behind one selection path in phase 03;
- leave the CLI syntax work to phase 04, but avoid another host-level branching rewrite later.

## 6. Exit criteria

Phase 03 exits when:

1. `DirectoryTreeIndexTarget` and `DirectorySetIndexTarget` exist and are covered by tests;
2. the daemon can run against a non-git directory target in integration tests;
3. watcher-before-scan ordering is implemented;
4. git mode still passes unchanged through the new shared watcher/revision-tracker composition.

## 7. Expected size

Planned implementation magnitude for phase 03:

- roughly 700-1300 LOC across 12-22 files;
- two new target classes, one shared watcher, and one service-side target-selection refactor;
- 20-40 updated or new tests across targets, core, and service layers.
