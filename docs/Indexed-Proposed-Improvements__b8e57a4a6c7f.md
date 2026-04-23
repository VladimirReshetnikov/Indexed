# Indexed — Proposed Improvements

- Created (UTC): 2026-04-23T21:12:46Z
- Repository HEAD: e5c1e2b48eea1534033dbf6bcd549b2059db91e7

## 1. Purpose

This document proposes the next set of improvements for `Indexed` after the
workspace-target expansion that introduced git-repository, directory-tree, and
directory-set targets. The goal is not to redefine the product. The goal is to
make the existing product more complete, more operable, safer in directory
mode, and easier to use repeatedly.

This report is grounded in the current project docs and review material,
especially:

- [`../README.md`](../README.md)
- [`./Indexed-Architecture.md`](./Indexed-Architecture.md)
- [`./Indexed-Workspace-Targets-Proposal.md`](./Indexed-Workspace-Targets-Proposal.md)
- [`./Indexed-Code-and-Architecture-Review__c0883d924cbd.md`](./Indexed-Code-and-Architecture-Review__c0883d924cbd.md)
- [`./Indexed-Size-Reduction-SafeNearTerm-Plan.md`](./Indexed-Size-Reduction-SafeNearTerm-Plan.md)

## 2. Current Baseline

`Indexed` is already a useful local search daemon:

- it maintains a durable SQLite/FTS5 code index;
- it supports git-driven incremental refresh plus watcher-driven refresh;
- it can now index a git repo, a single non-git directory tree, or an explicit
  labeled set of directories;
- it exposes an agent-friendly localhost HTTP surface and a thin CLI;
- it has explicit freshness reporting and target-aware daemon discovery.

The main gaps are now concentrated in six areas:

1. Feature completeness is still uneven because Stage 3 prose indexing is
   missing.
2. Operational visibility is still mostly target-global even though multi-root
   directory sets are now first-class.
3. Repeated use of non-git targets is still more manual than it should be.
4. Storage footprint and target-directory hygiene matter more now that Indexed
   is not repo-only.
5. Directory mode has a broader trust boundary than git mode and deserves more
   explicit safety guardrails.
6. The implementation remains Windows-only even though the product concept is
   not inherently Windows-specific.

## 3. Prioritization Principles

The recommendations below follow these principles:

- Keep git mode as the best experience for actual repositories. Directory mode
  should complement it, not blur or replace it.
- Prefer additive DTO, protocol, and on-disk evolution over flag days.
- Make daemon state more explicit instead of inferring health from partial
  signals.
- Invest in repeatable workflows for recurring workspaces, not only one-off
  command lines.
- Do not widen scope into semantic navigation, vector search, or
  cross-daemon federation until the single-target daemon model is more mature.

## 4. Recommended Improvements

### 4.1 Complete Stage 3: Prose Indexing and Truthful `auto` Mode

This is the most obvious missing piece. The README still lists Stage 3 as
pending, and the original architecture proposal treated prose search as a
first-class part of the product rather than an optional extra.

**What to add**

- A dedicated extraction layer for prose spans, preferably in a new
  `Indexed.Extractors` project.
- A `prose_fts` index and the schema/version work needed to store extracted
  spans alongside code rows.
- Whole-file prose extraction for Markdown and plain text.
- Roslyn-backed extraction for C# XML doc comments and comment blocks.
- Honest `mode` behavior: once prose exists, `auto` can really mean
  code-plus-prose; until then, unsupported combinations should stay explicit.
- Search result semantics that align docs, DTOs, and implementation for
  `kind`, `TotalMatches`, sorting, and span reporting.

**Why it matters**

- It closes the biggest documented product gap.
- It increases the value of Indexed for agent workflows that search comments,
  XML docs, notes, and design docs as often as raw code.
- It makes the existing architecture more internally consistent: Indexed stops
  being "code search plus future prose" and becomes the mixed code/prose engine
  it was designed to be.

**Suggested scope**

- One schema bump.
- One new extraction-focused project plus updates across `Indexed.Core`,
  `Indexed.Service`, `Indexed.Cli`, and tests.
- Roughly 15-25 source files and 30-50 focused tests.

### 4.2 Add a First-Class Target Registry for Recurring Workspaces

The current `--root` grammar is explicit and good, but repeated directory-mode
usage is still too manual. If a user regularly searches the same non-git
workspace, they should not need to restate the full target spec every time.

**What to add**

- A user-local target registry, for example under
  `%LOCALAPPDATA%\\Indexed\\targets.json`.
- CLI commands such as:
  - `idx target add`
  - `idx target list`
  - `idx target show`
  - `idx target remove`
- `idx find --target <name>` and `idx status --target <name>` as ergonomic
  aliases for stored `TargetSpec` instances.
- Optional stored defaults per target for index excludes or query excludes.

**Design recommendation**

The registry name should not replace target identity. It should point at a full
immutable `TargetSpec`. The daemon should still be keyed by `targetId`, not by
the friendly registry alias. That keeps caching and daemon adoption semantics
stable while improving UX.

**Why it matters**

- It turns directory mode from "possible" into "pleasant."
- It reduces accidental target duplication caused by ad hoc relabeling.
- It gives Indexed a durable concept of "my workspaces" without requiring
  cross-target search.

**Suggested scope**

- One local JSON registry format with explicit versioning.
- Roughly 8-14 files across CLI, target parsing, and discovery logic.
- 15-25 tests focused on alias resolution and compatibility behavior.

### 4.3 Expand `/status` into a Real Operational Surface

`Indexed` now supports multi-root targets, but most status still collapses down
to one target-global freshness block. That is enough for correctness, but not
for diagnosis.

**What to add**

- A `roots[]` section in `/status` and CLI JSON output with fields such as:
  `name`, `path`, `state`, `pendingCount`, `watcherState`, `lastError`, and
  `lastReconciliationAt`.
- Daemon-level metrics such as:
  `initialScanInProgress`, `reconciliationInProgress`, `inFlightBatch`,
  `queueDepth`, `lastBatchStats`, and `indexBytes`.
- A fast administrative lane for `/status` so liveness checks are not starved
  behind ordinary search requests.
- Clear reporting of degraded states such as unreadable roots, watcher faults,
  or repeated fallback reconciliation.

**Why it matters**

- Multi-root operation needs root-level diagnosis.
- It makes Indexed easier to trust in unattended agent workflows.
- It gives future features like `idx gc`, `idx doctor`, or registry-backed
  workspace management something explicit to build on.

**Suggested scope**

- One additive DTO revision.
- Roughly 10-16 touched files across abstractions, service, CLI, and tests.
- 15-30 tests, including "live but loaded" daemon scenarios.

### 4.4 Add Lifecycle Hygiene: `idx gc`, `idx stats`, and Orphan Cleanup

Target-centric daemon identity is correct, but it naturally leaves behind state:
old daemon directories, orphaned target IDs, logs, and superseded DBs. The
workspace-target proposal already anticipated this by calling out a future
garbage-collection command.

**What to add**

- `idx gc` to remove orphaned daemon descriptors, stale state directories, and
  no-longer-referenced legacy repo-only entries.
- `idx stats` to summarize per-target disk usage, schema version, and last-used
  information before cleanup decisions are made.
- Optional `--dry-run` and `--json` modes so the command is usable by scripts
  and agents.
- Conservative deletion rules: never delete a state directory that still maps
  to a live daemon or an explicitly registered target.

**Why it matters**

- Directory mode increases the number of plausible long-lived targets.
- SQLite-based search is operationally friendlier when disk usage is visible
  and cleanup is first-class.
- It keeps the app-data footprint understandable instead of mysterious.

**Suggested scope**

- Roughly 8-12 files, mostly in CLI/service/discovery layers.
- No required schema change.
- 10-20 tests around live-daemon detection and dry-run reporting.

### 4.5 Strengthen Directory-Mode Safety and Trust-Boundary Guardrails

Git mode benefits from an implicit project boundary. Directory mode does not.
Once Indexed can follow arbitrary roots, the safety model needs to become more
explicit and slightly more opinionated.

**What to add**

- Root guardrails that refuse obviously dangerous or overly broad roots unless
  the user opts in explicitly, for example `C:\\`, `C:\\Windows`,
  `%USERPROFILE%`, `%USERPROFILE%\\.ssh`, or other clearly sensitive/system
  locations.
- A clear `--allow-sensitive-root` or `--allow-broad-root` override that makes
  the opt-in visible and auditable.
- Documentation that explains the local trust model plainly:
  localhost-only, unauthenticated search surface, any local process can query
  indexed content.
- Hard limits on request size and bounded search concurrency if any remaining
  unbounded paths still exist in the current service.

**Why it matters**

- The product's scope expanded from repositories to arbitrary local trees.
- The default safety posture should keep accidental misuse rarer than deliberate
  expert usage.
- This is especially important for agent-driven workflows where commands may be
  synthesized rather than hand-written.

**Suggested scope**

- Roughly 6-10 files across CLI, target validation, service hardening, docs,
  and tests.
- 10-20 tests plus a small number of integration checks.

### 4.6 Execute the Existing Size-Reduction Roadmap

Indexed already has a good size-reduction analysis and a safe near-term plan.
The improvement here is not to invent a new storage strategy. The improvement
is to carry that plan through and make size a measured, regression-tested part
of the product.

**What to add**

- The baseline/canary measurement harness described in
  [`./Indexed-Size-Reduction-SafeNearTerm-Plan.md`](./Indexed-Size-Reduction-SafeNearTerm-Plan.md).
- The near-term workstreams already identified there:
  - curated default excludes for size-inflating text artifacts;
  - background FTS merge/optimization;
  - contentless FTS with snippet rehydration from disk.
- Documented before/after measurements committed into `docs/`.

**Why it matters**

- Directory mode broadens the class of targets and makes index size more
  visible.
- Large indexes are acceptable when deliberate, but not when unexplained.
- The project already did the analytical work; finishing it is high-leverage.

**Suggested scope**

- Follow the existing plan rather than replacing it here.
- Treat measurement output as a required artifact, not an optional appendix.

### 4.7 Make Watcher and Reconciliation Behavior Root-Aware at Scale

The current global reconciliation behavior is correct, and the workspace-target
proposal explicitly allowed it as a first cut. The next improvement should be
to make that correctness cheaper and more diagnosable under high churn.

**What to add**

- Per-root fairness in the debouncing and scheduling pipeline so one noisy root
  cannot starve the others.
- Root-scoped reconciliation requests where a watcher overflow or root-local
  fault does not force a whole-target reconciliation unless necessary.
- Root-level backoff and fault recording in status output.
- Tests that pin logical-path reconciliation behavior for directory-set targets.

**Why it matters**

- Directory-set mode is now a real product feature, not a side case.
- Root-aware scheduling is the difference between "supports N roots" and
  "stays responsive when N roots are active."
- It reduces unnecessary work without weakening correctness.

**Suggested scope**

- Roughly 8-14 files in queueing, watcher, indexer, DTO, and test layers.
- 15-25 tests focused on fairness, overflow, and reconciliation semantics.

### 4.8 Clean Up the Client/Server Boundary

The code-and-architecture review was right to call out the current coupling:
the CLI still knows more about service internals than it should.

**What to add**

- A small shared protocol/discovery surface, either as a dedicated project or
  as a clearly bounded extension of `Indexed.Abstractions`.
- Versioned `daemon.json` parsing and validation owned by that shared layer.
- CLI adoption logic that depends on protocol types, not service
  implementation types.

**Why it matters**

- It makes daemon discovery easier to evolve safely.
- It simplifies packaging and publishing.
- It reduces the conceptual leak between "client contract" and
  "server implementation detail."

**Suggested scope**

- One small shared assembly or one bounded abstractions expansion.
- Roughly 6-10 touched files plus compatibility tests.

### 4.9 Port Indexed Beyond Windows

This is the broadest recommendation and not the first one I would do, but it is
worth naming explicitly because the product direction now justifies it.

**What to add**

- Multi-targeting beyond `net10.0-windows`, likely with OS-specific service and
  watcher shims.
- Cross-platform app-data, path-normalization, and file-system-comparison
  abstractions.
- Integration coverage on at least one Linux environment before claiming the
  port is real.

**Why it matters**

- Indexed is increasingly a general local indexing service, not merely a
  Windows helper for one repository.
- Directory mode and target-aware discovery are conceptually portable.
- A clean portability layer will also improve Windows-specific code quality by
  making platform assumptions explicit.

**Suggested scope**

- This touches most projects and should be treated as a broad refactoring plus
  validation effort, not as a small bolt-on.

## 5. Recommended Sequence

Recommended order by dependency and leverage:

1. **Prose indexing (`4.1`)**. It closes the largest documented product gap and
   makes the query surface internally consistent.
2. **Operational surface (`4.3`)**. Better status and admin responsiveness
   should land before more management commands depend on them.
3. **Lifecycle hygiene (`4.4`)**. Once status is richer, cleanup and storage
   commands become easier to trust.
4. **Target registry (`4.2`)** plus **client/server cleanup (`4.8`)**. These
   fit naturally together because both improve how clients address daemons.
5. **Watcher/root-scale hardening (`4.7`)**. Do this after richer status exists
   so the behavior is observable while it changes.
6. **Safety guardrails (`4.5`)**. These can ship earlier if needed, but they
   also benefit from the registry and status work.
7. **Size-reduction execution (`4.6`)**. Run it as a measured workstream rather
   than an opportunistic tweak stream.
8. **Cross-platform port (`4.9`)**. Best handled after the target, protocol,
   and status models have settled further.

## 6. Deliberately Deferred Ideas

These may become interesting later, but I would not prioritize them yet:

- Cross-daemon federated search across unrelated targets.
- Semantic code navigation, symbol graphs, or xref.
- Embedding/vector search.
- Branch/history indexing.
- Rich query-language work beyond what is needed to finish prose mode and keep
  code search trustworthy.

Each of those expands product scope materially. None of them is required to
make the current Indexed direction feel complete.

## 7. Recommendation

The strongest next move is to treat Indexed as a product entering its second
phase rather than as a prototype still proving its premise. The premise is
already proven: fast local search over a warm, continuously updated target works
for repositories and now works for non-git workspaces too. The right
improvements now are the ones that make that capability complete, operable,
repeatable, and safe.

If I had to pick the three highest-value improvements, I would pick:

1. complete prose indexing;
2. make status/root health first-class;
3. add a target registry plus lifecycle cleanup for recurring non-git
   workspaces.

Those three together would make Indexed feel much more like a mature local
search service and much less like a powerful engine that still expects an expert
operator standing next to it.
