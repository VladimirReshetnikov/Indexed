# Indexed — Implementation Plan

- Created (UTC): 2026-04-15T14:31:51Z
- Repository HEAD: a2e6b2e2bef0e26b79ac0a6a41dd277c6d2ebd75
- Status: **Draft plan**. Expands the staging outline in [`Indexed-Architecture-Proposal.md` §15](./Indexed-Architecture-Proposal.md) into a per-stage task breakdown with preconditions, deliverables, tests, exit criteria, risks, and explicit out-of-scope items.
- Audience: Implementers (human or agent) picking up any stage. Written so an agent can open this document, choose a stage with satisfied preconditions, and execute it without re-deriving the design.
- Scope: Everything required to land stages S0 through S5 of the Indexed service. S6 (optional post-v1 work) is covered at coarser granularity.

## How to use this document

- Stages are **sequential**. Each stage declares its preconditions; do not start a stage until they are satisfied.
- Within a stage, tasks are listed in **execution order**. Sizing tags (`S` / `M` / `L`) are rough budgets: `S` = a few hours to a day, `M` = 1–3 days, `L` = 3+ days.
- **Every stage ends with a runnable daemon + CLI.** No stage is allowed to leave the tree in a half-built state.
- Every task should land with its tests. Stages do not close until the test coverage listed under "Test coverage" is passing on CI.
- When a task references the proposal (e.g., "§4.2 schema"), it means the corresponding section of `Indexed-Architecture-Proposal.md`. That document is the authority for shape and contracts; this plan is the authority for order and scope.

## Cross-cutting standards

These apply to every stage. They are listed once here rather than repeated per task.

### Targets and build

- Target framework: **`net10.0-windows`**. One `Directory.Build.props` at `src/Indexed/` pins the target, language version, nullable annotations, and treat-warnings-as-errors policy.
- Nullable reference types: **enabled** across all projects. Any `#nullable disable` needs a comment explaining why.
- Warnings as errors: **on** for Release. CI builds Release.
- Solution file: `src/Indexed/Indexed.sln`. All projects live under `src/Indexed/src/` and `src/Indexed/tests/`.

### Coding style

- Follow the preferences recorded in [`PREFERENCES.md`](../../../PREFERENCES.md) at the repository root, particularly the conventions around single-purpose projects, explicit layer boundaries, and test deduplication.
- Project namespaces mirror project names: `Indexed.Core` types live under `Indexed.Core.*`, not in a catch-all `Indexed.*` namespace.
- Internal types stay internal unless a test needs them, in which case use `InternalsVisibleTo` for the matching test project — not `public`.

### XML doc discipline

- Follow [`XML_DOCUMENTATION_STANDARD.md`](../../../XML_DOCUMENTATION_STANDARD.md) at the repository root.
- Interop-heavy and lifecycle-sensitive APIs get `<example>` blocks. Trivial DTOs and getters do not.
- Document **meaning** (preconditions, postconditions, failure modes, ordering, ownership) — not the signature.
- Priority surfaces that must be documented:
  - `Indexed.Abstractions` — all public types and members.
  - `Indexed.Core` public API — `SqliteIndex`, `CodeQueryPlanner`, `ProseQueryPlanner`, indexer orchestration.
  - `Indexed.Extractors` — the `IContentExtractor` contract and the Roslyn extractor (stripping rules, what is and is not preserved).
  - `Indexed.Service` — endpoint contracts.
- Lower-priority: glue code, internal helpers.

### Testing discipline

- Tests live under `src/Indexed/tests/<Project>.Tests/`. One test project per source project; no omnibus test project.
- Prefer `InlineData` over `TheoryData` when arguments are compile-time constants. Use `MemberData` / `TheoryData` for framework-specific pseudo-constants or shared data generators.
- Collapse semantically identical tests into theory data. Keep separate tests only for genuinely distinct behavior.
- **Property tests** (FsCheck) are required wherever a linear-scan reference implementation exists: regex → trigram planning, extractor round-trips, merge/dedupe semantics.
- **Golden-file tests** for extractors. Each extractor owns a directory of input files paired with expected `ProseSpan[]` JSON outputs.
- Integration / benchmark tests live in `tests/Indexed.Benchmarks/` and run against `C:\Tools2\Tools` itself (read-only).

### Dependency policy

- No dependency on any `Near.*` project. Indexed is standalone.
- Permitted NuGet dependencies, by project:

  | Project | Dependencies |
  |---------|--------------|
  | `Indexed.Abstractions` | none |
  | `Indexed.Core` | `Microsoft.Data.Sqlite`, `Indexed.Abstractions`, `Indexed.Extractors` |
  | `Indexed.Extractors` | `Microsoft.CodeAnalysis.CSharp`, `Indexed.Abstractions` |
  | `Indexed.Git` | `Indexed.Abstractions` (invokes `git.exe` via `Process`; no libgit2) |
  | `Indexed.Watcher` | `Indexed.Abstractions` |
  | `Indexed.Service` | `Indexed.Core`, `Indexed.Git`, `Indexed.Watcher` |
  | `Indexed.Cli` | `Indexed.Abstractions`, `System.Net.Http` |
  | Any tests | xUnit, FsCheck.Xunit for property tests |

- Additional dependencies require a short justification in the commit that introduces them.

### Git hygiene

- One logical change per commit. Stage-level milestones can span multiple commits.
- Commit message first line ≤ 72 chars, imperative mood. Body explains why, not what (diff shows what).
- Follow-up cleanups that cross stage boundaries are acceptable; record them in the stage's **Risks / Deferred** table rather than silently stretching the stage.

## Stage 0 — Workspace scaffolding

### Preconditions

- .NET 10 SDK installed locally.
- `git` on PATH.
- `src/Indexed/docs/Indexed-Architecture-Proposal.md` reviewed and accepted (it is).

### Deliverables

- `src/Indexed/Indexed.sln` — empty solution.
- `src/Indexed/Directory.Build.props` — shared build props (target framework, warnings, nullable, analyzers).
- `src/Indexed/README.md` — one-page pointer to the proposal + this plan.
- Skeleton project: `src/Indexed/src/Indexed.Abstractions/` with empty `DTOs.cs` placeholder and a matching test project.
- CI touch: ensure the new `.sln` builds clean and runs its (trivial) test.

### Task breakdown

0.1 **Create solution and shared build props (S).**
Create `Indexed.sln`. Author `Directory.Build.props` with `TargetFramework=net10.0-windows`, `Nullable=enable`, `TreatWarningsAsErrors=true` (Release), `ImplicitUsings=enable`. Add a `.editorconfig` inherited from the repo root (or a local one mirroring repo convention).

0.2 **Scaffold `Indexed.Abstractions` + `Indexed.Abstractions.Tests` (S).**
Create both projects, wire them into the solution, add an `InternalsVisibleTo` for the test project. Land a single trivial test (`Assert.True(true)`) to prove CI wiring.

0.3 **Scaffold `src/Indexed/README.md` (S).**
Short: what Indexed is, one-paragraph summary, pointer to proposal and plan, explicit "no implementation yet — in progress on stage X" banner that each stage updates.

### Test coverage

Trivial at this stage — just confirm the solution builds and the one placeholder test runs.

### Exit criteria

- `dotnet build src/Indexed/Indexed.sln -c Release` succeeds with zero warnings.
- `dotnet test src/Indexed/Indexed.sln` runs the placeholder test.

### Not in this stage

- Any DTO definitions (those come with S1).
- Any runtime project scaffolding beyond Abstractions.

### Risks

| Risk | Mitigation |
|------|------------|
| Shared-props collision with repo-root `Directory.Build.props` | Indexed's props should `<Import>` or re-declare explicitly; do not assume inheritance works across `src/` boundaries without checking |
| `net10.0-windows` availability on CI | Confirm SDK version in CI before the other stages start consuming it |

---

## Stage 1 — Enumeration + CLI shim

**Goal:** Stand up the HTTP contract, CLI ergonomics, daemon lifecycle, and git-authoritative file set. Queries are served by a ripgrep-fallback backend so the user-visible surface is exercised end-to-end without an index yet.

### Preconditions

- S0 complete.
- `rg` (ripgrep) available on PATH for the fallback backend.

### Deliverables

- `Indexed.Abstractions` fully populated with request / response DTOs.
- `Indexed.Git` project with git enumeration.
- `Indexed.Service` project hosting the HTTP daemon and the ripgrep backend.
- `Indexed.Cli` project with `idx find / status / rescan / stop`.
- Daemon bootstrap: single-instance mutex, port-file atomic write, idle-exit timer, detached launch from CLI.
- End-to-end: `idx find "pattern"` returns JSON matches.

### Task breakdown

1.1 **Finalize `Indexed.Abstractions` DTOs (M).**
Types to author:
- `QueryMode` enum: `Auto`, `Code`, `Prose`.
- `SpanKind` enum: `Code`, `Markdown`, `PlainText`, `XmlDoc`, `LineCommentBlock`, `BlockComment`.
- `SearchRequest` record (shape per proposal §8.1).
- `Match` record (shape per §8.2): `Path`, `Line`, `Column`, `ByteOffset`, `Text`, `Kind`, `Span?`, `ContextBefore`, `ContextAfter`.
- `MatchSpan` record: `StartLine`, `EndLine`.
- `Freshness` record.
- `SearchResponse` record.
- `StatusResponse` record.
- `IndexedErrorCode` enum and `ErrorResponse` record for failure shape.
- `JsonSerializerContext` for all of the above (source-generated `System.Text.Json` for AOT-friendliness).

Document every public type with XML docs per the standard.

1.2 **Implement `Indexed.Git` (M).**
Public surface:
```csharp
public sealed class GitRepository
{
    public static GitRepository Open(string directory); // walks up to find .git
    public string RepoRoot { get; }
    public string GetHeadSha();
    public string GetFirstCommitSha();
    public IReadOnlyList<string> EnumerateFiles(); // union of ls-files + untracked-not-ignored
    public bool IsLikelyBinary(string relativePath); // size + NUL-in-first-8KB check
    public IReadOnlySet<string> GetBinaryAttrPaths(); // `git check-attr -z binary`, batched
}
```
Invokes `git.exe` via `System.Diagnostics.Process` with `-z` flags for null-separated output. Never assumes UTF-8 encoding for paths — read as raw bytes where possible.

1.3 **Implement daemon bootstrap in `Indexed.Service` (M).**
- `DaemonHost` class: starts `HttpListener` on `127.0.0.1:0`, writes `daemon.json` via temp-file + rename, acquires `Global\Indexed-<repoId[0:12]>` named mutex.
- `IdleExitTimer`: tracks last request + last index activity; fires after configured idle window (default 30 min).
- `ShutdownEndpoint`: validates caller is local via `GetExtendedTcpTable` process-token match.
- `RepoId` helper: `SHA1(abspath + "\0" + firstCommitSha)`, truncated to 12 hex chars for directory naming.

1.4 **Implement `RipgrepSearchBackend` in `Indexed.Service` (M).**
Invokes `rg --json --no-ignore-parent --smart-case=$(caseSensitive?no:yes) --glob <pathGlob> <pattern>` and parses the streaming JSON. Maps to `Match` records. Sets `Kind = Code` on every result (S3 replaces this with real extraction). Surfaces a `freshness` block that is always `isStale: true` with a note "ripgrep-backed; no index present yet."

1.5 **Implement `Indexed.Cli` (M).**
Commands:
- `idx find <pattern> [--mode auto|code|prose] [--glob ...] [--exclude ...] [--regex] [--json] [--kind ...]`
- `idx status`
- `idx rescan`
- `idx stop`

Daemon auto-start: CLI checks `%APPDATA%\Indexed\<repoId[0:12]>\daemon.json`; if absent or `/status` refuses connection, launches the service binary with `DETACHED_PROCESS` + `CREATE_NO_WINDOW` (via `ProcessStartInfo` or direct `CreateProcess` P/Invoke if the managed flags are insufficient on net10).

Default output: ripgrep-style `path:line:col:text`. `--json` passes the daemon's response through unchanged.

1.6 **Wire up logging (S).**
Structured logs to `%APPDATA%\Indexed\<repoId[0:12]>\logs\indexed-YYYYMMDD.log` with daily rotation. Library choice: keep it small — `Microsoft.Extensions.Logging` with a file provider is acceptable. Defer richer logging to S5.

### Test coverage

- `Indexed.Abstractions.Tests` — DTO serialization round-trip for every type, error-response negative shapes.
- `Indexed.Git.Tests` — enumeration against a synthetic on-disk repo (create via `git init` in test setup): tracked / untracked / ignored / binary; rename and delete cases; HEAD and first-commit probing; `.gitattributes` binary marker.
- `Indexed.Service.Tests` — in-process `HttpClient` against `HttpListener`; `/status`, `/search` (ripgrep-backed), `/rescan`, `/shutdown`; port-file atomic write under simulated crash; single-instance mutex enforcement.
- `Indexed.Cli.Tests` — argument parsing, output formatting (text vs. `--json`), daemon-auto-start (using a fake launcher to avoid spawning a real process).

### Exit criteria

- `idx find "Indexed"` on this repo returns JSON matches in < 500 ms.
- `idx status` shows `isStale: true` and a useful message.
- Daemon exits cleanly after idle window.
- Killing the daemon mid-request leaves no orphaned `daemon.json`.
- All S1 tests green on CI.

### Not in this stage

- Any FTS5 / SQLite work.
- Any extraction logic.
- `FileSystemWatcher` — S4.

### Risks / Deferred

| Risk | Mitigation |
|------|------------|
| `rg --json` output version drift | Pin a minimum ripgrep version; test against `rg --version` in CI |
| `HttpListener` on net10 Windows — URL reservations | Bind loopback only; `httpcfg` / `netsh urlacl` not needed for `127.0.0.1` in non-admin contexts |
| Single-instance mutex leakage on crash | Named mutexes are released by the kernel on process termination; verified by test |

---

## Stage 2 — FTS5 code index

**Goal:** Replace the ripgrep backend with a real SQLite FTS5 trigram index over code. `mode: "code"` queries are served from the index.

### Preconditions

- S1 complete.
- `Microsoft.Data.Sqlite` (matching the Near/Nmux driver choice) available and tested against the FTS5 trigram tokenizer. **Verify before starting** that the bundled SQLite is ≥ 3.34 with `trigram` tokenizer support: `SELECT sqlite_version();` + `CREATE VIRTUAL TABLE t USING fts5(x, tokenize='trigram');`.

### Deliverables

- `Indexed.Core` project with SQLite wrapper, schema, query planner for code, full-scan indexer.
- Russ Cox regex→trigram planner (`Indexed.Core.RegexTrigrams`), ported to C#.
- `mode: "code"` served from the index; ripgrep backend deleted.
- Schema version 1, stored in `meta`.
- `index.db` created on first startup; rebuilt on schema mismatch.

### Task breakdown

2.1 **Scaffold `Indexed.Core` and SQLite wrapper (S).**
```csharp
public sealed class SqliteIndex : IAsyncDisposable
{
    public static SqliteIndex OpenOrCreate(string dbPath);
    public int SchemaVersion { get; }
    public ValueTask<IReadOnlyList<long>> QueryCodeTrigramsAsync(TrigramExpr expr, ...);
    public ValueTask InsertFileAsync(long fileId, string path, ReadOnlyMemory<byte> bytes, string? language, ...);
    public ValueTask DeleteFileAsync(long fileId);
    // ...
}
```
Connection management: one writer connection (used by the indexer worker), a pool of reader connections. WAL mode set at open (`PRAGMA journal_mode=WAL`).

2.2 **Schema bootstrap + migration (S).**
Schema SQL matches proposal §4.2 exactly. On open, read `meta.schema_version`; if absent, create schema and set version = 1. If present and different, close, delete `index.db`, reopen to trigger fresh create.

2.3 **Port Russ Cox's regex→trigram planner (L).**
Author `Indexed.Core.RegexTrigrams`:
- Parse regex with `System.Text.RegularExpressions` (or a minimal internal parser if the public surface doesn't expose the AST adequately — in practice, wrap with a hand-rolled parser for just the constructs we need: literal, alternation, concatenation, star/plus/?, char class, anchors).
- Compute an expression tree over trigram sets with operators `And`, `Or`, `AnyOf`.
- Emit an FTS5 `MATCH` string: e.g., `("abc" AND "bcd") OR "xyz"`.
- Fallback: if analysis yields "any trigram" (no constraint), return null → caller runs full scan over the globbed set.

Reference: Russ Cox, "Regular Expression Matching with a Trigram Index" (2012). The algorithm is ~500 lines in Go; similar size in C#.

Critical unit tests:
- `foo` → requires "foo"
- `foo|bar` → requires ("foo" OR "bar")
- `foo\s+bar` → requires ("foo" AND "bar")
- `^foo$` → requires "foo"
- `f.o` → requires nothing (weak)
- Golden file corpus of ~50 patterns spanning the above plus edge cases.

2.4 **Implement `FullScanIndexer` (M).**
Single-threaded indexer. For each file from `GitRepository.EnumerateFiles()`:
1. Apply binary / size / exclude-list filter.
2. Compute SHA-256.
3. Check `files.sha256` — skip if unchanged.
4. UPSERT `files`, replace `code_fts` row with raw bytes. One transaction per batch (200 files or 250 ms).

No extraction yet — `prose_fts` is untouched (S3 adds it).

2.5 **Implement `CodeQueryPlanner` + `CodeQueryExecutor` (M).**
Planner:
- Literal pattern → extract all 3-byte windows of lowercase pattern → AND them.
- Regex pattern → call `RegexTrigrams`.
- Empty trigram expression → execute full scan over glob-narrowed file set (with warning in response).

Executor:
- Intersect posting lists via FTS5 `MATCH`.
- Apply `pathGlob` / `excludeGlob` via `files.path LIKE` or via application-side glob (benchmarks will show which is faster).
- For each candidate `file_id`, read `code_fts.content` (or re-read disk for correctness — design choice; benchmark both).
- Run .NET `Regex` or byte-literal scan with `caseSensitive` honored.
- Extract line/column/context.

2.6 **Swap backend in `Indexed.Service` (S).**
Delete `RipgrepSearchBackend`. Wire `Indexed.Service` to `SqliteIndex` + `CodeQueryExecutor`. `mode: "code"` serves from FTS5; `mode: "prose"` and `mode: "auto"` return a `NotImplemented` error pointing to S3.

2.7 **Repo-rebuild-on-start for v1 (S).**
The daemon on startup: open DB, check integrity, if empty or schema-mismatched, run full-scan indexer before answering queries. This is the temporary equivalent of the watcher — S4 replaces it.

### Test coverage

- `Indexed.Core.Tests`:
  - Schema create + version match.
  - Schema mismatch triggers rebuild.
  - `SqliteIndex` round-trip: insert file, query by trigram, verify candidate list.
  - `CodeQueryPlanner` against pattern corpus (golden file).
  - `RegexTrigrams` property test: for a random pattern and random corpus, candidates from the planner are a **superset** of actual matches (never miss a true match).
  - `CodeQueryExecutor`: line/column/context extraction correctness on UTF-8 with BOM, CRLF, and mixed line endings.
- `Indexed.Service.Tests` extended for real FTS5-backed responses.
- `tests/Indexed.Benchmarks/` — new project. Runs pre-recorded queries against `C:\Tools2\Tools`, asserts latency envelopes per proposal §14. Allowed to be flaky on overloaded CI — tag as `[Category("benchmark")]`.

### Exit criteria

- `idx find "IndexManifest" --mode code` returns matches from FTS5 in ≤ 10 ms (warm).
- `idx find -e "class\s+\w+Index" --regex --mode code` works.
- Cold rebuild of `index.db` against this repo completes in ≤ 60 s.
- `index.db` size ≤ 3× source bytes.
- Ripgrep fallback path removed; `rg` is no longer a runtime dependency.
- All S2 tests green.

### Not in this stage

- Extraction (S3).
- Prose querying (S3).
- Incremental updates (S4).

### Risks / Deferred

| Risk | Mitigation |
|------|------------|
| FTS5 trigram tokenizer missing in bundled SQLite | Verified in preconditions. If missing: either switch `SQLitePCLRaw` bundle or statically link a newer SQLite |
| Regex→trigram port introduces subtle bugs | Property test ensures superset invariant — a bug makes queries slower, not wrong |
| Full-scan rebuild on every startup frustrates developers before S4 | Keep rebuild logic gated on "empty DB or schema mismatch"; once built, reopens are instant (S4 adds real updates) |
| `files.path LIKE 'src/%'` for globs may scan large fractions of the table | Benchmark against in-memory glob filtering; switch implementation based on measurement |

---

## Stage 3 — Prose index + content extraction

**Goal:** Index comments, XML doc comments, Markdown, and plain-text files as prose. `mode: "prose"` and `mode: "auto"` work end to end.

### Preconditions

- S2 complete.
- `Microsoft.CodeAnalysis.CSharp` available; verify minimum version against .NET 10 compatibility matrix.

### Deliverables

- `Indexed.Extractors` project with Roslyn C# extractor + regex extractors + whole-file extractors.
- Schema version 2: `prose_fts` table added.
- Query planner for `mode: "prose"`; merge-and-dedupe logic for `mode: "auto"`.
- Response DTO fully populated with `kind` + `span`.

### Task breakdown

3.1 **Define extraction contract in `Indexed.Abstractions` (S).**
```csharp
public readonly record struct ProseSpan(
    int StartLine,
    int EndLine,
    SpanKind Kind,
    string Content);

public interface IContentExtractor
{
    IEnumerable<ProseSpan> Extract(string path, ReadOnlyMemory<byte> fileBytes);
}
```

3.2 **Scaffold `Indexed.Extractors` project and `ExtractorRegistry` (S).**
```csharp
public sealed class ExtractorRegistry
{
    public static ExtractorRegistry BuildDefault();
    public IContentExtractor? ResolveFor(string path, ReadOnlyMemory<byte> firstBytes);
}
```
Dispatch by extension first; fall back to shebang for extension-less scripts (Python / shell).

3.3 **Implement `RoslynCSharpExtractor` (L).**
Core walker:
- Parse with `CSharpSyntaxTree.ParseText(SourceText.From(bytes))`.
- Walk the tree, collect trivia lists per line range.
- For `SyntaxTrivia.Kind() == SingleLineDocumentationCommentTrivia` or `MultiLineDocumentationCommentTrivia`: parse structured XML via `trivia.GetStructure()` → `DocumentationCommentTriviaSyntax`, strip element names, preserve inner text, preserve `cref` targets as plain-text tokens, flatten `<para>` / `<list>` / `<code>` into prose.
- For `SingleLineCommentTrivia` runs not attached to a doc-comment: group contiguous `//` lines into one `LineCommentBlock` span.
- For `MultiLineCommentTrivia`: one `BlockComment` span.

XML-stripping rules (document them in XML docs on the extractor class):
- Element content is preserved; element and attribute names are discarded.
- Exception: `<see cref="Foo.Bar"/>` → emit `"Foo.Bar"` as a token.
- `<para>` acts as a paragraph break (newline).
- `<list>` and `<item>` are flattened; bullet markers dropped.
- `<code>` content is preserved verbatim — code inside docs is still searchable as prose, with the understanding that it will also be trigram-indexed if present in the file body.

3.4 **Implement regex extractors (M).**
One small base class `RegexCommentExtractor` parameterized by `LinePrefixes` (e.g., `["//"]`) and `BlockDelimiters` (e.g., `("/*","*/")`).

Subclasses:
- `CFamilyExtractor` — `//`, `/* */`.
- `FSharpExtractor` — `//`, `(* *)`.
- `HashFamilyExtractor` — `#`.
- `PowerShellExtractor` — `#`, `<# #>`.
- `SqlExtractor` — `--`, `/* */`.
- `XmlHtmlExtractor` — `<!-- -->`.

Handle BOMs by skipping them at line-0 detection. Handle CRLF, LF, and mixed. Do **not** attempt to track string-literal state — false positives ("`//`" inside a string being tagged as a comment) are acceptable noise.

3.5 **Implement whole-file extractors (S).**
`MarkdownExtractor` and `PlainTextExtractor` — each emits a single span spanning the whole file.

3.6 **Wire extension mapping in `ExtractorRegistry.BuildDefault` (S).**
Explicit extension → extractor table. Unknown extensions → no extractor (file gets code-indexed only).

3.7 **Schema v2: add `prose_fts` (S).**
Migration: detect v1, delete DB, recreate at v2. (Simplest recovery; proposal §11 already endorses blow-away rebuilds on schema change.)

3.8 **Integrate extractors into the indexer (M).**
`FullScanIndexer.ProcessFileAsync` extended:
- After raw-bytes insert into `code_fts`, dispatch to `ExtractorRegistry`.
- If extractor returns spans, delete existing `prose_fts` rows for this `file_id` and insert the new spans.
- All in the same transaction as `code_fts` + `files`.

3.9 **Implement `ProseQueryPlanner` + `ProseQueryExecutor` (M).**
```sql
SELECT file_id, start_line, end_line, kind, content, bm25(prose_fts) AS rank
  FROM prose_fts
 WHERE prose_fts MATCH ? 
   AND file_id IN (<globbed>)
 ORDER BY rank
 LIMIT ?
```
Pattern is passed to FTS5 after light normalization (strip leading operator chars that would confuse FTS5 query syntax). BM25 ranks within-table. Line/column/text come directly from the span.

3.10 **Implement merge-and-dedupe for `mode: "auto"` (M).**
- Run code and prose plans in parallel (`Task.WhenAll`).
- Emit `Match` records keyed by `(path, line)`.
- Collision rule: prose wins (richer `kind` + `span`).
- Apply `kindFilter` post-merge.
- Apply global caps.

3.11 **Extend Service endpoint handlers (S).**
`POST /search` now accepts all three `mode` values plus `kindFilter`. Update error shapes if a request is malformed.

3.12 **Update CLI (S).**
Add `--mode {auto,code,prose}` and `--kind` flags. Update output formatter to show `kind` + `span` info in non-JSON mode (e.g., `[xml-doc:lines 40-44]` prefix).

### Test coverage

- `Indexed.Extractors.Tests`:
  - **Golden-file tests per extractor.** Directory layout: `tests/Indexed.Extractors.Tests/fixtures/<lang>/input.<ext>` + `expected.json`. A test runner discovers every input file and asserts extractor output matches.
  - Roslyn: `<summary>`, `<param>`, `<remarks>`, `<returns>`, `<example>`, `<see cref/>`, `<para>`, `<list>`, `<code>`, `<paramref>`, `<typeparam>`, `<exception>`. At least two fixture files: one "simple" (trivial method), one "kitchen sink" (every element + nested paragraphs + cref targets).
  - Regex extractors: BOM, CRLF, shebang, `//` inside string (accepted as noise), unterminated block comment (treated as comment-to-EOF).
  - Whole-file: Markdown with code fences; plain text with mixed line endings.
- `Indexed.Core.Tests`:
  - Schema v2 migration from v1 (delete + recreate).
  - Indexer extracts spans and inserts them correctly.
  - Prose query end-to-end: insert files, query with stemming, verify hits.
  - Merge-and-dedupe: both-sides hits, one-side hits, `kindFilter` application.
- `Indexed.Service.Tests` extended for `mode: prose` and `mode: auto` HTTP flows.

### Exit criteria

- `idx find "lifetime" --mode prose` finds XML-doc hits in `.cs` files across the repo (not just `.md` files).
- `idx find "IndexManifest" --mode auto` returns both code hits and (if present) xml-doc hits.
- Every match in a response carries `kind`; non-`code` hits carry `span`.
- Extractor golden tests pass for every supported language.
- All S3 tests green.

### Not in this stage

- `FileSystemWatcher` (S4).
- String-literal extraction (S6).
- Tree-sitter for any language (S6).

### Risks / Deferred

| Risk | Mitigation |
|------|------------|
| Roslyn XML parser is lenient on malformed doc comments — output may drift | Lock behavior with kitchen-sink fixture + document expected tolerance in the extractor's XML docs |
| BM25 weights may over-rank short doc spans | Out-of-scope for S3 closure; logged as open question #1 and revisited after real agent traffic exists |
| `prose_fts MATCH` syntax conflicts with user-entered patterns containing operator chars | Normalize: quote the entire pattern as an FTS5 phrase by default; expose `--fts` escape hatch later |

---

## Stage 4 — Incremental indexer

**Goal:** Background updates. File-change events produce index updates within seconds. HEAD changes trigger a full rescan. `isStale` accurately reflects outstanding work.

### Preconditions

- S3 complete.

### Deliverables

- `Indexed.Watcher` project.
- Debouncing event queue.
- Periodic rescan scheduler.
- HEAD-change detection.
- Incremental indexer worker that consumes the event stream.
- Accurate `pendingFileCount` in freshness responses.

### Task breakdown

4.1 **Scaffold `Indexed.Watcher` (S).**
`RepoWatcher` class wrapping `FileSystemWatcher` rooted at repo root with `IncludeSubdirectories = true`, `NotifyFilter = LastWrite | FileName | Size | CreationTime`.

4.2 **Implement debouncing queue (M).**
```csharp
public sealed class DebouncingFileEventQueue
{
    public DebouncingFileEventQueue(TimeSpan perPathQuietWindow, TimeSpan globalCommitCadence);
    public void Enqueue(string relativePath, FileChangeKind kind);
    public IAsyncEnumerable<FileChangeBatch> DrainAsync(CancellationToken ct);
}
```
- Per-path timer reset on every new event (250 ms default).
- Coalesce per-path events (Modified × N → Modified × 1; Created + Modified → Created).
- Rename = Delete(oldPath) + Create(newPath).
- Global cadence: emit a batch at most every 500 ms.
- Hard exclude `.git/` prefix.

4.3 **Wire exclude filters (S).**
Apply the exclude-list and binary/size filters from `Indexed.Git` before enqueuing, so obviously-ignored files never enter the pipeline.

4.4 **Implement `IncrementalIndexer` worker (M).**
Single worker thread. Consumes `DebouncingFileEventQueue.DrainAsync`; for each batch runs the same per-file pipeline S2 + S3 established (read → classify → SHA check → extract → transactional replace). Maintains an atomic `pendingFileCount` counter visible to `Freshness`.

4.5 **Implement `RescanScheduler` (M).**
- Timer firing every 5 minutes (configurable).
- On fire: call `GitRepository.EnumerateFiles()`, diff against `files` table, enqueue stale entries.
- Also fires on `POST /rescan`.

4.6 **Implement `HeadChangeWatcher` (S).**
- Every scheduler tick: `git rev-parse HEAD` + check `.git/index` mtime.
- If changed since last recorded `indexed_head`: trigger a full rescan (enqueue every tracked file for re-check — the SHA check in the indexer skips unchanged content cheaply).

4.7 **Freshness accounting integration (S).**
- `pendingFileCount` = debouncer outstanding count + queue depth.
- `lastFullScanAt` = timestamp of last completed full enumeration.
- `indexedHead` from `meta`; `currentHead` from a cached `git rev-parse` (refreshed every request — cached with 1 s TTL to avoid process-spawn cost).
- `isStale = (pendingFileCount > 0) || (indexedHead != currentHead)`.

4.8 **Service lifecycle changes (S).**
- Daemon no longer rebuilds on every startup. On startup: open DB, schedule a reconciliation rescan, start the watcher.
- Idle-exit timer now considers watcher activity too.

### Test coverage

- `Indexed.Watcher.Tests`:
  - Debounce coalescing: 100 Modified events on one path → one batch entry.
  - Rename: produces Delete + Create pair with correct paths.
  - High-churn scenario: 10 000 synthetic events, verify no OOM, final state matches reference.
  - `.git/` events filtered.
- `Indexed.Core.Tests` extended:
  - `IncrementalIndexer` consumes batches and applies them idempotently.
  - `RescanScheduler` catches a file the watcher was not notified about (simulated by writing a file with `FileSystemWatcher` disabled, then firing a rescan).
  - `HeadChangeWatcher`: simulated branch switch triggers full rescan.
  - Freshness counters are accurate during active edits.
- `Indexed.Service.Tests` extended for `POST /rescan` effect and `GET /status.freshness` changes.

### Exit criteria

- Edit a single file → search finds new content within 2 s.
- Bulk-edit 100 files → all queryable within 10 s.
- Branch switch triggers full rescan; `isStale` transitions true → false as it completes.
- High-churn scenario does not drop data (verified by post-rescan reconciliation).
- All S4 tests green.

### Not in this stage

- Fancier change-detection (inotify-style hashing of ambiguous events).
- Concurrent multi-writer indexing.

### Risks / Deferred

| Risk | Mitigation |
|------|------------|
| `FileSystemWatcher` drops events under high churn | Mitigated by 5-min rescan scheduler; documented limit in status output |
| `.git/index` mtime not reliable for HEAD-change detection during rebases | Compare both mtime and `git rev-parse HEAD` output; fall back to content equality |
| Event storms from `git pull` or `git checkout` of large diffs | Debouncer batches them; rescan scheduler reconciles; test with a synthetic "10 k file modification" event storm |

---

## Stage 5 — Productionization

**Goal:** Lock in performance targets, crash recovery, documentation. Move from "works on my machine" to "ready for real agent use."

### Preconditions

- S4 complete.

### Deliverables

- Benchmark suite pinned to §14 targets, running in CI.
- Crash-recovery test coverage (kill daemon mid-transaction; verify recovery).
- Property tests for regex↔linear-scan equivalence.
- Logging improvements (sink choice finalized; log format documented).
- Graceful shutdown drains in-flight queries.
- Proposal promoted to `src/Indexed/docs/architecture/Architecture.md`.
- Agent guidance added to repo `CLAUDE.md`.

### Task breakdown

5.1 **Lock the benchmark suite (M).**
- `tests/Indexed.Benchmarks/` project uses BenchmarkDotNet for micro + a custom harness for end-to-end.
- Pinned query corpus: 50 queries spanning literal / regex / prose / auto / `kindFilter`, with expected match counts.
- Assertions: latency percentiles within §14 targets, match counts exact.
- Runs in CI nightly; not on every PR (expensive).

5.2 **Crash-recovery testing (M).**
- Harness that spawns the daemon as a subprocess, issues indexing work, kills it at deterministic points (after N inserts, during commit), restarts it, verifies no corruption.
- Covers: mid-transaction kill, power-cycle simulation (`PRAGMA synchronous=FULL` + kill), schema-mismatch detection.

5.3 **Property tests (M).**
- FsCheck generator for small corpora + random regex patterns.
- Property: for any pattern `p` and corpus `C`, `indexedSearch(p, C) ≡ linearSearch(p, C)` (same match set, ignoring order).
- Shrinking must produce readable minimal failing cases.

5.4 **Graceful shutdown polish (S).**
- On `POST /shutdown`: stop accepting new requests, let in-flight queries drain with a 5 s cap, commit or abort the current indexer transaction, close DB, release mutex, delete `daemon.json`, exit 0.
- SIGINT / Ctrl-Break maps to the same path.

5.5 **Logging finalization (S).**
- Choose library (Microsoft.Extensions.Logging or Serilog — pick during S1 as noted; lock in here).
- Log schema: JSON lines; keys `ts`, `level`, `event`, `reqId`, plus event-specific fields.
- Rotation: daily, keep last 14 files by default.
- Redact no data — queries are not sensitive on a single-user host.

5.6 **Documentation promotion (M).**
- Copy `Indexed-Architecture-Proposal.md` to `src/Indexed/docs/architecture/Architecture.md`. Strip the "Draft proposal" status; replace with "Normative architecture for Indexed."
- Update `Indexed-Implementation-Plan.md` status to "Historical — implementation complete through S5" and move it to a `history/` subdirectory if the project follows Near's archived-docs convention, or leave it in place with updated banner.
- Author `src/Indexed/docs/guides/Using-Indexed-From-An-Agent.md` — a short guide showing example HTTP calls for common agent tasks.
- Update `src/Indexed/README.md` with real usage.

5.7 **Repo-level agent guidance (S).**
- Add a short block to `CLAUDE.md` at repo root noting that `idx` is available and preferred over `rg` for full-repo searches once the daemon is warm. Include example invocations. Defer until after real-world usage confirms reliability.

### Test coverage

- Benchmark assertions (see 5.1).
- Crash-recovery integration tests (see 5.2).
- FsCheck property tests (see 5.3).
- Graceful-shutdown behavior tests.

### Exit criteria

- Benchmark suite passes against this repo on CI.
- Crash-recovery tests pass.
- All property tests pass with shrinking.
- Documentation promoted.
- `CLAUDE.md` updated (optional, evidence-gated).
- No known-broken behavior; all open defects either closed or documented with explicit deferral to S6.

### Not in this stage

- Multi-repo support.
- Any S6 item.

### Risks / Deferred

| Risk | Mitigation |
|------|------------|
| Benchmark flakiness on shared CI | Run nightly, not per-PR; tag appropriately |
| Agents over-depend on `idx` before it's hardened | Hold off on `CLAUDE.md` promotion until a week of real use validates reliability |
| Documentation drift between proposal and normative Architecture.md | Ensure 5.6 is a copy-and-update in one commit; link from the old location to the new |

---

## Stage 6 — Optional / post-v1

Handled at coarser granularity; each bullet is a potential future stage.

### 6.1 Tree-sitter extractors for problematic languages

Triggered by evidence that regex extractor false positives hurt prose recall in a specific language. Implementation: Tree-sitter grammar loaded via `TreeSitterSharp` or equivalent; produces a syntax tree; we extract comment nodes. Replaces the language's regex extractor via `ExtractorRegistry`. One language at a time.

### 6.2 String-literal extraction

Triggered by evidence of demand for stemmed search of error messages or UI copy. Requires real per-language lexers (Tree-sitter is likely the delivery vehicle from 6.1). Adds a new `string-literal` kind to `SpanKind`.

### 6.3 Lucene.NET migration

Triggered by §4.6 in the proposal: per-field relevance, highlighter needs, richer query DSL. Migration is contained in `Indexed.Core`; extractors and service surface unchanged.

### 6.4 Multi-root support

Triggered by genuine multi-repo workflows among agents in this environment. v1 keeps one daemon per repo; unification is an API-surface question (which repo does a query address?) plus a storage-layer question (separate `.db` files, or a combined one with a repo dimension?).

### 6.5 Position-aware postings (Zoekt-style)

Triggered only by performance shortfall against §14 targets on weak-trigram regex queries. Trades space for faster verification. Not a priority; noted for completeness.

### 6.6 AOT publishing for the CLI

Triggered by startup-latency frustration. The CLI is small and AOT-friendly; the Service is not (Roslyn is not yet fully AOT-safe as of this writing). CLI can be AOT-published without affecting the daemon.

## Continuous concerns

Tracked across all stages; not owned by any one of them.

### XML documentation campaign

Follow [`XML_DOCUMENTATION_STANDARD.md`](../../../XML_DOCUMENTATION_STANDARD.md). Prioritize:

1. `Indexed.Abstractions` — all public types (S1).
2. `Indexed.Core` query planner, indexer, regex-trigram planner — contract-heavy, failure-mode-heavy (S2–S3).
3. `Indexed.Extractors` — each extractor documents its stripping / false-positive behavior (S3).
4. Service endpoints — document request/response shapes and error codes (S1, extended per stage).

XML-doc coverage is a stage-close checklist item, not a separate stage.

### Benchmark watch

Every stage after S2 runs the benchmark suite locally before closing. Regressions against §14 targets block stage closure; they are either fixed or the target is renegotiated in a documented amendment to the proposal.

### Open questions tracking

Keep the open-questions list in [`Indexed-Architecture-Proposal.md` §16](./Indexed-Architecture-Proposal.md) synchronized. When a stage answers one, move the resolution into the architecture doc and strike the open question.

### Agent self-use

Once S4 is shipped and stable, Claude Code and other agents in this repo should be pointed at `idx` as the preferred full-repo search tool (S5 does this formally via `CLAUDE.md`). This is the project's own dogfooding — any friction agents encounter is a defect report.

---

## Appendix A — Directory layout at each stage

Minimal end-of-stage directory structures, to make it easy to verify "did the stage actually land the right projects."

**End of S0:**
```
src/Indexed/
    Indexed.sln
    Directory.Build.props
    README.md
    docs/
        Indexed-Architecture-Proposal.md
        Indexed-Implementation-Plan.md
    src/
        Indexed.Abstractions/
    tests/
        Indexed.Abstractions.Tests/
```

**End of S1:** adds `Indexed.Git`, `Indexed.Service`, `Indexed.Cli` + their tests.

**End of S2:** adds `Indexed.Core` + tests + `tests/Indexed.Benchmarks/`.

**End of S3:** adds `Indexed.Extractors` + tests. `Indexed.Core` gains prose planner.

**End of S4:** adds `Indexed.Watcher` + tests.

**End of S5:** no new projects. `docs/architecture/Architecture.md` appears (promoted from the proposal).

## Appendix B — Example commands per stage

For copy-paste convenience.

**S0 verify:**
```bash
dotnet build src/Indexed/Indexed.sln -c Release
dotnet test  src/Indexed/Indexed.sln -c Release
```

**S1 end-to-end:**
```bash
dotnet run --project src/Indexed/src/Indexed.Cli -- find "Indexed" --json
dotnet run --project src/Indexed/src/Indexed.Cli -- status
dotnet run --project src/Indexed/src/Indexed.Cli -- stop
```

**S2 / S3 queries:**
```bash
idx find "IndexManifest"
idx find -e "class\s+\w+Index" --regex
idx find "lifetime" --mode prose --kind xml-doc
idx find "IndexManifest" --mode auto --glob "src/**/*.cs"
```

**S4 freshness check after edit:**
```bash
# Edit a file, then:
idx status
# expect: pendingFileCount briefly > 0, then back to 0
```

**S5 benchmark:**
```bash
dotnet test src/Indexed/tests/Indexed.Benchmarks -c Release --filter "Category=benchmark"
```
