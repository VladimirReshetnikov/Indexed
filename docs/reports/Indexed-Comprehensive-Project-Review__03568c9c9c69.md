# Indexed — Comprehensive Project Review

- Created (UTC): 2026-07-25T01:31:45Z
- Repository HEAD: a5c9f220e2f58e0ec599fa66e29c6859372ba006
- Scope: full-project review of the standalone Indexed repository — flaws and shortcomings, correctness risks, performance (indexing speed, search speed, index size), robustness, security posture, usability, tests, documentation, and packaging.
- Method: parallel deep reads of every production project (write side of `Indexed.Core`; query side of `Indexed.Core` including `RegexTrigrams`; `Indexed.Service` + `Indexed.Cli`; `Indexed.Extractors` + `Indexed.Targets` + `Indexed.Git`), plus a tests/docs/build sweep, synthesized against the prior review record in `docs/`. Every High finding was independently re-verified against the code at HEAD before inclusion; findings marked **(verified)** were confirmed line-by-line, and the Release-build failure was reproduced.

---

## 1. Context and prior review record

Indexed has an unusually strong review history. Five prior review documents
(`Indexed-Code-Review__4f8a1c3b7e02`, `Indexed-PR1-3-Code-Review__7c41a290d68f`,
`Indexed-Code-and-Architecture-Review__c0883d924cbd`,
`Indexed-Code-and-Architecture-Review__468c8153b543`,
`Indexed-Post-Fix-Architecture-Review__c51514c9d7f3`) produced ~80 findings, and
the post-fix review verified that **every prior critical and high finding was
closed** — writer-connection races, port-binding TOCTOU, path traversal,
premature `daemon.json` deletion, HeadPoller canary, DTO contract drift, and
more. That is rare and commendable. The themes those reviews left open —
**observability/diagnostics**, **size-reduction execution**, and
**cross-platform scoping** — remain open today and reappear below.

Real-world data points that ground this review:

- The ripgrep/ugrep comparison (`docs/reports/Indexed-Ripgrep-Performance-Comparison__61037266f366.md`)
  measured: 3–10× wins over scanners on broad corpus queries; a **7.3 GB index
  for an 8 GB corpus**; **~1.2 s median `idx status` wall clock** (vs 467 ms for
  `rg --version`) dominated by CLI startup; and ~10 s daemon-side elapsed for
  large-cap result materialization.
- The local state directory on this machine currently holds **16 target
  directories totaling ~2.5 GB**, several with no `daemon.json` (orphaned
  state), and no tooling exists to list, size, or clean them.

This review found that the prior rounds thoroughly hardened the daemon/HTTP
surface but never went deep on the **query planner's soundness invariant** or
the **extractor/target-identity layers** — which is exactly where the worst
remaining bugs live.

## 2. Executive summary

The architecture is sound and the code quality is high: the candidate-oracle +
live-disk-verification design is correct, FTS5 escaping is injection-safe,
cancellation and nullable discipline are excellent, freshness semantics are
truthful, and the crash-safety story (WAL, abandoned-mutex recovery,
corruption-at-open rebuild) is real. Prior review findings were demonstrably
fixed. The remaining problems cluster into four groups:

1. **Silent false negatives in search** — the one failure class the project's
   own invariant ("accept slower, never wrong") forbids. Unsupported regex
   escapes are analyzed as literal characters; non-BMP patterns produce
   impossible trigrams; UTF-16 files are never indexed at all; `--glob "*.cs"`
   matches only the repo root; F#'s `(*)` operator swallows files into prose
   spans. None of these announce themselves — the search just quietly returns
   nothing.
2. **A broken release pipeline** — `dotnet publish -c Release` (the documented
   install path) fails today on a NuGet vulnerability audit error (NU1903), and
   there is no CI to have caught it.
3. **Availability and convergence gaps under adversity** — one poisoned file
   permanently discards whole index batches; pooled readers share a SQLite
   cache with the writer and serialize behind write batches; an interrupted
   initial scan leaves a permanently incomplete index that directory targets
   report as fresh; the CLI can orphan a live daemon.
4. **An observability black hole** — the daemon logs only to a console that
   the launcher discards, while the docs promise rotating log files that no
   code writes.

Severity totals across the review: **11 High, 30 Medium, ~35 Low** (deduplicated;
several findings appear in one section but affect multiple concerns).

Top-priority actions (detailed in §13): fix the Release build; make the regex
trigram analyzer conservative for anything it does not understand; isolate
per-file indexing failures; switch reader connections to private cache; add
file logging; add CI and property-based trigram tests.

## 3. Correctness flaws and shortcomings

### 3.1 Query planner soundness (silent false negatives)

The planner's contract (`RegexTrigrams/TrigramAnalyzer.cs:27-30`) is that
imprecision must only ever widen the candidate set. Two High bugs violate it:

- **C1 (High, verified). Unsupported regex escapes are parsed as literal
  characters, over-constraining the trigram filter.**
  `RegexParser.cs:384-385` (`ParseEscape`, `default: return new LiteralNode(c.ToString())`)
  and `RegexParser.cs:348-349` (`ReadEscapeChar` default). `\u0041` becomes the
  literal text `u0041`, which `CompactLiterals` fuses into trigrams
  `u00/004/041` — ANDed into the MATCH expression even though the regex matches
  `A`. Same class: `\cX`, `\a`, `\e`, `\G`, octal escapes, and backreferences
  `\1`/`\k<name>` (the doc comment in `RegexAst.cs:12-16` claims backrefs become
  `OpaqueNode`, but no digit/`k` case exists). Files containing true matches are
  pruned before verification ever runs. **Fix:** whitelist the escapes the
  analyzer genuinely understands; return `OpaqueNode` (or the correctly decoded
  character for `\uXXXX`/`\cX`) for everything else, in both methods.
- **C2 (High, verified). Trigram windows are computed over UTF-16 code units;
  FTS5's trigram tokenizer works on codepoints.** `TrigramExpr.cs:193-201`
  (`WindowsOf` uses `Substring(i, 3)`). A literal containing a non-BMP
  character (emoji, astral CJK, mathematical alphanumerics) yields a window
  containing a lone surrogate, which becomes U+FFFD in UTF-8 — a trigram that
  cannot exist in the index. Because windows are ANDed, the query returns zero
  results; verification never sees the file. **Fix:** window by codepoint, or
  (better, see S3 in §6) emit literals as a single quoted FTS5 phrase and let
  the tokenizer window them server-side.
- **C3 (Medium). .NET full case folding disagrees with SQLite's simple
  folding.** `TrigramExpr.cs:198`, `TrigramAnalyzer.cs:99` pre-lowercase with
  `ToLowerInvariant`; U+0130 (`İ`) maps to `i` + combining dot in .NET but is
  left intact by FTS5's per-codepoint folding, so the emitted trigrams cannot
  match. Pre-lowercasing the MATCH phrase text is unnecessary — the tokenizer
  folds at query time. **Fix:** stop pre-lowercasing phrase text (keep it only
  for internal dedup keys).

### 3.2 Indexing pipeline correctness

- **C4 (High, verified). One poisoned file permanently discards entire
  incremental batches.** `IncrementalIndexer.cs:409-447`: the per-file
  try/catch covers only `IOException`/`UnauthorizedAccessException` around
  stat/read. Exceptions from `TextDecoder.Decode`, `ExtractorRegistry.Extract`
  (Roslyn or the regex extractors, which set no `matchTimeout`), or any
  `SqliteException` inside `UpsertFile`/`ReplaceProseSpans` hit the outer catch
  at `:442-447`, roll back the whole batch, and the worker drops the events
  (`:206-209`). The failure is self-perpetuating: the next reconciliation
  re-discovers the same cohort, batches it with the same poison file, and fails
  again forever — the index never converges and the only signal is a log line
  (which is itself discarded; see O7). The same gap aborts the entire initial
  scan in `FullScanIndexer.cs:261-276`. **Fix:** wrap decode/extract/upsert
  per-file, record an `indexing_error` skip row, and continue; keep whole-batch
  rollback only for transaction-level failures. Add a regex `matchTimeout` to
  the extractors.
- **C5 (Medium). UTF-16/UTF-32 files are never indexed.** The NUL-byte binary
  probe (`BinaryFileClassifier.cs:60-74`) rejects any UTF-16 file (every other
  byte is 0x00) before `TextDecoder`'s BOM-aware decode ever runs — the UTF-16
  support in `TextDecoder.cs:36-69` is dead code on the indexing path, and both
  the heuristic and the decode are documented in the architecture doc without
  noting they contradict. **Fix:** sniff UTF-16/UTF-32 BOMs in `Classify`
  before the NUL probe.
- **C6 (Medium). Debounced events can be stranded until an unrelated event
  arrives.** `DebouncingEventQueue.cs:113-160`, `:269-299`: when `Flush`
  leaves residue (paths younger than the debounce while others were ripe, or
  `_maxBatchSize` overflow), the next `DequeueAsync` blocks unconditionally on
  `ReadAsync` — leftovers wait for the next new event, worst case the 5-minute
  reconciliation tick. Freshness stays truthful (`PendingCount > 0`) but a
  saved file can be unsearchable for minutes. **Fix:** bounded wait when
  pending state is non-empty.
- **C7 (Medium). No reconciliation on daemon startup.** The scheduler's first
  tick fires after the full 5-minute interval (`ReconciliationScheduler.cs:50`)
  and `DaemonHost.cs:199-224` enqueues nothing when the DB is non-empty, so
  working-tree edits made while the daemon was down are invisible **and
  reported fresh** (`isStale=false`, `PendingCount=0`) for up to 5 minutes —
  despite the scheduler's own doc-comment claiming it covers this case.
  **Fix:** enqueue one `ReconciliationRequested` right after
  `IncrementalIndexer.Start()` when the index is non-empty.
- **C8 (Medium). An interrupted initial scan leaves a permanently incomplete
  index that directory targets report as fresh.** `DaemonHost.cs:221` runs the
  full scan only when `GetFileCount() == 0`, but `FullScanIndexer` commits in
  batches of 200 files / 250 ms — a crash or cancellation mid-scan leaves a
  partial DB the next start treats as complete. Git targets at least force
  `isStale=true` via the null revision token; directory/directory-set targets
  report `isStale=false` over a known-incomplete index, and manual-mode targets
  never heal without an explicit `idx rescan`. **Fix:** persist a
  `scan-complete` meta marker; on startup without it, enqueue reconciliation
  and report stale until it finishes.
- **C9 (Medium). Idle-exit can cancel the daemon's own initial scan.**
  The idle timer is poked only by requests and `BatchCommitted`
  (`DaemonHost.cs:161-165, 268`); `FullScanIndexer` never pokes it, so a scan
  longer than the idle timeout is cancelled by `RequestShutdown` — producing
  exactly the C8 state. **Fix:** poke (or suppress) the idle timer while the
  initial scan is in progress.

### 3.3 Extractors

- **C10 (High, verified). F#'s standard `(*)` multiplication-operator syntax
  is treated as a block-comment opener and swallows the rest of the file as
  prose.** `RegexCommentExtractors.cs:235-239` + `ScanBlockComments` at
  `:58-107`: the scanner searches for `*)` starting at `bestIndex + 2`,
  skipping the `*)` inside the `(*)` token itself; with no later `*)`,
  everything to EOF becomes one giant `BlockComment` span and every line lands
  in `blockedLines`. Real F# (`List.fold (*) 1 xs`) triggers this. F# block
  comments also nest, which the first-match scan truncates. **Fix:**
  special-case `(*` immediately followed by `)`; implement depth counting for
  F# (and Rust/Swift, whose block comments also nest).
- **C11 (Medium). Regex extractors match comment markers inside string
  literals.** `RegexCommentExtractors.cs:135-146`, `:63-74`: `"https://…"`
  produces a phantom line-comment span; `#` inside Python/YAML string values
  and `--` inside SQL literals do the same; a `/*` inside a string starts a
  phantom block comment that poisons `blockedLines` until a random `*/`. This
  pollutes `--mode prose`/`auto`. **Fix:** minimal per-line quote tracking;
  cheaply reject `//` preceded by `:` for the URL case; document the residual
  heuristic.
- **C12 (Low). Roslyn extraction recall gaps.** Comments inside inactive
  `#if` blocks (`DisabledTextTrivia`), `#region`/`#warning`/`#error` text, and
  trailing `//` comments sharing a line with a doc comment are dropped;
  `XmlTagRegex` strips literal unescaped generics like `<int>` from doc text
  (`RoslynCSharpExtractor.cs:25-27, 43-95`).
- **C13 (Low). F# `///` doc comments are indexed as `line-comment`, not
  `xml-doc`** — `--kind xml-doc` is effectively C#-only. A `DocLinePrefix`
  hook in `RegexCommentExtractorBase` would fix F# and serve Rust `///`/`//!`.

### 3.4 Target identity and git

- **C14 (High, verified). Root identity never canonicalizes symlinks,
  junctions, UNC/`\\?\` spellings, or 8.3 short names.**
  `TargetPathUtilities.cs:14-29` and `TargetId.cs:53-59` use only
  `Path.GetFullPath` + uppercase, though the workspace-targets proposal §7.4
  explicitly requires canonicalization of link targets and UNC/long-path
  spellings. The same directory reached via `C:\src\proj`, a junction,
  `\\?\C:\src\proj`, or a short name mints distinct target ids → duplicate
  daemons and duplicate multi-hundred-MB indexes; `EnsureDisjointRoots` also
  misses aliasing/nesting through junctions. **Fix:** resolve final paths
  (`Directory.ResolveLinkTarget`/`GetFinalPathNameByHandle`, stripping `\\?\`)
  in `NormalizeDirectoryPath`/`NormalizeRootForIdentity` before hashing and
  overlap checks.
- **C15 (Medium). `LegacyRepoId` does not case-fold, unlike `TargetId`.**
  `LegacyRepoId.cs:15-19` hashes the `git rev-parse --show-toplevel` string
  verbatim, whose casing follows the user's `cd` spelling — one repo can get
  two daemons and two app-data directories. Note any fix changes identity for
  already-indexed repos; migrate or verify against on-disk ids.
- **C16 (Low). Submodules and sparse checkouts degrade silently.** Gitlink
  entries are listed as plain paths (contents never indexed, pointer bumps
  surface as phantom upserts); skip-worktree entries flow through as "missing"
  skip telemetry. Neither is in the documented limitations. Filter mode-160000
  entries and document both.
- **C17 (Low). File-level symlinks can escape the root; reparse-point skips
  are silent.** `DirectoryTargetFileEnumerator.cs:47-51` skips directory
  reparse points without logging, and symlinked files pointing outside the
  root are still indexed — proposal §7.2 requires both to be handled.

### 3.5 CLI and daemon lifecycle

- **C18 (High, verified). `idx` crashes with an unhandled `GitProcessException`
  when run outside a git repository or when git is missing.**
  `CliApp.cs:65` catches only `InvalidOperationException or IOException or
  FileNotFoundException`; `GitProcessException : Exception`
  (`GitProcess.cs:230`) escapes, producing a CLR stack trace instead of the
  documented exit code 4 — on the single most common misuse path for the tool.
  **Fix:** catch it, print `idx: not a git repository (use --root for plain
  directories)`, return 4.
- **C19 (High, verified). Mid-request HTTP transport failures crash the CLI.**
  `DaemonClient.cs:125-171`: `GetStatusAsync`/`SearchAsync`/`RescanAsync`/
  `ShutdownAsync` never catch `HttpRequestException`, despite the class
  remarks (`:25-28`) promising synthetic `ErrorResponse` translation; the only
  transport catch is in the ping path (`:223`). Real trigger: daemon idle-exits
  between the adoption ping and the actual `/search`. Related: the
  `HttpClient` 60 s timeout surfaces as `TaskCanceledException`, which
  `Program.cs` maps to exit 130 ("interrupted") misleadingly. **Fix:** honor
  the documented contract; map transport failures to exit 4.
- **C20 (Medium). The CLI can orphan a live daemon forever.**
  `DaemonClient.cs:86-89`: if both liveness pings fail while the PID is alive
  (daemon under heavy GC/IO), the CLI deletes `daemon.json` and launches a new
  daemon, which dies on the singleton mutex and never writes `daemon.json`;
  the CLI polls the full 120 s, and the surviving daemon remains
  undiscoverable (and un-stoppable except by idle-exit) for the rest of its
  life. **Fix:** never delete `daemon.json` when the PID is alive; have the
  daemon periodically re-assert its `daemon.json`.
- **C21 (Medium). `Global\` mutex + per-user `daemon.json` deadlocks a second
  user on a shared machine, and the name is squattable.** `DaemonHost.cs:697`
  uses a machine-wide `Global\Indexed-{targetId}` mutex while discovery state
  is per-user; user B's daemon can never start for a shared path (and any
  local user can pre-hold the deterministic name to deny service). **Fix:**
  use `Local\` or incorporate the user SID.
- **C22 (Medium). Shutdown does not drain in-flight request handlers.**
  Handlers are fire-and-forget `Task.Run` (`DaemonHost.cs:276-287`);
  `DisposeAsync` stops the listener and disposes the SQLite index and gates
  while a `/search` may still be executing — connection resets and
  `ObjectDisposedException` are reachable via the routine idle-exit race.
  **Fix:** track in-flight handlers and await them with a short deadline.
- **C23 (Low). Latent lock-discipline gaps in `SqliteIndex`.** Instance
  `SetMeta` and the shutdown WAL checkpoint run on the writer connection
  without holding `_writerLock` (`SqliteIndex.cs:207-226`, `:1171-1183`) —
  safe with today's callers, but the API invites misuse and the checkpoint can
  silently no-op if shutdown races a batch.
- **C24 (Low). Assorted result-shaping bugs.** `Truncated` reports `true` on
  exactly-`MaxMatches` results with nothing dropped
  (`CodeQueryExecutor.cs:165-170`); trailing context can emit a phantom empty
  line for files ending in `\n` (`MatchExtraction.cs:96-106`); when the global
  cap truncates, the returned subset is "first N in rowid order" rather than
  first N in the requested sort order, and changes across rebuilds
  (`CodeQueryExecutor.cs:163-171` + `ORDER BY rowid` at `SqliteIndex.cs:786`);
  prose projection collapses multiple in-span occurrences into one match and
  can drift line numbers when normalization breaks line alignment
  (`ProseQueryExecutor.cs:83-102`).

## 4. Indexing speed

- **P1 (Medium). Indexing is fully single-threaded end to end.**
  Enumerate → classify → hash → decode → extract → upsert runs serially
  (`FullScanIndexer.cs:145-281`); SQLite needs one writer, but nothing requires
  the CPU-heavy stages (SHA-256, Roslyn, trigram tokenization) to serialize
  with I/O. A bounded producer pipeline feeding the existing single
  `WriterScope` consumer would cut cold-build wall time substantially on
  multi-core machines.
- **P2 (Medium). Full rescans re-read and re-hash every byte, and changed
  files are read twice.** `FullScanIndexer.cs:196-234`: no mtime+size
  short-circuit against the stored row (reconciliation already trusts exactly
  that pair at `IncrementalIndexer.cs:538-542`); files failing the SHA compare
  are re-read via `ReadAllBytesAsync`, plus a third open for the classifier's
  8 KiB peek; the hash stream uses a 4096-byte buffer. **Fix:** stat
  short-circuit, single read on cold build, ≥64 KiB buffer.
- **P3 (Medium). A git full scan enumerates the file list three times
  (6 process spawns).** `FullScanIndexer.cs:116-122` calls
  `GetFileCountHintAsync`, then `GetExplicitBinaryLogicalPathsAsync(null)`,
  then `EnumerateFilesAsync` — each pair of `git ls-files` spawns buffers full
  stdout. Enumerate once, derive the count, pass the list to the existing
  `GetBinaryAttrPaths(files, …)` overload.
- **P4 (Medium). The Roslyn extractor builds a full syntax tree per C# file
  just to read comment trivia.** `RoslynCSharpExtractor.cs:34-37` — and it
  runs again on every incremental upsert. `SyntaxFactory.ParseTokens` yields
  the same trivia at a fraction of the cost.
- **P5 (Low). Per-span/per-file command re-preparation.** `ReplaceProseSpans`
  and `UpsertFile` create fresh `SqliteCommand`s per span/file
  (`SqliteIndex.cs:714-747`); caching prepared commands per batch is a modest
  cold-build win.
- **P6 (Low). `GetFirstCommitSha` walks the entire history on every daemon
  start.** `GitRepository.cs:148-167` (`git rev-list --max-parents=0 HEAD`) is
  O(history) and immutable for a given repo — cache it in meta after first
  computation.

## 5. Index size reduction

Measured context: 7.3 GB index for an 8 GB corpus (Wolfram report), ~2.5 GB of
accumulated per-target state on this machine. The existing analysis
(`Indexed-Index-Size-Reduction-Strategies.md`) is good; the highest-leverage
new lever found here is Z1.

- **Z1 (Medium/high-leverage). `code_fts` stores full positional posting lists
  (`detail=full`) that the query planner never uses.** Every emitted MATCH
  expression is AND/OR of *single-trigram* phrases
  (`TrigramExpr.cs:96-128` writes one quoted 3-char token per literal; no
  multi-token phrases or NEAR are ever generated), so the per-occurrence
  position data — which the size doc's §1.3 identifies as the reason posting
  lists are O(occurrences) — is pure overhead today. `detail=none` supports
  single-token queries, and the executor re-verifies candidates from disk
  anyway. This is an actionable, plausibly large reduction in `code_fts_data`
  that the size docs never proposed. **Caveat:** it forecloses the
  single-phrase literal optimization S3 (§6), which needs positions — the two
  must be evaluated together (e.g., benchmark `detail=none` + AND-of-trigrams
  vs `detail=full` + phrase queries for both size and speed; the size doc's §8
  measurement plan is the right harness).
- **Z2 (Medium). `prose_fts` still stores span content verbatim** — the last
  stored-content copy in the schema, with `kind`/line metadata riding as
  UNINDEXED payload in the FTS content table (`SqliteSchema.cs:84-91`).
  Moving metadata to a plain table with external-content/contentless FTS and
  rehydrating snippets from disk (as the code path already does) would shrink
  it; `highlight()` would need re-derivation.
- **Z3 (Low, known-but-unimplemented). `PRAGMA auto_vacuum` hygiene.** Deleted
  pages are reused but the file never shrinks after large removals; the size
  doc recommends it, the schema never sets it (must be set before first table
  creation).
- **Z4 (Medium, operational). Orphaned state directories accumulate
  indefinitely.** Any tuning-flag change (`--max-indexable-file-mb`, globs,
  update mode) mints a new target id (`TargetId.cs:31-44`) and silently
  orphans the previous `index.db` forever; 16 directories / ~2.5 GB observed
  locally, several with no daemon.json. The proposed-improvements doc already
  calls for `idx gc`/`idx stats` — this review adds concrete evidence it is
  needed.

## 6. Search speed

- **S1 (High, verified). Pooled reader connections use SQLite shared-cache
  mode, defeating WAL reader concurrency.** `SqliteIndex.cs:1236-1248`
  (`OpenReader`, `Cache = Shared`; writer likewise at `:1222-1234`), while
  `OpenSyncReader` (`:1250-1268`) was deliberately switched to `Private` with
  a comment explaining exactly this problem. All search queries ride the
  shared-cache readers, so during write batches (back-to-back on a cold scan)
  reads hit SQLITE_LOCKED_SHAREDCACHE and serialize behind the writer —
  contradicting the architecture doc's "WAL-mode readers proceed concurrently
  with the writer" and the millisecond-class goal. **Fix:** `Cache = Private`
  (or default) for both; shared cache buys nothing in a single process with
  pooling off.
- **S2 (Medium). Candidate verification reads files strictly sequentially.**
  `CodeQueryExecutor.cs:118-173` — one awaited read per candidate; broad
  literals with hundreds of candidates pay the sum of N disk reads. A bounded
  4–8-way read pipeline preserves determinism (sort is applied at the end).
- **S3 (Medium). Literal search emits AND-of-trigram-windows instead of a
  single FTS5 phrase.** `CodeQueryPlanner.cs:43-54` + `TrigramExpr.cs:207-212`:
  `AND("sea","ear","arc",…)` matches scattered trigrams anywhere in a file;
  a single quoted phrase uses positional data for true substring semantics
  (exactly how FTS5 trigram LIKE works), shrinking the candidate set the
  executor must read and verify — and it fixes C2 for the literal path, since
  the tokenizer windows codepoints server-side. Trade off against Z1 (§5).
- **S4 (Medium). Prose queries materialize and `highlight()` every matching
  row with no LIMIT.** `SqliteIndex.cs:859-905` fully materializes and ranks
  all rows before `ProseQueryExecutor` discards all but `MaxMatches` (default
  200). Stream the reader and/or push `LIMIT` when no post-filter is present.
- **S5 (Medium). CLI fixed overhead undercuts the "millisecond-class" goal.**
  Measured ~1.2 s median for `idx status`. No `PublishReadyToRun`, trimming,
  or NativeAOT anywhere (`Indexed.Cli.csproj`, `Directory.Build.props`) — and
  the warm path also spawns `git rev-parse --show-toplevel` +
  `git rev-list --max-parents=0 HEAD` on every invocation just to recompute a
  target id a healthy daemon already advertises (`DaemonClient.cs:73`).
  **Fix:** `PublishReadyToRun=true` is a one-line safe win; NativeAOT is
  plausible (JSON is already source-generated); cache `cwd → targetId` to skip
  git on the warm path.
- **S6 (Low). Per-request glob compilation with `RegexOptions.Compiled`.**
  `PathGlob.cs:44-51` IL-emits per query; agents reuse the same `--glob` on
  every call. Small LRU cache keyed by (glob, ignoreCase), or drop `Compiled`.
- **S7 (Low). Wasted work on the candidate miss path and O(hits × size)
  offsets.** `ComputeLineOffsets` runs eagerly for every candidate including
  trigram false positives (`CodeQueryExecutor.cs:238`); `Utf8ByteOffsetOf`
  re-encodes the whole prefix per match (`MatchExtraction.cs:112-119`) —
  compute lazily/incrementally.
- **S8 (Low). Regex execution can overrun the request deadline.** Per-`Match`
  timeout is set to the full `TimeoutMs` and the per-hit loop never observes
  the token (`CodeQueryPlanner.cs:71`, `CodeQueryExecutor.cs:268-301`).

## 7. Robustness and operations

- **O1 (High). Daemon diagnostics are a black hole in the normal auto-start
  path, and the docs promise logs that are never written.** The daemon logs
  only to console (`Program.cs:35-37`); `DaemonLauncher` spawns it with
  `CreateNoWindow = true` and no stdio redirection (`DaemonLauncher.cs:65-68`),
  so every log line — including batch-failure and crash messages — is
  discarded. Meanwhile the usage guide (§ lines 659, 743, 774) and
  architecture doc document daily-rotated logs at
  `%LOCALAPPDATA%\Indexed\<targetId>\logs\`, a directory
  `DaemonPaths.EnsureCreated` creates and nothing ever writes to. Users
  following the documented troubleshooting steps find an empty directory.
  **Fix:** add a small rolling file-logger provider targeting the
  already-documented directory. This single fix also gives C4, O2, O3 a
  visible signal.
- **O2 (Medium). A dead `FileSystemWatcher` is never restarted.**
  `DirectoryWatcher.cs:129-133`: `OnError` requests reconciliation (good), but
  root-deletion/handle-invalidation errors permanently kill event delivery and
  the daemon silently degrades to 5-minute polling. Recreate the watcher with
  backoff.
- **O3 (Medium). Mid-run database corruption never triggers the self-healing
  rebuild.** Recovery exists only at open (`SqliteIndex.cs:103-177`);
  SQLITE_CORRUPT during a batch becomes the C4 fail-and-drop-forever loop.
  Detect corruption result codes in the worker and signal a rebuild.
- **O4 (Medium). Daemon launch failures are indistinguishable from slow
  starts.** `DaemonLauncher.cs:122-125` disposes the child process handle
  immediately; if the daemon exits at once (corrupt DB, native-load failure),
  the CLI polls the full 120 s and reports only a generic timeout. Poll
  `HasExited` and fail fast with the exit code.
- **O5 (Low). `IndexOptimizer` clears the dirty flag after a single bounded
  merge** (`IndexOptimizer.cs:179-199`), so merge backlog after a large ingest
  is never drained if no further writes arrive. Loop/re-arm while the merge
  reports rows changed.
- **O6 (Low). Assorted convergence leaks.** Reconciliation never prunes stale
  `file_skips` rows for files deleted while the daemon was down
  (`IncrementalIndexer.cs:510-522`); a directory rename strands the subtree
  under the old prefix until reconciliation (`DirectoryWatcher.cs:114-127`);
  `HeadPoller`'s error counter only resets when HEAD moves
  (`HeadPoller.cs:146-161`); `FullScanIndexer` never deletes vanished files
  despite claiming to "restamp the world" (latent — only run on empty DBs
  today).
- **O7 (Low). `GitProcess` edges.** Cancellation is not honored during
  `WaitForExit` (up to the full 60 s); `IsLockContention` retries on any
  "Unable to create" stderr; `GitRepository` carries production-dead code with
  stale doc comments (`IsLikelyBinary` with a hard-coded 50 MB constant,
  `GetIndexMtime` still claiming HeadPoller uses it).

## 8. Security and trust boundary

The localhost-unauthenticated-read model is documented (architecture doc §12)
and reasonable for the product; the gaps are where the implementation diverges
from its own stated rules.

- **X1 (Medium). `POST /rescan` is a mutating endpoint with no
  authentication.** `DaemonHost.cs:476-485` has no token check, while
  `DaemonInfo.cs:20-27` explicitly says the token should protect `/rescan`
  "when it gets side effects" — which it has had since Stage 4. Any local
  process (or any web page via blind cross-origin POST) can drive repeated
  full-tree hashing loops. **Fix:** require the existing token; the CLI
  already reads `daemon.json`.
- **X2 (Low). No Origin/Content-Type validation on POST endpoints.** Blind
  CSRF from browsers can trigger `/search`/`/rescan` work (responses are
  unreadable; DNS rebinding is blocked by the http.sys host check). Reject
  POSTs with a foreign `Origin` or non-JSON `Content-Type`.
- **X3 (Medium, same as C21).** The machine-wide mutex name is computable and
  squattable — a local-DoS primitive on shared machines.
- Directory-mode trust-boundary guardrails (refusing `C:\`, `%USERPROFILE%`,
  `.ssh` roots without an explicit override) remain unimplemented from the
  proposed-improvements doc §4.5 and are worth doing before wider
  distribution.

## 9. Usability

- **U1 (Medium). `--glob` semantics diverge from the advertised
  "gitignore-style" contract.** Patterns are anchored (`PathGlob.cs:56-123`),
  so `--glob "*.cs"` matches only root-level files, where gitignore/ripgrep
  match slashless patterns at any depth; `[abc]` classes are escaped to
  literals; `!` negation is unsupported — while `SearchRequest.cs:55-64`
  promises gitignore-style. Agents porting `rg -g` habits get silently empty
  filters — this compounds the §3.1 silent-false-negative class. **Fix:**
  implement the slashless-matches-anywhere rule and `[...]`, or re-document
  the subset precisely.
- **U2 (Medium). ripgrep-parity and agent-ergonomics gaps.** `-g` is not
  repeatable (second one silently overwrites; `ArgumentParser.cs:110-113`)
  unlike `--exclude` and unlike rg; `-e` means `--regex` here but "pattern" in
  rg — a trap for agents; no smart-case; no `--count`/`--count-matches`/
  `--files-with-matches` (the ripgrep-comparison report showed these avoid the
  10 s large-cap materialization path entirely); no `--files` listing; no
  shell completion.
- **U3 (Medium). `idx stop`/`idx rescan` cold-start a daemon when none is
  running** — including potentially a full initial scan of gigabytes, just to
  stop it (`CliApp.cs:46-63` routes all verbs through `DaemonClient.CreateAsync`).
  Probe-only for these verbs.
- **U4 (Low). `idx daemons` lists dead daemons indistinguishably from live
  ones** (`DaemonCatalog.cs:23-27`); apply the client's PID+start-time
  liveness check and flag them.
- **U5 (Low). Misc.** The daemon inherits and pins the CLI's working
  directory for its lifetime (`DaemonLauncher.cs:69`); `--idle-timeout-seconds 0`
  is accepted and exits almost immediately; unknown routes return HTTP 404
  with body code `BadRequest` (`DaemonHost.cs:507-511`); the 403 shutdown-token
  error still uses the retryable `unavailable` code (open since the post-fix
  review's M3).
- **U6 (Medium, roadmap).** The target registry (`idx target add/list/…`),
  richer per-root `/status`, and `idx gc`/`idx stats` from the
  proposed-improvements doc remain the right UX investments; the orphaned-state
  evidence in Z4 makes `gc`/`stats` the most urgent of the three.

## 10. Tests

Overall quality is genuinely good — behavioral tests, real git repos, a real
`DaemonHost` on a loopback port, pinned JSON wire shapes. The gaps align
precisely with where this review found bugs:

- **T1 (High). No property-based/differential testing for `RegexTrigrams`** —
  the soundness-critical component (1,169 lines of hand-rolled parser +
  analyzer). The existing "property" test is 10 hand-picked ASCII patterns
  against a 9-document corpus, and its oracle (`Satisfies` →
  `TrigramExtraction.WindowsOf`) shares production's UTF-16 windowing, making
  C1/C2/C3 structurally undetectable. **Fix:** add CsCheck/FsCheck: random
  patterns (including adversarial escapes and non-BMP input) + random corpora,
  asserting the candidate-superset invariant against an actual FTS5 table, and
  differential-test `RegexParser` against `System.Text.RegularExpressions`.
- **T2 (Medium). The CLI application layer is untested** — zero tests
  reference `CliApp`, `DaemonClient`, or `DaemonLauncher`; the untested paths
  (exit codes, adoption/liveness/deletion, launch failure) are exactly where
  C18/C19/C20/O4 live. Add one process-level E2E plus unit tests against a
  fake HTTP server.
- **T3 (Medium). The target-identity layer has zero unit tests** — no test
  references `TargetId.Compute`, `LegacyRepoId`, `TargetSpecFactory`, or
  `TargetRootArgumentParser`, although an identity regression silently orphans
  every index. Add golden-hash tests pinning exact ids for fixed inputs.
- **T4 (Medium). No lifecycle-race or search-during-index stress tests**
  (simultaneous daemon starts, idle-exit vs in-flight request, N searches
  under continuous churn — the scenario S1 makes slow today).
- **T5 (Low).** `ReconciliationScheduler`, `BinaryFileClassifier`,
  `LanguageGuess`, `IncludeFilter`, `LengthLimitingStream` have zero test
  references; no end-to-end over-cap-file → `/status` telemetry test; the
  polling `WaitForAsync` idiom carries flake risk once CI exists.

## 11. Documentation

Docs discipline is well above average — a broad spot-check of flags, defaults,
endpoints, caps, and layout matched the code. Exceptions:

- **D1 (High, same root as O1).** The usage guide and architecture doc
  document rotating log files no code writes. Fix the code (preferred) or the
  docs — not neither.
- **D2 (Low). DTO/doc-comment drift:** `SearchRequest.cs:25-29` claims code
  literals run as degenerate regexes (they use `IndexOf`);
  `CodeQueryPlanner.cs:31` documents an `ArgumentException` fallback that is
  actually `CodeQueryPlanException`; `RegexAst.cs:12-16` claims backrefs parse
  as opaque (see C1); the architecture doc's "WAL readers proceed concurrently"
  is contradicted by S1 until fixed.
- **D3 (Low). docs/ organization.** Six hash-suffixed historical reviews sit
  beside current product docs; move to `docs/reviews/` or `docs/archive/` and
  add an index. Known limitations should gain: UTF-16 files unindexed (until
  C5), glob subset semantics (until U1), submodule/sparse behavior (C16), and
  the tuning-flag → new-target-id orphaning behavior (Z4).

## 12. Build, packaging, portability

- **B1 (High, verified/reproduced). Release builds — and therefore the
  README's documented install path — fail today.** `Directory.Build.props`
  sets `TreatWarningsAsErrors` for Release, and NU1903 fires:
  `SQLitePCLRaw.lib.e_sqlite3` 2.1.10 has a known high-severity advisory
  (GHSA-2m69-gcr7-jv3q), pulled transitively via `Microsoft.Data.Sqlite`
  9.0.8. Reproduced: `dotnet build src/Indexed.Core -c Release` → `error
  NU1903`. **Fix:** add a direct `SQLitePCLRaw.bundle_e_sqlite3` ≥ 2.1.11
  reference (or bump `Microsoft.Data.Sqlite` to a patch carrying it).
- **B2 (Medium). No CI.** No `.github/` or pipeline files; a ~20-line
  windows-latest workflow running `dotnet build -c Release` + `dotnet test`
  would have caught B1 at the introducing commit.
- **B3 (Medium). No version story.** All assemblies are 1.0.0; the CLI never
  compares `DaemonVersion` (or even `TargetId`) from `/status` on adoption, so
  after an upgrade a long-lived old daemon silently keeps serving the old
  contract. Stamp real versions (MinVer/NBGV) and restart-on-major-mismatch.
- **B4 (Medium). `net10.0-windows` appears to be pure TFM choice.** No
  WinForms/WPF, registry, or DllImport usage; the genuinely Windows-specific
  bits are already guarded by `OperatingSystem.IsWindows()`. Retargeting to
  `net10.0` is likely near-free and would honestly re-scope "Windows-only" to
  "Windows-tested" — the cheap first step of the long-standing porting
  recommendation.
- **B5 (Low).** No central package management (`Directory.Packages.props`);
  `Microsoft.Extensions.Logging*` pinned at 9.0.0 on a net10 TFM; no
  ReadyToRun/AOT (see S5).

## 13. Prioritized recommendations

**Now (correctness and the broken pipeline; small, high-certainty fixes):**

1. Fix B1 (SQLitePCLRaw bump) and add the minimal CI workflow (B2).
2. Make the regex escape parser conservative — opaque, never literal, for
   anything not whitelisted (C1); window trigrams by codepoint or switch
   literals to single-phrase MATCH (C2/S3); stop pre-lowercasing phrase text
   (C3).
3. Isolate per-file indexing failures with an `indexing_error` skip reason and
   extractor regex timeouts (C4).
4. Switch pooled reader/writer connections to private cache (S1) — a two-line
   change that removes search-vs-index serialization.
5. Catch `GitProcessException` and transport exceptions in the CLI (C18, C19).
6. Special-case F# `(*)` and add nesting depth to F#/Rust block scanning (C10).
7. Add rolling file logging to the already-documented `logs/` directory (O1) —
   it makes every other failure in this report observable.

**Next (convergence, identity, and safety):**

8. Startup reconciliation for non-empty indexes (C7); `scan-complete` marker
   (C8); idle-timer poke during initial scans (C9); bounded debounce re-drain
   (C6).
9. Canonicalize root paths (symlinks/junctions/short names/UNC) in target
   identity and disjointness checks (C14), and case-fold `LegacyRepoId` with a
   migration plan (C15).
10. Never delete `daemon.json` for a live PID; daemon re-asserts its
    descriptor (C20). Token-protect `/rescan` (X1). Per-user mutex naming
    (C21). Drain in-flight requests on shutdown (C22).
11. Add the missing test tiers: property/differential trigram fuzzing (T1),
    golden-hash target-identity tests (T3), one CLI E2E (T2).

**Then (performance and product):**

12. Benchmark `detail=none` vs single-phrase-literal designs for `code_fts`
    (Z1 vs S3) using the size doc's §8 harness; contentless `prose_fts` (Z2);
    `auto_vacuum` (Z3).
13. Parallelize the indexing pipeline (P1), stat short-circuit on rescan (P2),
    single git enumeration (P3), lexer-only C# extraction (P4).
14. ReadyToRun/AOT the CLI and skip warm-path git spawns (S5); bounded-parallel
    candidate verification (S2); prose LIMIT pushdown (S4).
15. Fix glob semantics or re-document them (U1); repeatable `-g`, count/files
    modes, smart-case (U2); probe-only `stop`/`rescan` (U3); then the roadmap
    trio — `idx gc`/`stats` (with Z4's orphan evidence, first), richer
    per-root status, target registry (U6).
16. Retarget to `net10.0` and validate on Linux (B4).

---

*Findings are labeled C (correctness), P (indexing performance), Z (index
size), S (search speed), O (operations/robustness), X (security), U
(usability), T (tests), D (docs), B (build). Items already documented as known
limitations (whole-file reads, FTS5 prose syntax, line-oriented regex,
localhost trust model, FSW best-effort semantics) were excluded or explicitly
marked; they are not re-reported as new discoveries.*
