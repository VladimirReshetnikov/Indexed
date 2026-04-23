# Indexed — Workspace Targets Proposal

- Created (UTC): 2026-04-23T17:26:20Z
- Repository HEAD: d2e314726d4c317ed90f10f83ce200d8e6234112
- Status: Draft proposal for adding non-git directory and directory-set indexing alongside the current git-repository mode.
- Audience: Maintainers, reviewers, implementers, and agent consumers of `idx` / the localhost JSON API.
- Scope: Current-state review plus proposed architecture, contracts, storage model, and rollout plan for indexing any directory tree or explicit group of directory trees with continuous background updates.
- Related code:
  - `src/Indexed/src/Indexed.Service/DaemonHost.cs`
  - `src/Indexed/src/Indexed.Git/GitRepository.cs`
  - `src/Indexed/src/Indexed.Core/FullScanIndexer.cs`
  - `src/Indexed/src/Indexed.Core/IncrementalIndexer.cs`
  - `src/Indexed/src/Indexed.Core/RepoWatcher.cs`
  - `src/Indexed/src/Indexed.Core/HeadPoller.cs`
  - `src/Indexed/src/Indexed.Core/SqliteIndex.cs`
  - `src/Indexed/src/Indexed.Cli/DaemonClient.cs`
- Related docs:
  - [Architecture](./Indexed-Architecture.md)
  - [Usage guide](./Indexed-Usage-Guide.md)
  - [Tutorial](./Indexed-Tutorial.md)
  - [Architecture proposal](./Indexed-Architecture-Proposal.md)
  - [Stage 4 incremental indexer plan](./Indexed-Stage4-Incremental-Indexer-Plan.md)
  - [Post-fix code and architecture review](./Indexed-Post-Fix-Architecture-Review__c51514c9d7f3.md)

## Summary

Indexed already has the expensive parts of a general-purpose local indexing service: a durable SQLite/FTS5 index, low-latency code query planning and execution, debounced background writes, live-disk snippet rehydration, and a daemon/CLI control plane. The current hard limitation is not the query engine. It is that the daemon's identity, startup preconditions, file-set definition, and freshness model are all explicitly tied to a single git repository.

This proposal adds a new target model that lets Indexed serve:

- one git repository, exactly as it does today;
- one arbitrary directory tree, even when git is absent;
- one explicit set of directory trees, even when they do not share a common ancestor.

The new mode is complementary, not replacement-oriented. Git mode remains the preferred and richer experience when a repository is available because it has authoritative file-set semantics, `diff-tree`-based HEAD patching, and explicit HEAD freshness. Directory mode broadens where Indexed can be used; it does not reduce the quality or importance of the existing git-driven mode.

## 1. Current-state review

### 1.1 What the current docs promise

The active `src/Indexed` docs are consistent about the intended product shape today:

- `README.md`, `Indexed-Architecture.md`, `Indexed-Tutorial.md`, and `Indexed-Usage-Guide.md` all describe Indexed as a service for a single local git repository.
- The authoritative file set is documented as `git ls-files` plus `git ls-files --others --exclude-standard`.
- Freshness is documented in terms of `indexedHead` versus `currentHead`.
- Daemon discovery is documented around `repoId` and `%LOCALAPPDATA%\Indexed\<repoId>\`.

That current-state documentation is honest. The new feature should therefore be framed as an additive architecture extension, not as a retroactive reinterpretation of what Indexed has always been.

### 1.2 Where the implementation is git-coupled today

The main git-coupled seams are narrow but structural.

| Area | Current implementation | Why it matters for directory mode |
|------|------------------------|-----------------------------------|
| Startup precondition | `DaemonHost.StartAsync` calls `GitRepository.Open(_options.RepoRoot)` immediately. | A non-git directory cannot even launch today. |
| Target identity | `RepoId.Compute(repoRoot, firstCommitSha)` drives mutex name, app-data path, and daemon discovery. | A directory set has no `firstCommitSha`; identity must come from a generalized target spec. |
| File enumeration | `FullScanIndexer` and `IncrementalIndexer` both depend on `GitRepository.EnumerateFiles()` and `.gitattributes` binary checks. | Non-git targets need direct filesystem enumeration and must not depend on `git.exe`. |
| Incremental HEAD tracking | `HeadPoller` and `IncrementalIndexer.ExpandHeadMoved` depend on `GetHeadSha()` and `git diff-tree`. | Directory targets need watcher + reconciliation only; there is no HEAD token. |
| Freshness contract | `BuildFreshness()` in `DaemonHost` compares `indexed_head` against current git HEAD. | Directory mode needs a target-agnostic freshness model. |
| CLI daemon discovery | `DaemonClient.CreateAsync()` computes `repoId` before it knows whether a daemon already exists. | Multi-root targets need explicit target selection and target-derived identity. |

### 1.3 What is already reusable

A large part of the system is already target-agnostic and should remain so:

- `SqliteIndex` and the FTS5 schema are agnostic to git as long as they receive a stable file namespace.
- `CodeQueryPlanner` and `CodeQueryExecutor` do not care whether files came from git or raw filesystem enumeration.
- `TextDecoder`, `LanguageGuess`, `ExcludeFilter`, `PathGlob`, and `MatchExtraction` are already generic.
- `DebouncingEventQueue` and `IndexEvent` are generic enough to carry directory-mode change events.
- `RepoWatcher` is built on `FileSystemWatcher` over a plain filesystem root and is already most of the way to a generic watcher.
- `FileContentProvider` already resolves repository-relative paths against a root and defends against root escape; the same pattern works for generalized logical paths.

The proposal therefore does not require a new index engine, a new daemon model, or a new query planner. The work is primarily about generalizing target identity and file-set ownership.

### 1.4 One additional current-state issue worth fixing during this work

Current daemon identity ignores index-shaping launch options such as `--exclude-index` and `--no-default-excludes`. `DaemonClient.CreateAsync()` computes the same `repoId` regardless of those options, so one app-data directory can be reused across materially different indexing configurations.

That ambiguity is survivable in the current repo-only world because most users will operate with one stable profile. It becomes unacceptable once the root set itself is variable. Directory-mode support should therefore fix the broader problem and define target identity from the complete canonical target specification, not only from a repository path.

## 2. Goals and non-goals

### 2.1 Goals

1. Preserve the current git-repository experience, including its file-set semantics and git-driven freshness signals.
2. Add a first-class directory-tree mode that works without `.git/` or `git.exe`.
3. Add a first-class directory-set mode for an explicit list of roots.
4. Keep continuous background indexing in every mode: watcher-driven updates plus periodic reconciliation.
5. Keep one daemon serving one target, so query semantics and storage ownership remain simple.
6. Reuse the existing SQLite/FTS5 query engine and the current CLI/HTTP mental model wherever possible.
7. Make target identity deterministic and configuration-sensitive so daemon discovery and stored indexes are unambiguous.
8. Keep the proposal implementation-sized rather than exploratory: the new feature should be a production-ready extension of current architecture, not a sidecar prototype.

### 2.2 Non-goals

- Replacing git mode with directory mode when git is available.
- Cross-target search in one request. A directory set is one target; querying several unrelated existing daemons at once is out of scope.
- Auto-discovering arbitrary "interesting" directories from the whole machine.
- Semantic code navigation, xref, or vector search.
- File synchronization, remote indexing, or multi-user daemon sharing.
- Fully general ignore-file semantics in the first cut. Git mode keeps git semantics; directory mode starts with explicit roots plus explicit exclude globs.

## 3. Proposed terminology

The new design should stop using repository vocabulary for concepts that are actually more general.

- **target**: the unit served by one daemon and one `index.db`.
- **target kind**: one of `git-repo`, `directory-tree`, or `directory-set`.
- **root**: one absolute filesystem directory that contributes files to a target.
- **logical path**: the stable path string stored in `files` and returned in matches. In git mode this remains repo-relative. In multi-root mode it is rooted under a stable root label.
- **target spec**: the canonical, serialized description of a target: kind, roots, exclude policy, and any other index-shaping options that affect stored content.
- **revision token**: an optional freshness token carried by targets that have one. Git targets use HEAD SHA. Directory targets do not have a cheap authoritative global token and report null.

These terms matter because the current code mixes three different concerns under "repo": path root, git semantics, and daemon identity. The feature becomes simpler once those are separated.

## 4. Proposed architecture

### 4.1 Introduce a target abstraction

Add one new project, `Indexed.Targets`, that owns target-neutral contracts and the non-git directory implementations. Keep `Indexed.Git` as the git-specific implementation layer.

Recommended responsibility split:

- `Indexed.Targets`
  - `TargetSpec`, `TargetKind`, `TargetId`
  - root-label and logical-path rules
  - directory-tree and directory-set enumeration
  - directory-mode watcher/reconciliation helpers
- `Indexed.Git`
  - `GitRepository`, `GitProcess`
  - git-target adapter implementing the target contracts
- `Indexed.Core`
  - index storage, query planning/execution, batch writer, event queue
- `Indexed.Service`
  - target resolution, daemon lifecycle, HTTP surface
- `Indexed.Cli`
  - target selection and daemon discovery

This avoids pulling git-specific code into `Indexed.Core` while also avoiding a service/CLI dependency on the whole query engine merely to understand target identity.

### 4.2 Core contract

The exact type names are not important; the responsibilities are.

```csharp
public enum TargetKind
{
    GitRepository,
    DirectoryTree,
    DirectorySet,
}

public sealed record TargetSpec(
    TargetKind Kind,
    IReadOnlyList<string> Roots,
    IReadOnlyList<string>? IndexExcludeGlobs,
    bool UseDefaultIndexExcludes);

public interface IIndexTarget
{
    TargetSpec Spec { get; }
    string TargetId { get; }
    IReadOnlyList<TargetRoot> Roots { get; }

    IReadOnlyList<EnumeratedFile> EnumerateFiles(CancellationToken cancellationToken = default);
    bool TryMapAbsolutePath(string absolutePath, out LogicalPath logicalPath);
    string ResolveAbsolutePath(LogicalPath logicalPath);

    string? GetCurrentRevisionToken(CancellationToken cancellationToken = default);
    ITargetChangeTracker CreateChangeTracker(DebouncingEventQueue queue, ILogger logger);
}
```

Git targets implement the full interface, including a real revision token and a `diff-tree`-based tracker. Directory targets implement the same namespace and enumeration contract but return no revision token and omit the HEAD-specific tracker.

### 4.3 Daemon composition after the change

```text
CLI/HTTP target selection
        |
        v
    TargetSpec / TargetId
        |
        v
    DaemonHost
        |
        +--> IIndexTarget ------------------+
        |                                   |
        |                                   +--> full enumeration
        |                                   +--> absolute/logical path mapping
        |                                   +--> optional revision token
        |                                   +--> change tracker
        |
        +--> SqliteIndex
        +--> FullScanIndexer
        +--> IncrementalIndexer
        +--> CodeQueryPlanner / Executor
```

The important architectural shift is that `DaemonHost`, `FullScanIndexer`, and `IncrementalIndexer` should depend on `IIndexTarget`, not directly on `GitRepository`.

### 4.4 Why this is the right seam

This seam aligns with the actual variability:

- the query engine is constant;
- the storage engine is nearly constant;
- the thing that varies is how files are discovered, named, and observed for change.

That is exactly what `IIndexTarget` owns.

## 5. Target identity and daemon discovery

### 5.1 Identity must come from the canonical target spec

New target identity should be:

```text
targetId = SHA1(canonical-target-spec-json)[0:12]
```

The canonical target spec must include:

- target kind;
- normalized absolute roots, sorted;
- index-shaping options such as exclude globs and the default-excludes flag;
- stable root labels when multiple roots are present.

This ensures that materially different targets never reuse one another's `index.db`.

### 5.2 Compatibility rule for current git mode

To avoid needlessly orphaning current app-data directories, preserve the existing `repoId` formula for the legacy default git case:

- one git root;
- current repo-based identity;
- no future target-only features that materially alter logical paths.

All new directory targets, and any git target whose spec cannot be represented by the legacy rule, should use the spec-based `targetId`.

The result is pragmatic:

- existing `idx` usage inside normal git repos keeps its warm cache;
- new target kinds still get correct identity;
- we do not carry repo-centric identity deeper into the architecture than necessary.

### 5.3 Daemon discovery surface

Generalize `DaemonPaths.ForRepo(...)` to `DaemonPaths.ForTarget(...)`. Keep the same app-data layout:

```text
%LOCALAPPDATA%\Indexed\<targetId>\
    daemon.json
    index.db
    logs\
```

`daemon.json` should grow to include:

- `targetId`
- `targetKind`
- `roots`
- `primaryRoot`
- `repoId` and `repoRoot` only when the target kind is `git-repo`

This keeps old git-oriented clients understandable while giving directory mode a truthful control plane.

## 6. File namespace and schema evolution

### 6.1 Why the current `files.path` contract is no longer sufficient

Today `files.path` is defined as one repo-relative POSIX path and is globally unique within one repository. That breaks down for a directory set:

- two roots may both contain `src/Program.cs`;
- a watcher callback needs to know which absolute root produced the event;
- the query response still needs one stable display path.

Encoding the root name directly into the existing `path` column would work for the happy path but would blur display concerns, uniqueness, and root identity into one string. A schema revision is cleaner.

### 6.2 Proposed schema v3

Add a roots table and split file identity into root-local and target-global forms.

```sql
CREATE TABLE roots (
    root_id        INTEGER PRIMARY KEY,
    root_name      TEXT NOT NULL,
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
CREATE INDEX files_logical_path ON files(logical_path);
```

`code_fts`, `prose_fts`, and rowid = `file_id` stay unchanged.

### 6.3 Logical-path rules

- Git target: `logical_path == relative_path` exactly as today.
- Single directory-tree target: `logical_path == relative_path` exactly as today.
- Directory-set target: `logical_path == root_name + "/" + relative_path`.

This keeps the common single-root cases stable while giving multi-root targets collision-free, human-readable paths.

### 6.4 Root labels

For multi-root targets, derive `root_name` as follows:

1. start from the directory basename;
2. if basenames are unique, use them directly;
3. if they collide, append a short deterministic suffix derived from the canonical absolute path, such as `src~a1b2`.

The label must be stable because it becomes part of the logical path and therefore part of the user-visible query namespace.

### 6.5 Rebuild policy

This is a schema bump. Rebuild is acceptable and consistent with current Indexed policy. There is no value in an in-place migration from repo-relative `files.path` to the root-aware v3 model.

## 7. Enumeration and change detection by target kind

### 7.1 Git-repo target

Git mode remains behaviorally the same:

- enumeration: `git ls-files` plus untracked-not-ignored;
- binary override: `.gitattributes` `binary`;
- change feed: `FileSystemWatcher` + `HeadPoller` + `git diff-tree` + reconciliation;
- revision token: HEAD SHA.

This mode stays the preferred experience when the searched corpus is a real git working tree.

### 7.2 Directory-tree target

Directory mode should use:

- recursive filesystem enumeration rooted at one selected directory;
- size/NUL-byte/default-exclude filters, but no dependence on `git.exe`;
- one `FileSystemWatcher` over that root;
- periodic reconciliation by direct filesystem walk;
- no revision token.

Symlink and reparse-point rule for the first cut:

- do not recurse through directory symlinks/junctions;
- only index files whose canonical full path remains under the selected root;
- surface skipped-reparse-point behavior in logs and status notes if it materially affects coverage.

### 7.3 Directory-set target

Directory-set mode is directory mode repeated over several disjoint roots:

- one watcher per root, not one watcher on a guessed common ancestor;
- one reconciliation pass that unions the per-root file sets;
- overlapping roots are rejected up front because they create duplicate namespace and watcher ambiguity;
- logical paths are root-prefixed as described above.

Requiring explicit disjoint roots is a feature, not a limitation. It keeps target identity, logical paths, and watcher ownership deterministic.

## 8. Freshness model

### 8.1 Why the DTO must become target-agnostic

The current `Freshness` DTO is explicitly git-shaped:

- `IndexedHead`
- `CurrentHead`
- `PendingFileCount`
- `LastFullScanAt`
- `IsStale`

That shape is truthful for git mode and misleading for directory mode. Overloading HEAD fields with fake values would be contract drift.

### 8.2 Proposed evolution

Evolve `Freshness` additively:

- keep `PendingFileCount`, `LastFullScanAt`, and `IsStale`;
- add `IndexedRevisionToken`, `CurrentRevisionToken`, and `RevisionKind`;
- add `LastReconciliationAt`;
- keep `IndexedHead` / `CurrentHead` as git-only compatibility aliases for one contract generation.

Recommended semantics:

- git target:
  - `RevisionKind = "git-head"`
  - `IndexedRevisionToken = indexed HEAD`
  - `CurrentRevisionToken = current HEAD`
  - `IsStale = pending > 0 || inFlight || indexed != current`
- directory target:
  - `RevisionKind = "none"`
  - both revision tokens null
  - `IsStale = pending > 0 || inFlight || reconciliationInProgress || initialScanInProgress`

The important invariant is that freshness remains truthful without pretending that directory mode has an authoritative global revision token analogous to git HEAD.

## 9. CLI and HTTP surface

### 9.1 Preserve the current default

Current git usage should continue to work unchanged:

```bash
idx find "SearchRequest"
idx status
idx rescan
idx stop
```

When no generic root-selection flags are present, `idx` should keep today's behavior: resolve the enclosing git repo and use git mode.

### 9.2 Add generic target selection

Recommended CLI additions:

```text
idx find <pattern> [existing options] [--root <dir>]...
idx status [--root <dir>]...
idx rescan [--root <dir>]...
idx stop [--root <dir>]...
```

Selection rules:

1. If one or more `--root` flags are present, use directory-based target resolution.
2. One root means `directory-tree`; several roots mean `directory-set`.
3. If no `--root` flags are present, preserve today's git-repo behavior.
4. `--repo-root` remains as a git-only compatibility alias.

This avoids a new management CLI just to get the first cut shipped. Repeating the same root set yields the same `targetId`, so daemon reuse still works.

### 9.3 Status and daemon metadata

`StatusResponse` should grow additively to include:

- `TargetKind`
- `TargetId`
- `Roots`
- `PrimaryRoot`

Keep `RepoRoot` / `RepoId` only when `TargetKind == git-repo`.

### 9.4 Query path behavior

`--glob` and `--exclude` continue to operate on the logical path namespace.

Examples:

- single-root directory target: `src/**/*.cs`
- multi-root target: `sdk/**/*.cs`, `docs/**/*.md`

This preserves the current mental model: query-time path filtering applies to what the user sees in results.

## 10. Implementation plan

### Workstream A — generalize target ownership

Touch roughly 8-12 existing files plus one new project:

- add `Indexed.Targets`;
- move daemon identity and target selection off `GitRepository.Open(...)`;
- make `DaemonPaths`, `DaemonInfo`, and the CLI client target-aware;
- keep legacy git behavior as a compatibility path.

### Workstream B — schema and logical-path migration

Touch roughly 6-10 files:

- schema v3 with `roots` table and root-aware `files`;
- root-aware absolute/logical path mapping;
- target metadata persisted in `meta`.

### Workstream C — directory-tree mode

Touch roughly 10-15 files:

- implement direct filesystem enumeration;
- implement directory target watcher/reconciliation;
- wire target-agnostic `FullScanIndexer` / `IncrementalIndexer`;
- add directory-mode integration tests.

### Workstream D — directory-set mode

Touch roughly 6-10 files:

- multi-root label generation;
- one watcher per root;
- overlap validation and multi-root reconciliation;
- path-collision and duplicate-filename tests.

### Workstream E — contract and doc cleanup

Touch roughly 6-12 files:

- freshness DTO evolution;
- status/daemon metadata additions;
- CLI help and usage guide updates;
- new tutorial/examples for directory targets;
- retire any repo-only language in current-state docs that becomes too narrow.

The intended sequencing is A -> B -> C -> D -> E. Git mode should remain green after A, B, and C; multi-root adds the only user-visible namespace change.

## 11. Test plan

The new feature needs contract-level and architecture-level coverage, not only unit tests.

### 11.1 Target abstraction tests

- git target still enumerates tracked plus untracked-not-ignored files;
- directory target enumerates direct filesystem contents without git;
- multi-root target rejects overlapping roots and preserves stable root labels;
- absolute-path mapping never escapes a selected root.

### 11.2 Incremental behavior tests

- single-file edit in directory mode becomes queryable without a restart;
- root-local rename produces delete + upsert with stable logical path behavior;
- watcher overflow or temporary watcher failure is repaired by reconciliation;
- root temporarily unavailable during scan is surfaced as degraded rather than corrupting the index.

### 11.3 Compatibility tests

- existing git-mode CLI flows still work with no extra flags;
- existing git-mode freshness still reports HEAD tokens;
- current path globs in single-root targets behave the same as today;
- one git repo with default settings still maps to the legacy app-data directory.

### 11.4 Query namespace tests

- identical relative paths under different roots do not collide;
- `--glob` and `--exclude` operate on logical paths exactly as documented;
- search results show stable logical paths across daemon restarts for the same target spec.

## 12. Risks and trade-offs

### 12.1 Directory mode is less authoritative than git mode

Git mode knows exactly which files are in scope and can cheaply observe branch movement. Directory mode has to trust filesystem walks plus watcher hints. That is acceptable as long as the product tells the truth:

- git mode is richer;
- directory mode is broader;
- the two are not equivalent.

### 12.2 Multi-root path design is user-visible

Once a logical path format ships, agents and scripts will depend on it. That is why this proposal prefers an explicit root-aware schema instead of a hidden string-prefix trick.

### 12.3 Watcher pressure increases with unrelated roots

Several roots means several watchers, more reconciliation work, and more chances for transient root-specific errors. The current single-writer model is still valid, but status output should surface root-level degradations rather than bury them in logs.

### 12.4 Ignore semantics must stay explicit

Directory mode should not silently inherit partial git semantics. If later we want `.indexedignore`, `.ignore`, or root-local policy files, that should be a deliberate follow-up with an explicit contract.

## 13. Open questions

1. Should directory mode eventually support a persisted named-target registry, or is repeated `--root` selection enough for the first production cut?
2. Should single-root non-git mode reuse bare relative paths forever, or should every non-git target eventually expose an explicit root label for total uniformity?
3. Is a future `.indexedignore` file worth introducing, or should explicit CLI/API excludes remain the only durable directory-mode policy?
4. Should target identity always be fully spec-derived, or is the legacy git-id compatibility rule worth keeping permanently after the directory feature lands?
5. Do we want a later Windows-only optimization path that uses the NTFS USN journal for faster large-directory reconciliation, or is `FileSystemWatcher` plus periodic enumeration sufficient for the expected workloads?

## 14. Recommendation

Proceed by generalizing Indexed around a target abstraction, not by bolting directory enumeration directly into the current git classes. The current codebase is already well-positioned for this:

- the query and storage engines are reusable;
- the change pipeline is almost reusable;
- the main work is to separate target identity and file-set ownership from git-specific semantics.

If done this way, Indexed becomes a broader local indexing service without giving up what currently makes it good at repository search. Git mode remains the high-fidelity path for repositories. Directory modes expand the product into the much larger space of local code and documentation trees that are not under git control but still benefit from always-warm search.
