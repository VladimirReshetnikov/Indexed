# Indexed Workspace Targets — Phase 01 Implementation Plan

- Created (UTC): 2026-04-23T18:51:54Z
- Repository HEAD: 10b104a4d33770ef498b57c64923116f4edad489
- Status: Planned
- Phase scope: Target abstractions, target identity, daemon discovery metadata, legacy-compatibility rules, and the first non-repo-centric contract plumbing needed before schema and directory-mode work can land.
- Inputs:
  - [Workspace targets proposal](../Indexed-Workspace-Targets-Proposal.md)
  - [Workspace targets proposal review](../reports/Indexed-Workspace-Targets-Proposal-Review__840681494a06.md)
  - [Architecture](../Indexed-Architecture.md)
  - Current source under `src/Indexed/src/Indexed.{Abstractions,Cli,Core,Git,Service}`

## 1. Objectives

Phase 01 establishes the new target model without yet changing the storage schema or adding non-git file enumeration. The point of this phase is to create the architectural seam that later phases will build on, while keeping current git mode correct and compatible.

Normative goals for this phase:

1. Introduce a target-spec and target-id model that can represent git, directory-tree, and directory-set targets.
2. Generalize daemon identity and discovery from repo-centric to target-centric while preserving the legacy `repoId` cache path for the exact default git case.
3. Evolve daemon metadata and public DTOs so they can describe either git or non-git targets truthfully.
4. Land the new abstractions without yet requiring schema v3 or direct filesystem enumeration.
5. Keep existing git-mode CLI flows and daemon reuse behavior green.

Non-goals for this phase:

- schema v3;
- multi-root logical-path storage;
- direct filesystem enumeration;
- watcher-before-scan startup ordering for directory mode;
- root validation and directory-mode reconciliation;
- final CLI `--root` behavior beyond parseable scaffolding if needed for shared contract work.

## 2. Why this phase is separate

This phase deliberately isolates identity and contract evolution from file-set evolution.

Reasoning:

- `DaemonHost`, `DaemonClient`, `DaemonInfo`, `StatusResponse`, and `Freshness` currently embed repository assumptions in ways that would otherwise force every later phase to cut across the full stack at once.
- The legacy `repoId` rule is subtle enough that it deserves focused tests before schema churn makes regressions harder to localize.
- Getting `TargetSpec` and `TargetId` right first lets later phases consume a stable abstraction instead of repeatedly rewriting ad-hoc plumbing.

## 3. Deliverables

Phase 01 is complete when all of the following exist and are wired through the current git flow:

- one target-neutral contract layer with at least:
  - `TargetKind`
  - `TargetRootSpec`
  - `TargetRoot`
  - `TargetSpec`
  - `TargetId`
- one git-target adapter or equivalent bridge from `GitRepository` into the new target contracts;
- daemon paths and discovery keyed by `targetId`, with legacy git compatibility preserved exactly where intended;
- `daemon.json` expanded to carry target metadata in addition to repo compatibility fields;
- `StatusResponse` and `Freshness` evolved additively so later non-git targets do not need another breaking reshape;
- tests pinning the compatibility predicate and wire-format expectations.

## 4. Planned code changes

### 4.1 New target contract layer

Create a new `Indexed.Targets` project and add it to `Indexed.sln`.

Planned contents:

- shared contract types;
- canonical target-spec normalization and hashing;
- validation helpers that are phase-safe even before directory mode is implemented;
- a git-target adapter that wraps `GitRepository` without changing git enumeration behavior yet.

Design rule:

- `Indexed.Targets` may depend on `Indexed.Core` abstractions only if that dependency is genuinely target-neutral. Prefer avoiding a dependency from targets into the index/query engine in this phase.

### 4.2 Identity and compatibility plumbing

Replace direct `RepoId.Compute(repoRoot, firstCommitSha)` usage in the service and CLI startup path with a target-oriented computation.

Planned shape:

- preserve `RepoId.Compute(...)` as the legacy helper used only inside the closed compatibility predicate;
- add `TargetId.Compute(TargetSpec normalizedSpec, LegacyRepoIdentity? legacy = null)` or equivalent;
- make `DaemonPaths` resolve by target id rather than repo id;
- update mutex naming to key off target id rather than repo id.

Compatibility predicate to encode and test:

1. target kind is `git-repo`;
2. exactly one root;
3. root equals the discovered git work-tree root;
4. `UseDefaultIndexExcludes == true`;
5. `IndexExcludeGlobs` empty;
6. `UseDefaultDirectoryExcludes == false`.

Anything outside that predicate gets a spec-derived `targetId`.

### 4.3 DTO and metadata evolution

Evolve the public shape additively.

Planned changes:

- `DaemonInfo` gains `TargetId`, `TargetKind`, `Roots`, and `PrimaryRoot`;
- `StatusResponse` gains the same target fields;
- `RepoRoot` and `RepoId` remain present and serialize as `null` for non-git targets;
- `Freshness` gains target-neutral revision fields now, even if git mode remains the only implemented producer in this phase.

Compatibility rule:

- this phase preserves field names and casing for all existing fields;
- added fields may appear in JSON, but old fields must not disappear.

### 4.4 CLI and launcher updates

Refactor CLI startup to resolve a target first, then a daemon path from that target.

Planned scope:

- keep current default behavior: no generic root flags means discover enclosing git repo and use git mode;
- keep `--repo-root` working as today for git mode;
- if shared parsing work is cheap and low-risk, introduce dormant `--root` parsing scaffolding guarded so it does not misrepresent support before later phases land;
- otherwise defer user-visible `--root` parsing to a later phase and keep the phase-01 CLI surface git-only but target-based internally.

The controlling rule is truthfulness in help text. The CLI must not advertise directory targets before the daemon can actually serve them.

## 5. Test plan for this phase

### 5.1 Identity tests

- canonical target-id stability for equivalent normalized specs;
- target-id difference when exclude policy differs;
- target-id difference when labels or root order semantics differ after normalization;
- exact legacy `repoId` reuse for the closed compatibility predicate;
- divergence from legacy `repoId` for any predicate violation.

### 5.2 JSON contract tests

- `DaemonInfo` round-trips with new target fields;
- `StatusResponse` round-trips with target fields and `RepoRoot`/`RepoId` compatibility nulls;
- `Freshness` round-trips with additive revision fields.

### 5.3 Integration tests

- existing git daemon startup still works from the CLI default path;
- daemon metadata is written under `%LOCALAPPDATA%\Indexed\<targetId>\`;
- default git flows still find an existing warm daemon started before the refactor.

## 6. Risks and mitigations

### Risk: accidental warm-cache invalidation for existing git users

Mitigation:

- encode the compatibility predicate in dedicated tests, not just in prose;
- keep the legacy helper as an explicit code path instead of re-deriving it indirectly from the new hashing logic.

### Risk: contract churn across multiple phases

Mitigation:

- add target-neutral fields in phase 01 even if directory mode does not use all of them yet;
- keep repo compatibility fields present so later phases are additive rather than disruptive.

### Risk: over-advertising unsupported modes

Mitigation:

- phase-01 docs and help text remain explicit that only git-target execution is implemented until later phases land;
- any dormant parser support must be behind non-advertised or test-only code paths, or be deferred entirely.

## 7. Exit criteria

Phase 01 exits when:

1. the new target/identity layer exists and is exercised by the current git flow;
2. all repo-id compatibility tests pass;
3. JSON contract tests pass with the new additive fields;
4. the rest of the codebase can depend on `TargetSpec` / `TargetId` instead of raw repo-root assumptions in later phases.

## 8. Expected size

Planned implementation magnitude for phase 01:

- roughly 350-700 LOC across 10-16 files;
- one new project plus solution updates;
- 10-20 new/expanded tests across abstractions, CLI, and service layers.
