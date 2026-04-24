# Indexed Stage 3 — Prose Indexing and Truthful `auto` Mode Implementation Plan

- Created (UTC): 2026-04-24T00:47:38Z
- Repository HEAD: bc00dc1f7bd053d42c86f65066027fc487b1130a
- Status: Implemented plan with recorded outcome
- Audience: Maintainers, reviewers, implementers
- Scope: Complete Stage 3 for `src/Indexed` using the current Stage 4/5 codebase as the baseline

## 1. Summary

This plan delivers the missing Stage 3 feature set in the current Indexed
codebase:

- extractor-backed prose indexing;
- operational `QueryMode.Prose`;
- truthful `QueryMode.Auto` that executes both code and prose search;
- real `SortBy.Relevance` behavior for prose-bearing queries;
- end-to-end propagation of `SpanKind` and `MatchSpan`;
- current-state documentation and tests that describe the runtime truth rather
  than the earlier staged placeholder behavior.

The work is intentionally framed against the code that exists today, not
against the historical pre-target implementation plan. The current system
already has:

- a schema v3 database containing a dormant `prose_fts` table;
- target-aware indexing and content rehydration from disk;
- service-side request validation and concurrency controls;
- CLI and DTO shapes that already mention prose and `auto`.

Stage 3 therefore does **not** need a product redesign. It needs the missing
extractor, indexing, and query machinery that turns dormant contract surface
into real behavior.

## 1.1 Implementation outcome

The implementation followed the plan with a few clarifying decisions that are
worth recording here for maintainers:

- `SqliteSchema.Version` stayed at `3`; Stage 3 reused the already-present
  `prose_fts` table rather than forcing a rebuild-only schema bump.
- Prose query syntax remained raw FTS5 MATCH syntax. Invalid expressions are
  now normalized into `IndexedErrorCode.PatternInvalid` instead of leaking raw
  SQLite failures.
- `Match.ByteOffset` is now truthful for code hits (UTF-8 byte offset within
  the decoded working-tree content) and explicitly span-surface-relative for
  prose hits.
- `QueryMode.Auto` is no longer a code alias. It runs both sides when both can
  contribute, skips prose for regex queries, and prefers prose on exact
  same-line collisions.
- The extractor suite landed in its own `Indexed.Extractors` project, with a
  dedicated `Indexed.Extractors.Tests` project to pin normalization behavior.

## 2. Scope and Non-Goals

### In scope

- new extractor layer and default extractor registry;
- prose-span persistence into the existing `prose_fts` table;
- prose search execution and `auto` merge behavior;
- BM25-backed relevance ordering where prose hits participate;
- CLI text formatting for prose hits;
- contract, architecture, usage, and tutorial updates;
- broad automated coverage across extraction, storage, query, HTTP, and CLI.

### Explicit non-goals

- semantic code navigation, symbol search, xref, or vector search;
- multiline regex for code search;
- new ignore-file formats such as `.indexedignore`;
- cross-daemon or cross-target federated search;
- tree-sitter-backed language parsing;
- cross-platform porting.

## 3. Current-State Constraints

The current implementation introduces several useful constraints that shape the
plan:

1. `code_fts` is contentless, so code search already rehydrates source text
   from disk at query time.
2. `prose_fts` already exists in schema v3 with the columns needed for a first
   usable Stage 3 cut:
   `content`, `kind`, `start_line`, `end_line`, and `file_id`.
3. `QueryMode.Auto` is currently a code-only alias and `QueryMode.Prose`
   returns `NotImplemented`; the DTOs and docs already acknowledge this staged
   truth.
4. `Match` requires `Path`, `Line`, `Column`, `ByteOffset`, `Text`, `Kind`,
   optional `Span`, and context lines. Prose execution therefore needs a
   deterministic strategy for producing location and snippet data, not merely
   span-level row IDs.
5. Directory targets and git targets share the same indexing pipeline, so the
   extractor layer must be target-agnostic and purely file/content-based.

## 4. Design Decisions

### 4.1 Reuse schema v3 instead of bumping the database version

No schema bump is planned for this stage.

Reasoning:

- `prose_fts` is already present in `SqliteSchema.Ddl`.
- delete/update paths already clear `prose_fts` rows by `file_id`.
- the missing behavior is population and querying, not storage shape.

Consequence:

- cold rebuilds are **not** required solely because Stage 3 lands;
- existing daemons that reopen an empty `prose_fts` simply start filling it on
  the next full scan or incremental update.

### 4.2 Add a dedicated `Indexed.Extractors` project

The extractor layer should live outside `Indexed.Core`.

Reasoning:

- extractors are language/content aware, while `Indexed.Core` should remain the
  storage/query engine;
- the architecture proposal already drew this boundary;
- it keeps Roslyn and language-specific parsing dependencies out of the query
  engine itself.

Planned references:

- `Indexed.Extractors` references `Indexed.Abstractions` for `SpanKind`;
- `Indexed.Core` references `Indexed.Extractors`.

### 4.3 Preserve line correspondence in extracted prose content

Extracted prose content should preserve line structure relative to the source
span so that query-time highlighting can recover a stable match line inside the
span.

Reasoning:

- current `Match` requires line-oriented output;
- we do not want to read source files and re-run language-specific parsing at
  query time;
- `highlight(prose_fts, ...)` becomes much more useful if highlighted text can
  be mapped back to `start_line + relativeLine`.

Implementation implication:

- extractors normalize text by removing markers/tags while preserving line
  boundaries where possible instead of collapsing everything into one paragraph.

### 4.4 Use FTS5 highlighting for prose match localization

Prose query execution will use SQLite FTS5 `highlight(...)` over the stored
`content` column to locate a representative hit inside the extracted span.

Reasoning:

- porter stemming means a raw string search over the original pattern is not
  enough to find the actual surface token that matched;
- the stored content is already the canonical prose surface;
- highlighting avoids implementing a second tokenizer/stemmer purely for
  location recovery.

Consequence:

- prose `Column` and `ByteOffset` are derived from the extracted prose surface,
  not from byte positions in the original source file;
- docs must state that truth explicitly.

### 4.5 Keep prose pattern semantics as FTS5 query syntax for this cut

`SearchRequest.Pattern` in prose mode will continue to be interpreted as an
FTS5 match expression.

Reasoning:

- that is already the documented request contract;
- it avoids silent compatibility changes for direct HTTP callers;
- it keeps Stage 3 focused on implementing the missing feature rather than
  revising the query language.

Mitigation:

- syntax errors from SQLite FTS5 are surfaced as `IndexedErrorCode.PatternInvalid`.
- documentation should continue to recommend simple term/phrase inputs for most
  callers.

### 4.6 `SortBy.Relevance` becomes real for prose-bearing queries and degrades
### cleanly for code-only results

Planned behavior:

- `Mode=Prose`: sort by BM25 descending, then stable path/line/column.
- `Mode=Auto`: sort prose hits by BM25 first, then code hits by stable
  path/line/column, unless `SortBy.Path` was requested.
- `Mode=Code`: accept `SortBy.Relevance`, but treat it as the stable path order
  because code mode has no BM25 surface.

Reasoning:

- rejecting `SortBy.Relevance` once prose exists would be overly coarse;
- code mode still needs deterministic output even when the caller asks for a
  relevance sort that only prose can provide.

### 4.7 `auto` mode merges code and prose hits additively, with prose-preferred
### deduplication on exact path/line collisions

Planned merge rule:

- execute both plans in parallel unless `KindFilter` proves one side cannot
  contribute;
- if a code hit and a prose hit land on the same `(path, line)`, keep the
  prose hit;
- otherwise preserve both.

Reasoning:

- path/line collision is the practical overlap case the original design cared
  about;
- prose hits carry richer semantics (`Kind`, `Span`, extracted text);
- this keeps the first Stage 3 merge simple and deterministic.

Known limitation:

- path/line dedupe can collapse a code hit and a prose hit that are not
  semantically identical but happen to land on the same source line.
- that is acceptable for this cut and should be documented as a current
  implementation detail, not a long-term guarantee.

## 5. Planned Workstreams

## 5.1 Extractor Project and Contracts

Create `src/Indexed/src/Indexed.Extractors/` with:

- `ExtractedProseSpan` record:
  - `StartLine`
  - `EndLine`
  - `SpanKind Kind`
  - `string Content`
- `IContentExtractor`
- `ExtractorRegistry`
- extractor implementations:
  - `MarkdownExtractor`
  - `PlainTextExtractor`
  - `RoslynCSharpExtractor`
  - `CFamilyRegexCommentExtractor`
  - `FSharpRegexCommentExtractor`
  - `HashLineCommentExtractor`
  - `PowerShellCommentExtractor`
  - `SqlCommentExtractor`
  - `XmlHtmlCommentExtractor`

Planned dependency:

- `Microsoft.CodeAnalysis.CSharp` at the repo-already-used `4.14.0` line.

Implementation notes:

- registry dispatch should be extension-first and explicit;
- unknown extensions simply produce no prose spans;
- C# uses Roslyn for comment-trivia identification, but line-preserving
  normalization still happens over the original comment text so source-line
  correspondence remains usable.

## 5.2 Line-Preserving Normalization Rules

Normalization is where most Stage 3 correctness risk lives.

Planned rules:

- Markdown and plain text:
  - preserve content verbatim;
  - whole file becomes one span.
- Single-line comments:
  - remove the language-specific prefix and one optional following space;
  - keep one extracted line per source line.
- Block comments:
  - strip opening/closing delimiters;
  - keep internal line structure;
  - trim one leading `*` on Javadoc-style interior lines when present.
- C# XML doc comments:
  - use Roslyn trivia boundaries to identify blocks;
  - strip `///` or doc-comment framing first;
  - replace inline tags with text when they carry useful tokens:
    - `<see cref="X.Y"/>` => `X.Y`
    - `<seealso cref="X.Y"/>` => `X.Y`
    - `<paramref name="arg"/>` => `arg`
    - `<typeparamref name="T"/>` => `T`
  - strip remaining tags but preserve inner text and line structure.

This is intentionally pragmatic rather than perfect XML reconstruction. The
goal is reliable search value with stable line mapping, not a full-fidelity doc
renderer.

## 5.3 Storage Integration

Add to `SqliteIndex`:

- `ReplaceProseSpans(WriterScope scope, long fileId, IReadOnlyList<ExtractedProseSpan> spans)`
- `QueryProseMatchesAsync(...)` or equivalent reader API returning:
  - `file_id`
  - `logical_path`
  - `kind`
  - `start_line`
  - `end_line`
  - `content`
  - `highlighted`
  - `rank`

Indexing integration points:

- `FullScanIndexer`:
  - after `UpsertFile`, extract prose spans from decoded content and replace
    stored `prose_fts` rows in the same transaction.
- `IncrementalIndexer`:
  - same behavior on changed files;
  - deletes already clear `prose_fts` via existing code paths.

This stage should not introduce a separate prose-only transaction boundary.
Code and prose updates for the same file belong in one writer scope.

## 5.4 Prose Query Execution

Add `ProseQueryExecutor` and supporting helpers.

Execution flow:

1. Validate mode-specific request rules.
2. Query `prose_fts MATCH $pattern`.
3. Join to `files.logical_path`.
4. Apply `PathGlob`, `ExcludeGlob`, and `KindFilter`.
5. Use `highlight(prose_fts, 0, startMarker, endMarker)` to find a
   representative matched line within the extracted prose content.
6. Project `Match`:
   - `Path`: logical path
   - `Line`: `start_line + relativeLine`
   - `Column`: 1-based column in extracted prose line
   - `ByteOffset`: UTF-8 byte offset within extracted prose content
   - `Text`: extracted prose line
   - `Kind`: row kind
   - `Span`: `(start_line, end_line)`
   - `ContextBefore/After`: neighboring extracted prose lines
7. Respect `MaxMatchesPerFile` and `MaxMatches`.

Planned error handling:

- malformed FTS5 expressions map to `PatternInvalid`;
- `CaseSensitive` is accepted but ignored in prose mode;
- `IsRegex` is accepted but ignored in prose mode.

## 5.5 Truthful `auto` Mode

Backend behavior changes:

- `Auto` is no longer an alias for `Code`.
- `SearchAsync` executes both sides in parallel unless filters make one side
  impossible.
- merge helper deduplicates exact path/line collisions in favor of prose hits.

Filter-aware shortcuts:

- if `KindFilter` is prose-only, skip the code plan;
- if `KindFilter` is `{ Code }`, skip the prose plan;
- if `Mode=Auto` and `SortBy=Relevance`, prose hits sort ahead of code hits,
  code hits retain stable path ordering.

## 5.6 CLI and DTO Cleanup

Update:

- `QueryMode` XML docs from staged placeholders to current truth;
- `SearchRequest` and `SearchResponse` docs to describe actual prose behavior;
- `Match` remarks to explain code-hit vs prose-hit `Text`, `Column`, and
  `ByteOffset` semantics honestly;
- `OutputFormatter.WriteSearchText` so prose hits show their semantic surface,
  for example:
  `path:line:col:[xml-doc lines 10-14] extracted text`.

The CLI text path should remain compact and grep-like; the JSON response
remains the canonical contract.

## 5.7 Test Strategy

### Extractor coverage

Add `Indexed.Extractors.Tests` with focused fixture-style tests for:

- Markdown whole-file extraction
- plain-text whole-file extraction
- C# XML docs
- C# line comments
- C# block comments
- PowerShell `#` and `<# #>`
- SQL `--` and `/* */`
- HTML/XML `<!-- -->`
- false-positive tolerance on comment markers inside string literals where the
  regex extractors are intentionally approximate

### Core coverage

Expand `Indexed.Core.Tests` for:

- prose row replacement in `SqliteIndex`
- full-scan population of `prose_fts`
- incremental updates refreshing prose rows
- prose query execution end to end
- `auto` merge/dedupe semantics
- `SortBy.Relevance` behavior
- `KindFilter` shortcuts in prose and auto modes

### Service and CLI coverage

Update:

- `Indexed.Service.Tests` for `Mode=Prose`, `Mode=Auto`, and relevance sort
- `Indexed.Cli.Tests` for prose text formatting
- `Indexed.Abstractions.Tests` for any wire-shape changes or doc-truth comments

## 6. Planned File/Subsystem Impact

This stage is expected to touch:

- one new source project;
- one new test project;
- roughly 20-35 production files;
- roughly 15-30 test files;
- `Indexed.sln`, project references, and project docs.

Most affected subsystems:

- `Indexed.Extractors`
- `Indexed.Core`
- `Indexed.Service`
- `Indexed.Cli`
- `Indexed.Abstractions`
- `src/Indexed/docs/`

## 7. Validation Plan

Required validation steps:

1. `dotnet build src/Indexed/Indexed.sln --nologo -v minimal`
2. `dotnet test src/Indexed/Indexed.sln --nologo -v minimal`
3. Manual spot checks through the CLI against the Indexed repo itself:
   - `idx find "lifetime" --mode prose`
   - `idx find "TargetId" --mode auto`
   - `idx find "summary" --mode prose --kind xml-doc --json`

Success criteria:

- prose mode returns real hits instead of `NotImplemented`;
- auto mode returns the union of code and prose hits with deterministic order;
- relevance sort is accepted and useful for prose-bearing queries;
- index rebuild and incremental update tests remain green;
- docs and DTO comments no longer describe stale staged behavior as current.

## 8. Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Roslyn-based XML doc extraction becomes line-unstable if we over-normalize | Preserve source line boundaries and keep normalization deliberately conservative |
| FTS5 highlight output does not mark some complex boolean query shapes as usefully as expected | Treat the first highlighted token as a representative location and document that prose location is span-relative, not a full token-offset contract |
| `auto` merge semantics hide a code hit and a prose hit on the same line | Keep the rule explicit, test it, and document it as a current implementation detail |
| Regex-based extractors create some false-positive comment spans | Accept this as Stage 3 noise; reserve parser-specific tightening for a later phase |
| Existing docs claim Stage 3 is absent | Update the current-state docs in the same change so users do not read stale architecture fiction |

## 9. Exit Condition

This stage is complete when all of the following are true:

- `QueryMode.Prose` is fully operational across CLI, service, and core;
- `QueryMode.Auto` no longer behaves as a code-only alias;
- indexed prose spans are stored and refreshed continuously;
- `SortBy.Relevance` is implemented for prose-bearing queries;
- prose hits carry stable `Kind` and `Span` data and useful text/context;
- the test suite covers extraction, storage, query execution, and contract
  shape;
- the Indexed current-state docs describe Stage 3 as implemented.
