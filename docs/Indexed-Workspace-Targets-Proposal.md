# Indexed — Workspace Targets Proposal

- Created (UTC): 2026-04-23T17:26:20Z
- Repository HEAD: d2e314726d4c317ed90f10f83ce200d8e6234112
- Status: Draft proposal for adding non-git directory and directory-set indexing alongside the current git-repository mode.
- Audience: Maintainers, reviewers, implementers, and agent consumers of `idx` / the localhost JSON API.
- Scope: Current-state review plus proposed architecture, contracts, storage model, and rollout plan for indexing any directory tree or explicit group of directory trees with continuous background updates.
- Related code:
  - `src/Indexed.Service/DaemonHost.cs`
  - `src/Indexed.Git/GitRepository.cs`
  - `src/Indexed.Core/FullScanIndexer.cs`
  - `src/Indexed.Core/IncrementalIndexer.cs`
  - `src/Indexed.Core/RepoWatcher.cs`
  - `src/Indexed.Core/HeadPoller.cs`
  - `src/Indexed.Core/SqliteIndex.cs`
  - `src/Indexed.Cli/DaemonClient.cs`
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

The active docs in this repository are consistent about the intended product shape today:

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
- **target root**: one validated root together with its stable label and "primary root" flag.
- **logical path**: the stable path string stored in `files` and returned in matches. In git mode this remains repo-relative. In multi-root mode it is rooted under a stable root label.
- **canonical target spec**: the deterministic description of a target used for daemon identity. It includes target kind, roots, labels, and every index-shaping option that affects stored content.
- **enumerated file**: one filesystem entry produced by target enumeration, carrying the absolute path plus the relative and logical names that the index will persist.
- **revision token**: an optional freshness token carried by targets that have one. Git targets use HEAD SHA. Directory targets do not have a cheap authoritative global token and report null.
- **revision tracker**: the optional target-specific change source that publishes revision changes such as git HEAD movement. Git targets have one; directory targets do not.

These terms matter because the current code mixes three different concerns under "repo": path root, git semantics, and daemon identity. The feature becomes simpler once those are separated.

## 4. Proposed architecture

### 4.1 Introduce a target abstraction

Add one new project, `Indexed.Targets`, that owns target-neutral contracts and the non-git directory implementations. Keep `Indexed.Git` as the git-specific implementation layer.

Recommended responsibility split:

- `Indexed.Targets`
  - `TargetSpec`, `TargetKind`, `TargetId`
  - root-label and logical-path rules
  - directory-tree and directory-set enumeration
  - root validation and directory-mode admissibility helpers
- `Indexed.Git`
  - `GitRepository`, `GitProcess`
  - git-target adapter implementing the target contracts
  - git-specific revision tracking and `.gitattributes` binary overrides
- `Indexed.Core`
  - index storage, query planning/execution, batch writer, event queue
  - target-neutral watcher, reconciliation timer, binary heuristics
- `Indexed.Service`
  - target resolution, daemon lifecycle, HTTP surface
- `Indexed.Cli`
  - target selection and daemon discovery

This avoids pulling git-specific code into `Indexed.Core` while also avoiding a service/CLI dependency on the whole query engine merely to understand target identity.

### 4.2 Core contract

The exact type names are not important; the responsibilities are. Two interface constraints matter:

1. enumeration must stream rather than materialize a whole 100 K-file tree in memory before indexing starts;
2. the change pipeline should stay factored into target-neutral watchers/schedulers plus an optional target-specific revision tracker rather than bundling everything into one opaque façade.

```csharp
public enum TargetKind
{
    GitRepository,
    DirectoryTree,
    DirectorySet,
}

public sealed record TargetRootSpec(string? Label, string Path);
public readonly record struct TargetRoot(string Name, string AbsolutePath, bool IsPrimary);
public readonly record struct LogicalPath(string Value);

public sealed record TargetSpec(
    TargetKind Kind,
    IReadOnlyList<TargetRootSpec> Roots,
    IReadOnlyList<string>? IndexExcludeGlobs,
    bool UseDefaultIndexExcludes,
    bool UseDefaultDirectoryExcludes);

public readonly record struct EnumeratedFile(
    string AbsolutePath,
    string RelativePath,
    LogicalPath LogicalPath,
    long SizeBytes,
    DateTimeOffset LastWriteUtc);

public interface IIndexTarget
{
    TargetSpec Spec { get; }
    string TargetId { get; }
    IReadOnlyList<TargetRoot> Roots { get; }

    IAsyncEnumerable<EnumeratedFile> EnumerateFilesAsync(CancellationToken cancellationToken = default);
    bool TryMapAbsolutePath(string absolutePath, out LogicalPath logicalPath);
    string ResolveAbsolutePath(LogicalPath logicalPath);
    string? GetCurrentRevisionToken(CancellationToken cancellationToken = default);
}

public interface IRevisionTracker : IDisposable
{
    string? LastKnownRevisionToken { get; }
    void Start();
}
```

`IIndexTarget.EnumerateFilesAsync` is intentionally streaming. Git targets may still materialize `git ls-files` internally if that is the simplest correct implementation, but the contract does not require a full in-memory file list before the first batch can be indexed. Directory targets should stream directly from filesystem enumeration.

Progress accounting should be target-kind-sensitive. Git targets can often report an exact upfront total; directory targets should be free to report "processed so far" without an exact total until a cheap estimate exists.

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
        |                                   +--> streaming full enumeration
        |                                   +--> absolute/logical path mapping
        |                                   +--> optional revision token
        |
        +--> DirectoryWatcher(roots)
        +--> IRevisionTracker?  (git only; HeadPoller in the first cut)
        +--> ReconciliationScheduler
        |
        +--> SqliteIndex
        +--> DebouncingEventQueue
        +--> FullScanIndexer
        +--> IncrementalIndexer
        +--> CodeQueryPlanner / Executor
```

The important architectural shift is that `DaemonHost`, `FullScanIndexer`, and `IncrementalIndexer` should depend on `IIndexTarget`, not directly on `GitRepository`.

Invariant: `HeadMoved` remains part of the shared `IndexEvent` hierarchy in `Indexed.Core`, but only a git target's revision tracker emits it. The `IncrementalIndexer` branch that handles `HeadMoved` is intentionally unreachable in directory mode.

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
canonical = "indexed-target-v1" "\0"
            targetKindName "\0"
            rootCount "\0"
            foreach root in canonical-root-order:
                root.Label "\0"
                normalized-absolute-root-path "\0"
            useDefaultIndexExcludes "\0"
            useDefaultDirectoryExcludes "\0"
            excludeGlobCount "\0"
            foreach glob in canonical-glob-order:
                glob "\0"
targetId = SHA1(canonical)[0:12]
```

The canonical byte stream is built directly, not by hashing serializer output. That avoids load-bearing dependence on `System.Text.Json` emission details such as property ordering, null omission, numeric formatting, and escaping.

The canonical target spec must include:

- target kind;
- normalized absolute roots in a fixed canonical order;
- stable root labels when multiple roots are present;
- index-shaping options such as exclude globs, the default index-excludes flag, and the default directory-excludes flag.

On Windows, root-path normalization is case-insensitive and should collapse equivalent long-path / UNC spellings before hashing. The `"indexed-target-v1"` prefix reserves room for future canonicalization changes without silently re-hashing existing targets.

This ensures that materially different targets never reuse one another's `index.db`.

### 5.2 Compatibility rule for current git mode

To avoid needlessly orphaning current app-data directories, preserve the existing `repoId` formula only for the exact legacy default git case. The compatibility predicate is closed:

- `TargetKind == GitRepository`;
- exactly one root, and it equals the discovered git work-tree root;
- `UseDefaultIndexExcludes == true`;
- `IndexExcludeGlobs` is null or empty;
- `UseDefaultDirectoryExcludes == false`.

Any deviation produces a spec-derived `targetId`. Existing `%LOCALAPPDATA%\Indexed\<repoId>\` directories that no longer match the current target spec become orphans; normal daemon startup should never auto-delete them. A future `idx gc` command may prune them.

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
- `repoId` and `repoRoot`, serialized as `null` for non-git targets

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
```

`logical_path TEXT UNIQUE NOT NULL` already creates the needed implicit index; no extra explicit index is required. `code_fts`, `prose_fts`, and rowid = `file_id` stay unchanged.

### 6.3 Logical-path rules

- Git target: `logical_path == relative_path` exactly as today.
- Single directory-tree target: `logical_path == relative_path` exactly as today.
- Directory-set target: `logical_path == root_name + "/" + relative_path`.

This keeps the common single-root cases stable while giving multi-root targets collision-free, human-readable paths.

### 6.4 Root labels

For multi-root targets, labels are mandatory and user-supplied. The CLI shape should be explicit:

```text
idx find foo --root sdk=C:\src\proj\sdk --root docs=C:\src\proj\docs
```

Rules:

1. single-root targets use a bare path and keep today's relative-path namespace;
2. multi-root targets require a unique label per root;
3. labels participate in the canonical target spec and therefore in `targetId`;
4. once a label is part of a target, it is part of the user-visible query namespace and must never be changed silently.

If the CLI ever accepts a bare path in multi-root mode as sugar, it must fail fast on ambiguity and should still normalize internally to an explicit label before canonicalization. The proposal does not rely on auto-derived labels.

### 6.5 Rebuild policy

This is a schema bump. Rebuild is acceptable and consistent with current Indexed policy. There is no value in an in-place migration from repo-relative `files.path` to the root-aware v3 model.

Schema v3 rollout triggers a one-time rebuild of every existing `index.db`. No in-place migration is supported; release notes should call this out explicitly.

## 7. Enumeration and change detection by target kind

### 7.0 Shared admissibility rules

The current binary heuristic is only partly git-specific. The shared part should move into a target-neutral helper in `Indexed.Core`, for example `BinaryHeuristic.IsLikelyBinary(absolutePath, maxBytes)`, owning:

- size-cap rejection;
- first-8-KiB NUL-byte scan;
- unreadable / missing file classification as "not indexable".

Git targets then layer `.gitattributes` `binary` overrides on top. Directory targets do not.

Two default exclude sets should exist and remain conceptually separate:

- `DefaultBinaryAdjacentGlobs`: the current lockfile / minified-bundle / generated-code list used to keep the trigram index lean;
- `DefaultDirectoryModeExcludes`: a new directory-target-only list for trees that should never be wandered into by default, such as `.git/**`, `.hg/**`, `.svn/**`, `.bzr/**`, `node_modules/**`, `.venv/**`, `venv/**`, `__pycache__/**`, `bin/**`, `obj/**`, `target/**`, `.idea/**`, `.vs/**`, `.vscode/**`, `.gradle/**`, `build/**`, `dist/**`, `out/**`, `.next/**`, `.nuxt/**`, `coverage/**`, `.tox/**`, `.pytest_cache/**`, `.mypy_cache/**`, `Thumbs.db`, `.DS_Store`, `$RECYCLE.BIN/**`, and `System Volume Information/**`.

### 7.1 Git-repo target

Git mode remains behaviorally the same:

- enumeration: streamed `git ls-files` plus untracked-not-ignored;
- binary override: `.gitattributes` `binary`, layered over `BinaryHeuristic`;
- change feed: `DirectoryWatcher` + git revision tracker (`HeadPoller` in the first cut) + `git diff-tree` + reconciliation;
- revision token: HEAD SHA.

`DefaultDirectoryModeExcludes` do not apply in git mode. Git mode stays the preferred experience when the searched corpus is a real git working tree.

### 7.2 Directory-tree target

Directory mode should use:

- streamed recursive filesystem enumeration rooted at one selected directory;
- `BinaryHeuristic` + `DefaultBinaryAdjacentGlobs` + `DefaultDirectoryModeExcludes` + user-supplied exclude globs, but no dependence on `git.exe`;
- one watcher over that root;
- periodic reconciliation by direct filesystem walk;
- no revision token.

Symlink and reparse-point rule for the first cut:

- do not recurse through directory symlinks/junctions;
- only index files whose canonical full path remains under the selected root;
- surface skipped-reparse-point behavior in logs and status notes if it materially affects coverage.

Directory-mode startup must succeed even if `git.exe` is absent from `PATH`. The target implementation must not reach into `Indexed.Git` at all.

### 7.3 Directory-set target

Directory-set mode is directory mode repeated over several disjoint roots:

- one watcher per root, not one watcher on a guessed common ancestor;
- one reconciliation pass that unions the per-root file sets;
- overlapping or nested roots are rejected up front because they create duplicate namespace and watcher ambiguity;
- logical paths are root-prefixed as described above.

Requiring explicit disjoint roots is a feature, not a limitation. It keeps target identity, logical paths, and watcher ownership deterministic.

Multi-root reconciliation diffs by logical path, not relative path. The target enumerator yields `(absolutePath, root)`; `target.TryMapAbsolutePath(...)` produces the logical-path key used to compare against `_index.GetAllPathsWithShaAsync()`.

### 7.4 Root validation rules

Directory-set targets must validate roots before daemon startup:

- reject exact collisions after canonicalization;
- reject nested roots (`C:\src\proj` and `C:\src\proj\docs`);
- fold case on Windows so `C:\src\proj` and `c:\SRC\PROJ` collide;
- canonicalize long-path and UNC spellings before comparison;
- if a root itself is a symlink or junction, canonicalize its target first, then apply the same overlap rules.

These checks are boring but not optional; without them the first multi-root users will get duplicate watcher events and ambiguous logical paths.

### 7.5 Reconciliation and startup ordering

Directory-mode startup ordering is stricter than git mode:

1. validate and canonicalize roots;
2. arm `DirectoryWatcher` for all roots;
3. begin initial enumeration / scan;
4. let any watcher events raised during that scan accumulate in the debouncing queue;
5. enqueue an immediate `ReconciliationRequested` after the initial scan commits.

That ordering closes the "scan read the file before the watcher was armed" gap that would otherwise be large on a 100 K-file directory tree.

The first cut may keep `ReconciliationRequested` global rather than per-root. That is correct but can be wasteful. A future `ReconciliationRequested(rootId?)` refinement is reasonable if per-root overflow recovery becomes a real cost.

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

Compatibility rule: `IndexedHead`, `CurrentHead`, `RepoRoot`, and `RepoId` remain present in the JSON shape and are serialized as `null` for non-git targets rather than omitted. This preserves round-trip stability and keeps old clients from branching on field presence.

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

`LastReconciliationAt` requires one new persisted meta key, for example `last_reconciliation_at`, written after each completed reconciliation pass.

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
idx find <pattern> [existing options] [--root <dir>|<label=dir>]...
idx status [--root <dir>|<label=dir>]...
idx rescan [--root <dir>|<label=dir>]...
idx stop [--root <dir>|<label=dir>]...
idx daemons
```

Selection rules:

1. If one or more `--root` flags are present, use directory-based target resolution.
2. One root means `directory-tree`; several roots mean `directory-set` and require labels.
3. If no `--root` flags are present, preserve today's git-repo behavior.
4. `--repo-root` remains as a git-only compatibility alias.
5. `--repo-root` and `--root` are mutually exclusive; passing both is a parse error.
6. Add `--no-default-directory-excludes` for directory targets only; it is independent of `--no-default-excludes`.

This avoids a new management CLI just to get the first cut shipped. Repeating the same root set yields the same `targetId`, so daemon reuse still works.

`idx daemons` is the minimum read-side convenience for a multi-target world. It need not introduce a registry; it can simply enumerate `%LOCALAPPDATA%\Indexed\*\daemon.json` and print `targetId`, kind, roots, pid, and start time.

### 9.3 Status and daemon metadata

`StatusResponse` should grow additively to include:

- `TargetKind`
- `TargetId`
- `Roots`
- `PrimaryRoot`

Keep `RepoRoot` / `RepoId` in the DTO and in `daemon.json`, but serialize them as `null` when `TargetKind != git-repo`.

### 9.4 Query path behavior

`--glob` and `--exclude` continue to operate on the logical path namespace.

Examples:

- single-root directory target: `src/**/*.cs`
- multi-root target: `sdk/**/*.cs`, `docs/**/*.md`

This preserves the current mental model: query-time path filtering applies to what the user sees in results.

## 10. Implementation plan

### Workstream A — generalize target ownership

Rough size: ~300-500 LOC across 8-12 existing files plus one new project.

- add `Indexed.Targets`;
- move daemon identity and target selection off `GitRepository.Open(...)`;
- make `DaemonPaths`, `DaemonInfo`, and the CLI client target-aware;
- replace the monolithic "change tracker" idea with `IRevisionTracker` plus shared `DirectoryWatcher` / `ReconciliationScheduler`;
- keep legacy git behavior as a compatibility path.

### Workstream B — schema and logical-path migration

Rough size: ~250-400 LOC across 6-10 files.

- schema v3 with `roots` table and root-aware `files`;
- root-aware absolute/logical path mapping;
- target metadata persisted in `meta`, including `last_reconciliation_at`.

### Workstream C — directory-tree mode

Rough size: ~450-700 LOC across 10-15 files.

- implement streaming direct filesystem enumeration;
- add `DefaultDirectoryModeExcludes`;
- implement directory target watcher/reconciliation;
- arm the watcher before the initial scan begins;
- wire target-agnostic `FullScanIndexer` / `IncrementalIndexer`;
- add directory-mode integration tests, including a scenario where `git.exe` is absent from `PATH`.

### Workstream D — directory-set mode

Rough size: ~300-500 LOC across 8-12 files.

- labeled multi-root CLI and target-spec parsing;
- one watcher per root;
- overlap validation, root canonicalization, and logical-path reconciliation;
- path-collision and duplicate-filename tests.

### Workstream E — contract and doc cleanup

Rough size: ~200-350 LOC across 8-14 files.

- freshness DTO evolution;
- status/daemon metadata additions;
- `idx daemons`;
- `--root` / `--repo-root` mutual-exclusion handling;
- CLI help and usage guide updates;
- new tutorial/examples for directory targets;
- security and sensitive-root guidance;
- retire any repo-only language in current-state docs that becomes too narrow.

The intended sequencing is A -> B -> C -> D -> E. Git mode should remain green after A, B, and C; multi-root adds the only user-visible namespace change.

## 11. Test plan

The new feature needs contract-level and architecture-level coverage, not only unit tests.

### 11.1 Target abstraction tests

- git target still enumerates tracked plus untracked-not-ignored files;
- directory target streams direct filesystem contents without git;
- multi-root target rejects overlapping and nested roots and preserves explicit labels;
- absolute-path mapping never escapes a selected root.

### 11.2 Incremental behavior tests

- single-file edit in directory mode becomes queryable without a restart;
- root-local rename produces delete + upsert with stable logical path behavior;
- watcher overflow or temporary watcher failure is repaired by reconciliation;
- root temporarily unavailable during scan is surfaced as degraded rather than corrupting the index;
- an edit that lands during the initial directory scan is observed because the watcher was armed before scanning began.

### 11.3 Compatibility tests

- existing git-mode CLI flows still work with no extra flags;
- existing git-mode freshness still reports HEAD tokens;
- current path globs in single-root targets behave the same as today;
- one git repo with default settings still maps to the legacy app-data directory;
- directory-mode startup succeeds end to end when `git.exe` is not on `PATH`.

### 11.4 Query namespace tests

- identical relative paths under different roots do not collide;
- `--glob` and `--exclude` operate on logical paths exactly as documented;
- search results show stable logical paths across daemon restarts for the same target spec;
- `--repo-root` and `--root` together produce a parse error with a clear diagnostic;
- `idx daemons` lists active daemons without requiring a write-side registry.

## 12. Risks and trade-offs

### 12.1 Directory mode is less authoritative than git mode

Git mode knows exactly which files are in scope and can cheaply observe branch movement. Directory mode has to trust filesystem walks plus watcher hints. That is acceptable as long as the product tells the truth:

- git mode is richer;
- directory mode is broader;
- the two are not equivalent.

### 12.2 Multi-root path design is user-visible

Once a logical path format ships, agents and scripts will depend on it. For multi-root targets, labels are therefore part of the stable public contract and must never be changed silently.

### 12.3 Watcher pressure increases with unrelated roots

Several roots means several watchers, more reconciliation work, and more chances for transient root-specific errors. The current single-writer model is still valid, but status output should surface root-level degradations rather than bury them in logs. The first cut may keep reconciliation global rather than per-root; a `ReconciliationRequested(rootId?)` refinement is an acceptable later performance optimization.

### 12.4 Ignore semantics must stay explicit

Directory mode should not silently inherit partial git semantics. If later we want `.indexedignore`, `.ignore`, or root-local policy files, that should be a deliberate follow-up with an explicit contract.

### 12.5 Initial scan must not create a blind spot

Directory targets are likely to have longer cold-start scans than git targets. The watcher-armed-before-scan rule is therefore a correctness requirement, not an optimization. For a representative 100 K-file tree on SSD, the working target should remain "cold full scan completes in the same rough class as current large git-repo rebuilds" rather than silently relaxing performance expectations for directory mode.

### 12.6 Per-root health will eventually matter

One freshness block remains enough for the first cut, but multi-root targets will likely want a later `Roots[]` health surface carrying per-root pending counts, watcher faults, and last-error state. The proposal should leave room for that additive DTO evolution.

## 13. Open questions

1. After shipping `idx daemons`, do we eventually need a persisted named-target registry, or is read-only daemon discovery enough for the likely workflows?
2. Should single-root non-git mode reuse bare relative paths forever, or should every non-git target eventually expose an explicit root label for total uniformity?
3. Is a future `.indexedignore` file worth introducing, or should explicit CLI/API excludes remain the only durable directory-mode policy?
4. Do we want a later Windows-only optimization path that uses the NTFS USN journal for faster large-directory reconciliation, or is `FileSystemWatcher` plus periodic enumeration sufficient for the expected workloads?
5. Should Indexed eventually refuse or require explicit override for obviously sensitive roots such as `%USERPROFILE%`, `%USERPROFILE%\.ssh`, `C:\Windows`, `C:\`, or `/etc`?

## 14. Recommendation

Proceed by generalizing Indexed around a target abstraction, not by bolting directory enumeration directly into the current git classes. The current codebase is already well-positioned for this:

- the query and storage engines are reusable;
- the change pipeline is almost reusable;
- the main work is to separate target identity and file-set ownership from git-specific semantics.

If done this way, Indexed becomes a broader local indexing service without giving up what currently makes it good at repository search. Git mode remains the high-fidelity path for repositories. Directory modes expand the product into the much larger space of local code and documentation trees that are not under git control but still benefit from always-warm search.

## 15. Security implications

Directory mode inherits the existing core security model:

- daemon binds loopback only;
- `/search` and `/status` remain unauthenticated read endpoints on localhost;
- destructive endpoints remain token-protected;
- the daemon never writes outside its own state directory.

What changes is the containment rule. In git mode, path containment means "under the repo root." In directory mode it means "under one of the validated target roots, and under the specific root that owns the logical path being resolved." The existing `FileContentProvider` `OutOfRoot` behavior is the model to preserve.

Users pointing `--root` at sensitive directories are explicitly opting into making those files searchable by any local process that can reach the loopback daemon. Documentation should warn about this. A future safety valve such as "refuse obviously sensitive roots unless `--allow-sensitive-root` is present" is reasonable, but not required for the first cut.
