# Indexed Workspace Targets — Implementation Decision Log

- Created (UTC): 2026-04-23T18:51:54Z
- Repository HEAD: 10b104a4d33770ef498b57c64923116f4edad489
- Status: Active working log
- Purpose: durable record of design decisions made while implementing workspace targets, including rejected alternatives when the reason is important for future maintenance.

## How to read this log

- Entries are append-only except for typo fixes and status updates.
- Each entry records the decision, the reasoning, and the consequences for later phases.
- "Accepted" means implemented or intentionally queued as the working design.
- "Deferred" means consciously postponed, not forgotten.

## Decisions

### D01. Keep implementation staged, with a plan document before each phase

- Status: Accepted
- Date (UTC): 2026-04-23
- Decision:
  - Implement workspace targets in explicit phases rather than one monolithic rewrite.
  - Create a separate detailed implementation-plan document before each phase begins.
- Reasoning:
  - The feature crosses identity, schema, filesystem enumeration, watcher semantics, DTO contracts, CLI UX, and documentation.
  - The user explicitly asked for detailed documentation and per-phase planning.
  - Staging reduces the risk of ending with a half-migrated repo model that neither preserves git mode nor cleanly supports directory mode.
- Consequences:
  - Some intermediate commits will intentionally carry abstractions that are only fully exercised by later phases.
  - Documentation will be treated as part of the implementation, not post-hoc cleanup.

### D02. Preserve the current git experience as the default and compatibility baseline

- Status: Accepted
- Date (UTC): 2026-04-23
- Decision:
  - No generic root flags means "discover enclosing git repository and use git mode" remains the default CLI behavior.
  - The legacy `repoId` mapping remains valid only for the exact default git-target predicate captured in the proposal.
- Reasoning:
  - The user asked for directory mode to complement rather than supersede git mode.
  - Existing git behavior is both the current product contract and the easiest high-signal regression detector while the feature lands.
- Consequences:
  - Every phase must include compatibility tests for default git flows.
  - New target machinery must fit around the git path rather than replacing it wholesale.

### D03. Create a dedicated `Indexed.Targets` project instead of hiding the abstraction inside `Indexed.Service`

- Status: Accepted
- Date (UTC): 2026-04-23
- Decision:
  - Put target contracts, target-id computation, and non-git target implementations in a new assembly.
- Reasoning:
  - Target identity and target semantics are not service concerns only; the CLI and core indexers also need them.
  - Keeping the abstraction in `Indexed.Service` would recreate repo-centric layering by making the service the owner of concepts that are actually cross-layer contracts.
- Consequences:
  - Solution/project references will change early.
  - Some code that currently points directly at `Indexed.Git` will instead point at `Indexed.Targets`, with git-specific behavior injected via an adapter.

### D04. Prefer additive public-contract evolution over field replacement

- Status: Accepted
- Date (UTC): 2026-04-23
- Decision:
  - `StatusResponse`, `Freshness`, and `daemon.json` gain target-neutral fields while preserving repo/head compatibility fields.
- Reasoning:
  - There are already JSON contract tests and the product is aimed at scripts and agents.
  - Replacing fields now would create avoidable churn before the new mode even ships.
- Consequences:
  - Non-git targets will use `null` compatibility fields rather than field omission.
  - Documentation must be explicit about which fields are compatibility aliases versus preferred future vocabulary.

### D05. Keep `Indexed.Targets` pure and move the git adapter into `Indexed.Git`

- Status: Accepted
- Date (UTC): 2026-04-23
- Decision:
  - `Indexed.Targets` contains shared target contracts, canonicalization, and identity logic, but does not reference `Indexed.Git`.
  - The runtime git implementation lives in `GitIndexTarget` under `Indexed.Git`.
- Reasoning:
  - Public DTOs need to reference target types, and `Indexed.Abstractions` now depends on `Indexed.Targets`.
  - If `Indexed.Targets` referenced `Indexed.Git`, that dependency would leak upward into the abstractions layer and blur the architectural seam the feature is trying to establish.
- Consequences:
  - Git-only behaviors such as `diff-tree` expansion and `.gitattributes` binary overrides are surfaced through optional target capabilities rather than through the core target interface itself.

### D06. Treat `RepoId` as a compatibility alias, not the new daemon identity

- Status: Accepted
- Date (UTC): 2026-04-23
- Decision:
  - The daemon now discovers itself by `targetId`, while `repoId` remains available only as git compatibility metadata.
- Reasoning:
  - The previous startup protocol incorrectly assumed that one repository path implied one indexing configuration.
  - Keeping `repoId` around is still useful for continuity and diagnostics, but it can no longer be the storage/mutex/discovery key once exclude policy becomes target-defining.
- Consequences:
  - `daemon.json` and `/status` now carry both target metadata and repo compatibility metadata.
  - Future non-git targets will serialize `repoRoot` / `repoId` as `null` rather than omitting the fields.

### D07. Make schema v3 root-aware and keep logical paths as the public query namespace

- Status: Accepted
- Date (UTC): 2026-04-23
- Decision:
  - Add a `roots` table and replace the single `files.path` identity with `root_id + relative_path + logical_path`.
  - Keep `logical_path` as the string surfaced to queries, search results, and glob filters.
- Reasoning:
  - Multi-root targets need a stable public namespace that does not collapse files with the same relative path from different roots.
  - Storing only absolute paths would make the DB less portable and would duplicate information already represented by roots.
- Consequences:
  - Schema version bumped to 3.
  - Existing warm indexes rebuild once on upgrade.
  - Git targets preserve today's visible path shape because their logical path still equals the repo-relative path.

### D08. Arm directory watchers before the initial full scan begins

- Status: Accepted
- Date (UTC): 2026-04-23
- Decision:
  - Start `DirectoryWatcher`, optional `IRevisionTracker`, and `ReconciliationScheduler` before the first full scan for real daemon starts.
- Reasoning:
  - Directory-mode cold scans can be long enough that post-read, pre-watcher edits are otherwise lost.
  - Duplicate notifications are cheap; missed edits are correctness bugs.
- Consequences:
  - Startup order now favors correctness over minimal duplicate work.
  - Debouncing plus SHA-based unchanged detection absorb the extra churn.

### D09. Ship an explicit, non-magical CLI grammar for directory targets

- Status: Accepted
- Date (UTC): 2026-04-23
- Decision:
  - One `--root <dir>` means `directory-tree`.
  - Two or more `--root <label=dir>` arguments mean `directory-set`.
  - `--repo-root` and `--root` are mutually exclusive.
  - `--no-default-directory-excludes` is rejected unless at least one `--root` is present.
- Reasoning:
  - A small amount of extra syntax is cheaper than silently changing namespaces or auto-deriving labels that later surprise users.
  - Parse-time rejection of mutually incompatible flags keeps daemon identity deterministic and user intent visible.
- Consequences:
  - Single-root directory targets keep the familiar relative-path namespace.
  - Multi-root users must choose labels deliberately, and those labels become part of both `targetId` and the search namespace.

### D10. Restrict root labels to a path-safe subset

- Status: Accepted
- Date (UTC): 2026-04-23
- Decision:
  - Reject root labels that contain `/`, `\`, `=`, control characters, or the special names `.` / `..`.
- Reasoning:
  - Labels are embedded directly into logical paths as `<label>/<relative-path>`.
  - Allowing path-separator-like characters would make parsing, matching, and future tooling ambiguous for little benefit.
- Consequences:
  - Label validation now happens in the shared target-normalization layer, not only in the CLI.
  - The daemon entrypoint and the CLI enforce the same label rules.

### D11. Keep `idx daemons` file-backed and read-only in the first cut

- Status: Accepted
- Date (UTC): 2026-04-23
- Decision:
  - Implement `idx daemons` by enumerating `%LOCALAPPDATA%\\Indexed\\*\\daemon.json` and returning parsed `DaemonInfo` records.
  - Support both text and `--json` output, but do not introduce a mutable registry or liveness side effects.
- Reasoning:
  - The missing operator capability was "what daemon descriptors exist?", not "yet another stateful subsystem."
  - Reusing `DaemonInfo` avoids inventing a second source of truth for target metadata.
- Consequences:
  - `idx daemons` is cheap and transparent.
  - Stale but parseable `daemon.json` files can still appear until a future cleanup command exists; that trade-off is acceptable for this phase.
