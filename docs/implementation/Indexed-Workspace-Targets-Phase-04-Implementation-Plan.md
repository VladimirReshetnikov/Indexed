# Indexed Workspace Targets — Phase 04 Implementation Plan

- Created (UTC): 2026-04-23T19:55:03Z
- Repository HEAD: 10b104a4d33770ef498b57c64923116f4edad489
- Status: Planned
- Phase scope: public CLI and service-entrypoint support for directory targets, daemon enumeration, current-state documentation refresh, final decision-log updates, and end-to-end validation.
- Depends on:
  - [Phase 01 plan](./Indexed-Workspace-Targets-Phase-01-Implementation-Plan.md)
  - [Phase 02 plan](./Indexed-Workspace-Targets-Phase-02-Implementation-Plan.md)
  - [Phase 03 plan](./Indexed-Workspace-Targets-Phase-03-Implementation-Plan.md)
  - [Workspace targets proposal](../Indexed-Workspace-Targets-Proposal.md)
  - [Implementation decision log](./Indexed-Workspace-Targets-Decision-Log.md)

## 1. Objectives

Phase 04 turns the now-working runtime into a usable feature surface.

Normative goals:

1. Add first-class CLI target selection with repeated `--root` flags.
2. Preserve default git behavior when no `--root` flags are present.
3. Enforce the documented compatibility rules around `--repo-root`, multi-root labels, and default-directory excludes.
4. Add `idx daemons` as the minimum operator-facing discovery surface for multiple live targets.
5. Refresh current-state docs so the product is described as a target-based indexing service rather than a git-only daemon.
6. Record the design decisions made across phases 02-04 and run full validation before close-out.

Non-goals:

- introducing a persisted named-target registry;
- implementing garbage collection for orphaned target directories;
- changing the HTTP DTO surface beyond what phases 01-03 already required.

## 2. CLI contract to ship

### 2.1 Target selection

Supported forms:

```text
idx find <pattern> [existing options] [--root <dir>]...
idx find <pattern> [existing options] [--root <label=dir>]...
idx status [--root <dir>|<label=dir>]...
idx rescan [--root <dir>|<label=dir>]...
idx stop [--root <dir>|<label=dir>]...
idx daemons [--json]
```

Selection rules to enforce:

1. No `--root` flags means preserve today's git-target behavior.
2. `--repo-root` remains the git compatibility selector and is mutually exclusive with `--root`.
3. Exactly one `--root` must be a bare path and maps to `directory-tree`.
4. Two or more `--root` flags require explicit `LABEL=PATH` syntax for every root and map to `directory-set`.
5. `--no-default-directory-excludes` is accepted only for directory targets and remains independent of `--no-default-excludes`.

### 2.2 Output expectations

- `idx status` text output should remain compact but clearly identify target kind and roots.
- `idx daemons` text output should list at least target kind, target id, pid, start time, and roots.
- JSON output for `idx daemons` should be a stable serialized collection of `DaemonInfo` records.

## 3. Planned code changes

### 3.1 Argument parsing and CLI model

Update the CLI parse layer so it can represent:

- repeated roots;
- directory-default exclude toggle;
- the new `Daemons` verb;
- mutual exclusion diagnostics for `--repo-root` plus `--root`;
- multi-root label validation that matches the target-spec rules.

The parse model should carry enough information to build one target-selection object without reinterpreting raw strings elsewhere in the CLI.

### 3.2 Target-aware daemon launch and adoption

Refactor the CLI-service boundary so daemon startup is target-aware rather than repo-root-only.

Required behavior:

- the client computes the target id from the selected target and probes the matching `daemon.json`;
- the launcher passes directory-target arguments through to `Indexed.Service`;
- the service entrypoint reconstructs `DaemonOptions.TargetSelection` correctly for git, directory-tree, and directory-set modes.

### 3.3 Daemon enumeration

Add a lightweight read-only discovery path that:

- enumerates `%LOCALAPPDATA%\\Indexed\\*\\daemon.json`;
- tolerates unreadable or malformed entries;
- optionally filters out dead daemons only if that can be done cheaply and safely;
- reuses `DaemonInfo` parsing rather than inventing a second catalog format.

### 3.4 Documentation refresh

Update current-state docs to reflect the shipped behavior:

- [README](../README.md)
- `Indexed-Usage-Guide.md`
- `Indexed-Tutorial.md`
- `Indexed-Architecture.md`

Documentation requirements:

- explain the new target kinds and logical-path behavior;
- document `--root`, `--repo-root`, `--no-default-directory-excludes`, and `idx daemons`;
- state the directory-mode safety caveat for sensitive roots;
- keep git mode presented as the preferred mode when the corpus is a real git working tree.

### 3.5 Decision log completion

Append decisions covering:

- the exact CLI grammar chosen for `--root`;
- label-validity rules if tightened during implementation;
- `idx daemons` output shape and whether it exposes JSON;
- any divergence between proposal wording and shipped behavior.

## 4. Tests for this phase

### 4.1 CLI parser tests

- single bare `--root` selects directory mode cleanly;
- multi-root invocations require labels;
- `--repo-root` plus `--root` is a parse error;
- `--no-default-directory-excludes` composes correctly and is rejected or ignored with a clear rule in git mode;
- `idx daemons` parses with and without `--json`.

### 4.2 CLI/service integration coverage

- a directory-tree target launched through the public CLI argument path answers `status` and `find`;
- a labeled directory-set target is discoverable by target id reuse;
- `idx daemons` can read back live daemon metadata from the app-data tree.

### 4.3 Regression coverage

- default git-mode invocation still uses the legacy repo path without requiring new flags;
- daemon adoption still works for an already-running git target;
- existing JSON-contract tests stay green.

## 5. Risks and mitigations

### Risk: CLI and service drift on root syntax

Mitigation:

- centralize `LABEL=PATH` parsing in shared code used by both the CLI and the daemon entrypoint;
- add parser tests for both single-root and multi-root forms.

### Risk: daemon enumeration turns into a second lifecycle system

Mitigation:

- keep `idx daemons` read-only and file-backed;
- explicitly avoid any registry writes or background cleanup in this phase.

### Risk: current-state docs lag the implementation

Mitigation:

- treat README/usage/architecture/tutorial updates as exit criteria, not stretch work;
- align terminology on "target", "root", and "logical path" across docs before final validation.

## 6. Exit criteria

Phase 04 exits when:

1. `idx` can launch or adopt git, directory-tree, and directory-set daemons through the public CLI surface;
2. `idx daemons` works against the on-disk daemon catalog;
3. current-state docs describe workspace targets as a shipped capability;
4. the decision log captures the final implementation choices;
5. `dotnet build` and `dotnet test` for `src/Indexed/Indexed.sln` complete successfully.

## 7. Expected size

Planned implementation magnitude for phase 04:

- roughly 700-1400 LOC across 10-20 files;
- one public CLI-surface expansion, one daemon-entrypoint parser expansion, and one documentation refresh across the main current-state docs;
- 10-25 updated or new tests across CLI, service, and integration coverage.
