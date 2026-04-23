# Indexed — Workspace Targets Proposal Review

- Created (UTC): 2026-04-23T17:55:50Z
- Repository HEAD: 0cc016f3b1fa814803542d144d89030107954fef
- Status: Review of [`Indexed-Workspace-Targets-Proposal.md`](../Indexed-Workspace-Targets-Proposal.md) at the same HEAD, with recommended amendments before the proposal is adopted as the implementation contract.
- Scope: proposal §1–§14 against current `src/Indexed` code and existing architecture docs. Focused on correctness, interface shape, schema evolution, identity, and user-facing surface.
- Prior reviews consulted:
  - [Indexed-Post-Fix-Architecture-Review__c51514c9d7f3.md](../Indexed-Post-Fix-Architecture-Review__c51514c9d7f3.md)
  - [Indexed-Code-and-Architecture-Review__c0883d924cbd.md](../Indexed-Code-and-Architecture-Review__c0883d924cbd.md)

## Executive summary

The proposal correctly identifies the seam to cut: the query engine, storage, and change pipeline are already target-neutral in all but name, and the real coupling is concentrated in `DaemonHost.StartAsync`, `RepoId.Compute`, `FullScanIndexer`, `IncrementalIndexer`, and `HeadPoller`. Its central recommendation — an `IIndexTarget` abstraction, a new `Indexed.Targets` project, and a root-aware schema v3 — is the right shape and the right blast radius.

The proposal is not yet implementation-ready in three areas: (1) the `IIndexTarget` interface conflates the `change tracker` façade with what is naturally three composable sources (`RepoWatcher` + `HeadPoller` + `ReconciliationScheduler`), and materializes enumeration eagerly in a way that will not survive a 100 K-file directory tree; (2) the identity story, the schema rebuild policy, and the legacy `repoId` compatibility rule are under-specified enough that a motivated reader can pick two mutually-incompatible interpretations; (3) two user-safety gaps — a missing default-exclude list for arbitrary directory trees, and a missing root-label stability rule — will land on the first user who points `--root` at a non-trivial tree. None of these are architectural problems; all are reachable by tightening the proposal before code lands.

This review is structured as blocking (**B**), major (**M**), minor (**N**), and polish (**P**) findings against the proposal, followed by one consolidated list of recommended amendments. The proposal's goals, non-goals, terminology, and high-level seam choice survive intact.

## What the proposal gets right

Worth preserving as-is:

- **Framing directory mode as complementary, not replacement.** §Summary and §12.1 are emphatic on this point. Current git-mode fidelity (authoritative file set, cheap branch-move detection via `diff-tree`) is a real asset; the proposal does not dilute it.
- **The seam choice.** §4.4 correctly observes that variability lives in "how files are discovered, named, and observed for change" and places `IIndexTarget` exactly there. The query engine (`CodeQueryPlanner`, `CodeQueryExecutor`, `RegexTrigrams`, `SqliteIndex` FTS5 path) and the repair-event protocol survive untouched, which matches reality: those layers already take paths and content, not git concepts.
- **Additive freshness evolution.** §8.2 keeps `IndexedHead` / `CurrentHead` as git-only compatibility aliases while introducing `IndexedRevisionToken` + `RevisionKind`. This is the correct way to evolve the `Freshness` DTO without breaking [`StatusResponse`](../../src/Indexed.Abstractions/StatusResponse.cs) shape for existing callers.
- **Surfacing the `--exclude-index` / `--no-default-excludes` identity leak.** §1.4 catches a real bug: today [`DaemonClient.CreateAsync`](../../src/Indexed.Cli/DaemonClient.cs) computes `repoId` purely from path + first-commit SHA, so two daemons with different index-shaping options share one `index.db`. Folding this into target identity is the right fix and is overdue regardless of the directory-mode work.
- **Explicit disjoint-roots requirement for directory-set mode.** §7.3 is right to demand this rather than try to union overlapping trees; otherwise watcher ownership and logical paths become ambiguous.

## Findings against the proposal

### Blocking (must be resolved before implementation begins)

#### B1. `IIndexTarget.EnumerateFiles` should not return a materialized list

**Proposal reference:** §4.2.

```csharp
IReadOnlyList<EnumeratedFile> EnumerateFiles(CancellationToken cancellationToken = default);
```

Git mode returns the ls-files union as an `IReadOnlyList<string>` today ([`GitRepository.EnumerateFiles`](../../src/Indexed.Git/GitRepository.cs)) because `git ls-files -z` already produces the full list in one subprocess; the memory profile is fine. A directory-tree target backed by `Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)` over a 100 K-file tree either (a) materializes a `List<EnumeratedFile>` of tens of megabytes before the first file is indexed, or (b) pays for one full recursive walk before `FullScanIndexer` sees any progress. Both defeat the current batch-by-200 pattern in [`FullScanIndexer.RunAsync`](../../src/Indexed.Core/FullScanIndexer.cs), which starts committing after the first batch completes.

**Amendment.** Change the enumeration contract to stream:

```csharp
IAsyncEnumerable<EnumeratedFile> EnumerateFilesAsync(CancellationToken cancellationToken = default);
```

Git targets can back this with `await foreach (var p in GitLsFiles(...)) yield return ...` over an array they already have. Directory targets back it with `Directory.EnumerateFiles` plus per-entry `FileInfo`. The initial-scan UX (progress reporting, cancellation latency) matches what the code already promises.

While we are at it: the `Total` count fed into `IndexProgress` today comes from `files.Count`, which the streaming form cannot provide upfront. Either drop `Total` from progress for directory targets, or expose a separate `EstimateFileCountAsync` that git can satisfy exactly and directory mode can approximate via a cheap directory-only prewalk. The proposal should pick one; the current text implies both, which is the worst option.

#### B2. The "change tracker" is three sources, not one

**Proposal reference:** §4.2 `ITargetChangeTracker`, §4.3 diagram.

Today, change detection is a trio:

- [`RepoWatcher`](../../src/Indexed.Core/RepoWatcher.cs) — FSW, target-neutral modulo its `.git` skip.
- [`HeadPoller`](../../src/Indexed.Core/HeadPoller.cs) — git-only.
- [`ReconciliationScheduler`](../../src/Indexed.Core/ReconciliationScheduler.cs) — target-neutral.

The proposal replaces these with one `ITargetChangeTracker` per target, and says "git targets implement the full interface" and "directory targets ... omit the HEAD-specific tracker." That blurs the ownership pattern that the Stage 4 plan deliberately established: the file watcher and the reconciliation scheduler are target-neutral components that get *composed with* a target-specific revision poller, not reimplemented per target.

**Amendment.** Keep the three sources separate. Only `HeadPoller` is target-specific; recast it as one member of a target-specific `IRevisionTracker` (optional — can be null for directory targets). `RepoWatcher` becomes `DirectoryWatcher` (already almost generic); it takes the list of watch roots from `IIndexTarget.Roots` and keeps its `.git` skip as a behaviour of the git adapter registering an extra root-local exclude rather than hard-coded in the watcher. `ReconciliationScheduler` stays unchanged — it is a timer that publishes `ReconciliationRequested`; it does not care about the target.

Net structural effect:

```text
DaemonHost
    +-- IIndexTarget               (enumeration + path mapping; owns per-kind semantics)
    +-- DirectoryWatcher(roots)    (target-neutral; one watcher per root internally)
    +-- IRevisionTracker?          (null in directory mode; HeadPoller in git mode)
    +-- ReconciliationScheduler    (target-neutral)
```

This preserves the Stage 4 separation of concerns, removes dead code paths in directory mode (no empty-method override of a HEAD tracker), and cleanly handles the fact that `IncrementalIndexer` already switches on `IndexEvent` kind rather than on target kind.

#### B3. Directory mode is missing a default exclude list

**Proposal reference:** §2.2 non-goals ("Fully general ignore-file semantics in the first cut. ... directory mode starts with explicit roots plus explicit exclude globs."), §7.2 directory-tree target.

[`ExcludeFilter.DefaultBinaryAdjacentGlobs`](../../src/Indexed.Core/ExcludeFilter.cs) targets lockfiles, minified bundles, source maps, and generated C# — excellent defaults for a git-tracked source tree where users have already opted out of vendored junk via `.gitignore`. Directory mode does not have a `.gitignore` safety net. A naïve user running `idx find foo --root C:\src\myproj` against a typical Windows project will happily index `node_modules\`, `.venv\`, `bin\`, `obj\`, `target\`, `__pycache__\`, `Thumbs.db`, `.DS_Store`, `.idea\`, `.vs\`, and any number of other build-output or editor directories. A slightly less naïve user running against `C:\src` will encounter `.git\` inside every checkout under that tree — the watcher will churn, the index will bloat, and the search experience will degrade silently.

**Amendment.** Add a `DefaultDirectoryModeExcludes` constant next to `DefaultBinaryAdjacentGlobs`, applied for *any* target whose kind is `DirectoryTree` or `DirectorySet`, on by default, skipped only via a new `--no-default-directory-excludes` flag. Include at minimum: `.git/**`, `.hg/**`, `.svn/**`, `.bzr/**`, `node_modules/**`, `.venv/**`, `venv/**`, `__pycache__/**`, `bin/**`, `obj/**`, `target/**` (Rust/Maven/Cargo), `.idea/**`, `.vs/**`, `.vscode/**`, `.gradle/**`, `build/**` (Gradle/Meson), `dist/**`, `out/**`, `.next/**`, `.nuxt/**`, `coverage/**`, `Thumbs.db`, `.DS_Store`, `$RECYCLE.BIN/**`, `System Volume Information/**`, `.tox/**`, `.pytest_cache/**`, `.mypy_cache/**`. This is opinionated and will need evolution; that is fine. The alternative (no defaults) is a landmine on first run.

Also worth stating: in git mode, keep the current behavior — the existing `.gitignore` + `.git/info/exclude` + `git check-ignore` pipeline is the safety net, and the directory-mode defaults should not stack on top of it by mistake. The two lists are conceptually distinct: `DefaultBinaryAdjacentGlobs` is about trigram-index bloat; `DefaultDirectoryModeExcludes` is about "do not wander into /Windows". Keep them separately toggleable.

#### B4. Root labels must be user-supplied for multi-root, not derived from filesystem state

**Proposal reference:** §6.4.

The proposal proposes deriving `root_name` from the directory basename and appending a short hash suffix on collision (`src~a1b2`). This is user-visible — it becomes part of every `logical_path` — and therefore part of every search result and every `--glob` that an agent writes. Two problems:

- **Instability under set changes.** Adding a new root with a colliding basename can retroactively change the suffix rule. If the first target used `sdk/` and `tools/` as two roots (both unique basenames), and the user later adds a third root whose basename is also `sdk`, the collision rule has to either change the existing label (breaking stored logical paths) or silently add a non-matching suffix (inconsistent). Neither is acceptable.
- **Instability under path moves.** If the user moves `C:\src\proj\sdk` to `D:\work\sdk`, the basename-derived label is unchanged, but the suffix (derived from the canonical absolute path) changes. Every stored logical path moves. Agents that cached a path from a prior query break.

**Amendment.** For multi-root targets, require labels explicitly:

```bash
idx find foo --root sdk=C:\src\proj\sdk --root docs=C:\src\proj\docs
```

Rules:

- Single-root targets: no label syntax, logical path equals relative path as today.
- Multi-root targets: labels are mandatory and must be unique within the target.
- If the user provides a bare path in multi-root mode, derive from basename and require that the derivation yields a unique label; fail the command with a "use `LABEL=PATH` to disambiguate" error otherwise.
- Labels participate in `TargetSpec`, so reordering roots with the same labels yields the same `targetId`; relabeling or remapping forces a rebuild (as it should).

This is more friction at first use but eliminates a whole class of "why did my query stop matching?" bugs.

#### B5. Target identity must not hash raw JSON

**Proposal reference:** §5.1.

```text
targetId = SHA1(canonical-target-spec-json)[0:12]
```

`System.Text.Json` canonicalization is historically load-bearing and has drifted on minor version upgrades: key ordering in `JsonObject`, trailing-whitespace handling, numeric formatting (`1.0` vs `1`), `null` versus omitted properties with `DefaultIgnoreCondition`, escape-sequence choices for non-ASCII characters in paths. A hash-stable representation should not depend on any serializer's exact byte-level behavior.

**Amendment.** Specify a hand-rolled canonical byte stream built directly from the spec fields in a fixed order, using NUL-byte delimiters (the same pattern [`RepoId.Compute`](../../src/Indexed.Service/RepoId.cs) already uses). Something like:

```text
canonical = "indexed-target-v1" "\0"
            targetKindName "\0"
            roots.Length.ToString(InvariantCulture) "\0"
            for each root in sortedRoots:
                root.Label "\0" Path.GetFullPath(root.Path) "\0"
            useDefaultIndexExcludes ? "1" : "0" "\0"
            useDefaultDirectoryExcludes ? "1" : "0" "\0"
            indexExcludeGlobs.Count.ToString(InvariantCulture) "\0"
            for each glob in sortedExcludeGlobs:
                glob "\0"
targetId = SHA1(canonical)[0..6].ToHex()
```

Root-path normalization is Windows-case-insensitive (as `FileContentProvider` already is) to match NTFS. The `"indexed-target-v1"` prefix reserves room for future format revisions without re-hashing existing targets silently.

### Major (should be resolved or explicitly deferred)

#### M1. Legacy `repoId` compatibility rule needs a precise predicate

**Proposal reference:** §5.2.

The proposal says: preserve the existing `repoId` formula for "the legacy default git case: one git root; current repo-based identity; no future target-only features." That language is soft enough to admit two interpretations.

**Amendment.** Make the predicate explicit and closed:

> The legacy `repoId` formula applies exactly when all of the following hold:
>
> - `TargetKind == GitRepository`
> - exactly one root, which equals the discovered git work-tree root
> - `UseDefaultIndexExcludes == true` and `IndexExcludeGlobs` is null or empty
> - `UseDefaultDirectoryExcludes == false` (directory defaults do not apply to git mode)
>
> Any deviation produces a spec-based `targetId`. An existing `%LOCALAPPDATA%\Indexed\<repoId>\` directory that no longer matches its current `TargetSpec` is orphaned; a future `idx gc` command may prune orphans, but the daemon never auto-deletes them during normal operation.

Pinning the predicate now avoids an eternal stream of "but what about this edge case" and makes the identity mapping testable.

#### M2. Schema v3 should state its rebuild scope precisely

**Proposal reference:** §6.5.

"This is a schema bump. Rebuild is acceptable and consistent with current Indexed policy." True as stated, but the proposal is silent on what happens to *every currently warm `index.db` in the wild* — which includes the maintainer's dev machines — on first upgrade. `SqliteSchema.Version` goes from 2 → 3; [`SqliteIndex.OpenOrCreate`](../../src/Indexed.Core/SqliteIndex.cs) deletes and recreates; all pre-existing indexes rebuild from scratch. For this repo that is ~60 s per target per dev machine. Fine, but worth saying out loud so nobody is surprised.

**Amendment.** Add a one-sentence note: "Schema v3 rollout triggers a one-time rebuild of every existing `index.db`; no in-place migration is supported; release notes should call this out."

Also: the DDL in §6.2 declares `logical_path TEXT UNIQUE NOT NULL` and a separate `CREATE INDEX files_logical_path ON files(logical_path)`. `UNIQUE` already creates an implicit index; the explicit `CREATE INDEX` is redundant. Drop the explicit index.

#### M3. Binary-heuristic factoring is incomplete

**Proposal reference:** §7.1, §7.2.

Today [`GitRepository.IsLikelyBinary`](../../src/Indexed.Git/GitRepository.cs) does three things: (1) size cap against `MaxIndexableFileBytes`, (2) first-8-KiB NUL-byte scan, (3) exists/readable sanity. All three are target-neutral; only [`GitRepository.GetBinaryAttrPaths`](../../src/Indexed.Git/GitRepository.cs) (`.gitattributes binary` override) is git-specific. Directory mode needs (1)–(3) and should not get (a reimplementation of) (4).

**Amendment.** Pull (1)–(3) into a target-neutral helper in `Indexed.Core` (call it `BinaryHeuristic.IsLikelyBinary(absolutePath, maxBytes)`) and have both `GitRepository` and the new directory adapter consume it. Keep `.gitattributes` access in `Indexed.Git` only. This also eliminates the current quirk that `IsLikelyBinary` mixes absolute-path resolution with repo-relative path input.

#### M4. `HeadMoved` is shared-hierarchy but git-only in semantics — document the invariant

**Proposal reference:** §4.2, §7.2, §8.

[`IndexEvent`](../../src/Indexed.Core/IndexEvent.cs) is the event base class and `HeadMoved` is one of its cases. After the refactor, the hierarchy stays in `Indexed.Core` and is shared by all targets, but only the git adapter ever emits `HeadMoved`. That is fine and cheap, but the proposal should state the invariant explicitly so it does not get "fixed" later by someone who mistakes dead code for a bug:

> Invariant: `HeadMoved` is emitted only by a git target's revision tracker. `IncrementalIndexer`'s `case HeadMoved` branch is intentionally unreachable in directory mode. The event sits in the shared `IndexEvent` hierarchy so the queue and debouncer remain target-agnostic.

No code change; one paragraph of commentary.

#### M5. Overlap-vs-nesting-vs-case rules for multi-root need to be stated

**Proposal reference:** §7.3.

"Overlapping roots are rejected up front." Fine for strict overlap, but the real failure modes are subtler:

- **Nested roots.** `C:\src\proj` and `C:\src\proj\docs` — the second is a subtree of the first. Must be rejected: otherwise a change in `docs\foo.md` fires two watcher events (one per root) and deduplication by logical path is not enough because the logical paths differ between roots.
- **Case-variant roots on Windows.** `C:\src\proj` and `c:\src\PROJ` — same physical directory, different strings. Must be rejected; path normalization during `TargetSpec` canonicalization should fold case on Windows.
- **Roots reached by different absolute forms.** `C:\src\proj` and `\\?\C:\src\proj`, or drive letter vs UNC (`\\mymachine\C$\src\proj`). Detection requires comparing `new DirectoryInfo(path).FullName` or `File.GetFileSystemEntries(...)` under normalization; at minimum, reject when `Path.GetFullPath` outputs collide after case-fold.
- **Symlink/junction-reached roots.** Out of scope for first cut (§7.2 already says "do not recurse through directory symlinks"). State the rule at the root-validation layer too: if a root itself is a symlink/junction, canonicalize it and use the target; if the target is inside another root's tree, reject as nesting.

**Amendment.** Dedicated subsection "§7.4 Root validation rules" covering nesting, case-folding, long-path / UNC canonicalization, and symlink target canonicalization. This is boring plumbing but the first user who hits it will not find it boring.

#### M6. Reconciliation must be root-aware for directory-set targets

**Proposal reference:** §7.3, §11.2.

Today, [`IncrementalIndexer.ExpandReconciliationAsync`](../../src/Indexed.Core/IncrementalIndexer.cs) calls `_repo.EnumerateFiles()` once and diffs against `_index.GetAllPathsWithShaAsync()` in one set-comparison. For a directory-set target, the equivalent flow must:

1. iterate `target.Roots`, enumerate each, build a union keyed by **logical path** (not relative path, which collides between roots);
2. map each enumerated absolute path through `target.TryMapAbsolutePath(...)` to obtain its logical path before comparison;
3. diff the union against `_index.GetAllPathsWithShaAsync()` (which already returns logical paths after the schema v3 change);
4. continue with stat-drift detection per root, again using logical paths as keys.

The proposal says "one reconciliation pass that unions the per-root file sets" but does not flag this absolute-to-logical mapping. It is easy to write a reconciliation that works for single-root directory mode and silently deletes every file from a multi-root index because the relative paths do not match the indexed logical paths.

**Amendment.** Add one paragraph to §7.3 stating: "Multi-root reconciliation diffs by logical path, not relative path. The enumerator yields `(absolutePath, rootId)`; `target.TryMapAbsolutePath` produces the logical path, which is the comparison key against `_index.GetAllPathsWithShaAsync()`." Then either add a reconciliation-specific test for this (preferred) or flag the risk in §11 explicitly.

#### M7. Mention `LastReconciliationAt` persistence

**Proposal reference:** §8.2.

Adding `LastReconciliationAt` to the `Freshness` DTO requires the incremental indexer to persist the timestamp after each reconciliation pass. Today the indexer only writes `MetaKey_IndexedHead` and `MetaKey_LastFullScanAt`. A new meta key (`last_reconciliation_at`) plus a one-line update inside `ExpandReconciliationAsync` lands this. Trivial, but the proposal is silent; worth a bullet in §10 workstream E to avoid someone adding the DTO field without the write path.

#### M8. Watcher-startup ordering becomes a bigger deal in directory mode

**Proposal reference:** §9 (implicit), §11.2.

Current startup in [`DaemonHost.StartAsync`](../../src/Indexed.Service/DaemonHost.cs): open index → full scan (if empty) → start watcher/poller/scheduler. The gap between "full scan reads a file" and "watcher is armed" is short in git mode because `git ls-files` is fast. In directory mode, a full scan over a 100 K-file tree is 30+ s of wall time; any change made during that window is missed by both the scan (if it occurred after the file was read) and the watcher (not yet listening).

**Amendment.** State the ordering rule explicitly:

> For directory targets, the `DirectoryWatcher` is armed *before* the initial enumeration starts. Events that arrive during the initial scan accumulate in the debouncing queue and are processed by the incremental indexer after the scan's writer scope closes. An immediate `ReconciliationRequested` is enqueued at the end of the initial scan to catch anything the arming race missed.

Git mode can preserve today's ordering since the window is short and `HeadPoller` / FSW together close it. The rule above is a directory-mode correctness requirement.

### Minor (should be resolved but not blocking)

#### N1. `--repo-root` and `--root` mutual exclusion

**Proposal reference:** §9.2.

"Selection rules: (1) If one or more `--root` flags are present, use directory-based target resolution. (3) If no `--root` flags are present, preserve today's git-repo behavior."

Silent about: what if both `--repo-root` and `--root` are supplied? Parse error? `--repo-root` wins for backwards compat? `--root` wins because it is newer?

**Amendment.** Specify: mutually exclusive; passing both is a parse error with a clear diagnostic. `--repo-root` remains a git-only alias and becomes a no-op on targets selected via `--root`.

#### N2. `StatusResponse` DTO evolution should preserve field presence

**Proposal reference:** §9.3.

"Keep `RepoRoot` / `RepoId` only when `TargetKind == git-repo`." As a DTO behavior this means either (a) omit the fields from the JSON for non-git targets, or (b) emit them as `null`. Agents that dispatch by presence vs null are rare but real; the proposal should pick one.

**Amendment.** State: both fields remain in the DTO, serialized as JSON `null` for non-git targets (same policy as `Freshness.Note` today). Omission-via-`DefaultIgnoreCondition` is avoided because round-trip symmetry matters for the tests.

#### N3. `idx gc` / target registry UX

**Proposal reference:** §13.1 open question.

The proposal defers a persisted named-target registry. Fine, but without *any* registry the user has no way to list running daemons short of scanning `%LOCALAPPDATA%\Indexed\` manually. That is acceptable today because there is one repo in each checkout; it becomes painful the moment a user has three directory-set daemons running, each with a different root combination.

**Amendment.** Add a minimal `idx daemons` verb that lists `%LOCALAPPDATA%\Indexed\<targetId>\daemon.json` entries with their roots, started-at, and pid. No registry writes; purely a read-side convenience that makes multi-target life tolerable. The real registry can follow later.

#### N4. FSW buffer-overflow recovery is implicitly per-target, should be per-root

**Proposal reference:** §7.3, §12.3.

Today [`RepoWatcher.OnError`](../../src/Indexed.Core/RepoWatcher.cs) enqueues a single `ReconciliationRequested` on FSW error — one watcher, one event. Multi-root directory-set mode has N watchers, and an error on one watcher should not trigger a full re-enumeration across every root. `ReconciliationRequested` is currently scopeless.

**Amendment.** Leave `ReconciliationRequested` global in the first cut (behavior is correct, just wasteful) but add a comment that a future `ReconciliationRequested(rootId?)` variant can narrow the scope without breaking the contract. Flag this in §12.3 as an acceptable performance-only trade-off.

#### N5. Security section needs a directory-mode paragraph

**Proposal reference:** (none — proposal does not touch security).

Today the service's security model (in `Indexed-Architecture.md §12`) restates: localhost binding, shutdown-token auth, path containment, read-only repo access. Directory mode inherits all of these, but the "path containment" line currently reads "only reads files under the repo root", which is no longer a complete statement. The proposal should own its security surface in one paragraph rather than implicitly.

**Amendment.** Add §15 "Security implications":

- Localhost-only binding unchanged.
- Path containment applies per root: `FileContentProvider` (or its multi-root successor) verifies every resolved path stays under the root it belongs to. The existing `FileReadStatus.OutOfRoot` logic in [`FileContentProvider`](../../src/Indexed.Core/FileContentProvider.cs) is the model.
- The daemon only reads; it never writes outside its own state directory.
- Users pointing `--root` at directories containing secrets (`.env`, private keys, password stores) accept that any local process with loopback-HTTP access can search them, because the daemon does not authenticate `/search`. Add a documentation warning; consider an opt-in `--deny-sensitive-roots` heuristic that refuses obvious ones (`%USERPROFILE%\.ssh`, `%USERPROFILE%`, `C:\`, `C:\Users`, `C:\Windows`, `/etc`).

### Polish (tighten wording or tiny DDL fixes)

- **P1.** §3 terminology: "canonical target spec" vs "target spec" — use one consistently.
- **P2.** §4.2 — define `EnumeratedFile`, `TargetRoot`, and `LogicalPath` at least as fields: the code reader should not have to guess the shape from context.
- **P3.** §6.2 DDL — drop the redundant `files_logical_path` index (already implied by `UNIQUE`).
- **P4.** §10 workstream estimates — file counts are fine, but add order-of-magnitude LOC figures (e.g. "~400 LOC added, ~50 LOC modified") so the sizing is comparable to prior review deltas.
- **P5.** §12.2 — "Once a logical path format ships, agents and scripts will depend on it." Replace with a concrete note that the root-label rule (after amendment B4) is part of the stable public contract and should never be changed silently.
- **P6.** §13 open questions — number 4 ("Should target identity always be fully spec-derived...") is resolved by the compatibility predicate in M1 above; consider closing it in the amended proposal.

## Recommended consolidated amendments

Applying the findings above, the proposal should be amended in roughly this shape. (The count is LOC-level estimates of proposal-document changes; the actual implementation plan in §10 is separately sized.)

| § | Change | Rough size |
|---|--------|-----------|
| §4.2 | Replace `EnumerateFiles` with `EnumerateFilesAsync`; define `EnumeratedFile`, `TargetRoot`, `LogicalPath`; drop `ITargetChangeTracker` in favor of composable `DirectoryWatcher` + optional `IRevisionTracker` + shared `ReconciliationScheduler` | ~40 LOC |
| §5.1 | Replace JSON hashing with explicit canonical byte stream; add `"indexed-target-v1"` prefix | ~15 LOC |
| §5.2 | Close the legacy-`repoId` predicate (single git root, default excludes, no directory defaults) | ~10 LOC |
| §6.2 | Drop redundant explicit `files_logical_path` index | -1 LOC |
| §6.4 | Require explicit labels for multi-root targets (`LABEL=PATH` syntax); fail fast on collisions; remove automatic hash-suffix fallback | ~20 LOC |
| §6.5 | Note one-time rebuild of every existing `index.db` on upgrade | ~3 LOC |
| §7.1–§7.2 | Factor binary heuristic into `BinaryHeuristic` in `Indexed.Core`; git adapter retains `.gitattributes` access | ~8 LOC |
| §7.4 (new) | Root-validation rules: nesting, case-folding, long-path/UNC, symlinks | ~30 LOC |
| §7.3 | Clarify reconciliation diff uses logical paths for multi-root; flag startup-ordering rule for directory mode | ~15 LOC |
| §8.2 | Persist `last_reconciliation_at` in `meta`; note the one new meta key | ~5 LOC |
| §9.2 | `--repo-root` + `--root` mutually exclusive | ~3 LOC |
| §9.3 | `RepoRoot` / `RepoId` remain in DTO, serialized as null for non-git targets | ~3 LOC |
| §10.E | Add `DefaultDirectoryModeExcludes` constant + flag (`--no-default-directory-excludes`); add `LastReconciliationAt` plumbing; add `idx daemons` verb | ~20 LOC |
| §12.3 | Note `ReconciliationRequested(rootId?)` as future performance work | ~5 LOC |
| §13 | Close open question 4; recast 1 as "short-term: `idx daemons` only, long-term: named-target registry" | ~5 LOC |
| §15 (new) | Security model with directory-mode path-containment + sensitive-roots warning | ~20 LOC |
| §M4 | One-paragraph invariant: `HeadMoved` is git-only in emission, target-neutral in the event hierarchy | ~5 LOC |

Total proposal amendment: ~200 LOC of document change; no finding requires restructuring the core architecture.

## Risk items the proposal should capture before merge

Separate from the amendments, four items deserve explicit risk entries (either in §12 or a new §16) so they are not rediscovered at implementation time:

1. **Initial-scan performance envelope for directory trees.** Commit to a target: for a representative 100 K-file tree on SSD, cold full scan completes in ≤60 s, and the watcher-armed-before-scan rule keeps the initial-coverage gap at zero. Without this, the proposal's §13 performance targets (inherited implicitly from `Indexed-Architecture.md §13`) will silently drift for directory targets.

2. **Watcher pressure at N roots.** The current single-writer model handles N FSWs correctly, but the effective debouncing window becomes more aggressive at high N (more events per window → more batches hitting the 200-event cap). Commit to a per-root fairness rule: batches never starve one root for another. Simplest policy: round-robin drain of per-root pending events, preserving arrival order within each root.

3. **Per-root health reporting.** `/status` currently reports one freshness block. Multi-root mode should additionally surface per-root degradation signals (watcher faulted, root unreadable, stat-drift count) as a `Roots: [{ name, state, pendingCount, lastError }]` array. Not urgent; flagging it now avoids a second DTO revision later.

4. **Non-git-repo startup precondition.** Today `DaemonHost.StartAsync` bails if `GitRepository.Open` fails. After the refactor, directory-mode startup must succeed *in the absence of git.exe entirely* (no subprocess launched, no PATH probe). Worth an integration test: CI matrix where `git.exe` is not on PATH, running a directory-mode scenario end-to-end. Otherwise a subtle regression can slip in where some innocuous call-site reaches into `Indexed.Git` and reintroduces the dependency.

## Recommendation

Adopt the proposal's core architecture — `IIndexTarget`, `Indexed.Targets`, spec-derived identity, root-aware schema v3, additive freshness evolution — with the amendments above applied. The blocking findings (B1–B5) are the only changes that affect the public interface shape; the rest tighten undef­ined corners and close safety gaps. After these amendments the proposal is implementation-ready, and the work breakdown in §10 can proceed unchanged in sequencing, with minor LOC inflation absorbing the extra plumbing described here.

Nothing in this review recommends reducing the scope, slowing the roll-out, or re-examining the seam choice. The proposal is a good extension of the current architecture; it deserves to land with the corners filed off.
