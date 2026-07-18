# Indexed — Architecture Proposal

- Created (UTC): 2026-04-15T03:17:59Z
- Repository HEAD: 9d348b9fcccba220e0b69a851c1e23129bc4ef11
- Status: **Draft proposal** for a new project under [`src/Indexed`](../). No implementation exists yet. Revised 2026-04-15 after review to adopt SQLite FTS5 as the index engine and to add a content-extraction layer so that comments and XML doc comments are indexed as prose alongside Markdown.
- Audience: Maintainers, implementers, reviewers. Also written to brief AI coding agents that will be primary consumers of the resulting service.
- Scope: The entire planned `src/Indexed` workspace — background-indexed full-text search over the working tree of a local git repository, exposed as a long-running service with an HTTP/JSON surface and a thin CLI client.

## 1. Goals and non-goals

### 1.1 Goals

1. **Millisecond-class full-text search** across every non-binary, non-git-ignored text file in the working tree of a local git repository, regardless of repository size (tens to hundreds of thousands of files).
2. **Regex, literal, and stemmed prose search** against the same corpus. Code gets trigram-indexed byte search (identifier substrings and regex); prose gets porter-stemmed word search (inflection-aware); both are reachable through one query surface.
3. **Comments and XML doc comments are first-class prose.** Prose extracted from source files is indexed alongside `docs/` content so agents can ask "which methods are documented as handling lifetimes" and get useful answers, not only "which `.md` files mention lifetimes."
4. **Agent-first ergonomics.** Primary consumers are AI coding agents in this repository. Requests and responses are deterministic structured JSON, and match the mental model of ripgrep without requiring agents to shell out.
5. **Background, eventually-consistent index.** Indexing runs asynchronously. Queries may return slightly stale results; every response states its own freshness so callers can decide whether to retry.
6. **Git-authoritative file set.** The indexed file set is exactly `git ls-files` plus untracked-but-not-ignored files, minus binaries and oversize files. Never indexes `.git/`, gitignored paths, or vendored blobs marked as binary.
7. **Crash-safe on disk.** A crash during indexing never corrupts a queryable index; at worst the in-flight work is discarded and recovered from the last durable SQLite transaction.
8. **Self-contained.** No external services. One embedded SQLite database per repo under the user profile; the daemon is a single .NET process.

### 1.2 Non-goals (for v1)

- **Cross-repository search.** One daemon indexes one repository root.
- **Semantic / vector search.** Lexical full-text only.
- **Branch/history search.** Working tree only; `git grep` already handles history.
- **Write operations.** The service only reads the working tree; it never modifies files outside its own state directory.
- **Distributed indexing, replication, multi-user access.** Strictly local, single-user, single-host.
- **Rich relevance ranking for code.** Code matches are ordered by path then line. Prose matches use FTS5 BM25 as an intra-file tiebreak, but there is no cross-corpus relevance scoring.

## 2. Consumer model

The service is built for **agentic callers first**, human callers second.

| Caller | Interface | Notes |
|--------|-----------|-------|
| Claude Code / other AI agents | `POST http://127.0.0.1:<port>/search` with JSON body | Discovered via a port-file in the per-repo state directory. No authentication — localhost-only, single-user. |
| Human on the terminal | `idx find <pattern>` CLI client | Wraps the same HTTP endpoint. Prints ripgrep-style output by default; supports `--json` to passthrough raw service response. |
| Tests / tooling | Direct library calls against `Indexed.Core` | The query engine is a pure library; the service is a thin host around it. |

An agent's ideal workflow:

1. Send a `POST /search` with a pattern, a mode, and a glob filter.
2. Inspect `freshness` in the response. If `isStale` is true and the result matters, re-query in ~200 ms or accept the stale answer.
3. Iterate cheaply — each query should cost single-digit milliseconds on a warm index.

## 3. Source-of-truth file set

### 3.1 Enumeration

The indexable set is defined by git, always:

```
A = git ls-files -z                                   # tracked files
B = git ls-files -z --others --exclude-standard       # untracked, not gitignored
FileSet = (A ∪ B)
```

Paths from git are repo-relative POSIX-style; the daemon stores them that way and converts to OS paths only when reading content.

Rationale for including untracked-but-not-ignored: code in progress — new files an agent just wrote — must be searchable before they are committed.

### 3.2 Binary and oversize filtering

A path in `FileSet` is filtered out if **any** of the following is true:

1. Size > `maxFileBytes` (default 10 MiB; configurable).
2. First 8 KiB contains a `NUL` byte (git's and ripgrep's binary heuristic).
3. `git check-attr -z binary <path>` reports `binary: set` (batched per rescan, not per-file at query time).
4. Path matches the daemon's exclude list (defaults: `**/node_modules/**`, `**/bin/**`, `**/obj/**`, `**/*.min.js`, `**/*.map`). Configurable and auditable via `GET /status`.

A file **not** in `FileSet` is never read.

### 3.3 Repo identity

Each indexed repository is identified by:

```
repoId = SHA1( abspath(repoRoot) + "\0" + firstCommitSha )
```

The first-commit SHA anchors identity across worktree moves. Index state lives at:

```
%APPDATA%\Indexed\<repoId[0:12]>\
    daemon.json         # { port, pid, startedAt, repoRoot, schemaVersion }
    index.db            # single SQLite file: schema, FTS5 tables, metadata
    logs/               # daily-rotated structured logs
```

Placing state under `%APPDATA%` matches the convention used by Near (`%APPDATA%\Near\near-state.db`) and Nmux (`%APPDATA%\Nmux\nmux-state.db`), and the SQLite file format is already familiar to the repo's tooling (`sqlite3` CLI is expected to be available per `CLAUDE.md`).

## 4. Index engine and storage — SQLite FTS5

### 4.1 Why FTS5

Indexed uses **SQLite with FTS5** as its index engine. FTS5 ships two tokenizers that cover both halves of our workload without a second dependency:

- `tokenize = 'trigram'` (SQLite ≥ 3.34, 2020) — byte trigrams, natively answering substring / `LIKE '%foo%'` queries. Ideal for code and identifier substrings.
- `tokenize = 'porter unicode61'` — Unicode word segmentation + Porter stemming. Ideal for prose.

This choice was reached by rejecting two alternatives:

- **Hand-rolled trigram index** (Google codesearch / Zoekt style). Best raw performance on code search, but a reimplementation when a suitable library exists. Rejected on the principle of preferring existing libraries.
- **Lucene.NET.** Richer analyzer ecosystem and per-field relevance, but heavier dependency, poor native fit for identifier-substring search (requires n-gram tokenizer filters that bloat the index), and operational overhead (segment files, merge policy) that FTS5 hides inside one `.db`. Remains the **documented fallback** if per-field relevance, highlighting, or language-specific stemmers become necessary.

Neither engine does full regex natively at scale; both use the same plan (trigram-narrow to candidate files, run .NET `Regex` on those). That equivalence is why the engine choice is not load-bearing for regex behavior.

### 4.2 Schema

One database per repo. Three virtual/regular tables plus metadata.

```sql
-- File table: the authoritative mapping from file_id to path + content-identity.
CREATE TABLE files (
    file_id     INTEGER PRIMARY KEY,
    path        TEXT UNIQUE NOT NULL,   -- repo-relative POSIX
    mtime_utc   INTEGER NOT NULL,
    size_bytes  INTEGER NOT NULL,
    sha256      BLOB NOT NULL,
    language    TEXT,                   -- 'csharp', 'markdown', 'python', ...
    indexed_at  INTEGER NOT NULL
);
CREATE INDEX files_path_glob ON files(path);

-- Code index: one row per file, rowid = file_id. Trigram tokenizer.
-- Contains the full raw file bytes; comments are captured here along with
-- everything else, so regex like `//\s*TODO` still finds them in context.
CREATE VIRTUAL TABLE code_fts USING fts5(
    content,
    tokenize = 'trigram'
);

-- Prose index: one row per extracted prose span. Porter+unicode61 tokenizer.
-- file_id / start_line / end_line / kind are UNINDEXED because we filter
-- them with application-level joins rather than FTS MATCH predicates.
CREATE VIRTUAL TABLE prose_fts USING fts5(
    content,
    kind         UNINDEXED,
    start_line   UNINDEXED,
    end_line     UNINDEXED,
    file_id      UNINDEXED,
    tokenize = 'porter unicode61'
);

-- Small KV for schema version, repo identity, indexed HEAD, etc.
CREATE TABLE meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
```

SQLite runs in WAL mode, unlocking concurrent reads during indexer writes.

### 4.3 Why per-span, not per-file

Indexing comments as prose breaks file-level symmetry: a single `.cs` file contributes **code content** (entire raw bytes → `code_fts`) *and* **N prose spans** (extracted comment + XML-doc blocks → `prose_fts`), with distinct tokenization per table. The unit of indexing is therefore a **span**: a contiguous byte range inside a file tagged with a kind.

Comments are **dual-indexed on purpose**:

- An agent searching literal `//\s*TODO` with a regex expects byte-accurate hits in the code surface. `code_fts` covers that.
- An agent searching `"lifetimes"` with stemming expects hits in the prose of a doc comment — "lifetime" and "lifetimes" both match. `prose_fts` covers that.
- Duplicates across the two tables are deduplicated at response-merge time (see §8.3).

### 4.4 `kind` vocabulary

| `kind` | Produced by | Where it appears |
|--------|-------------|------------------|
| `code` | Implicit — raw file bytes | `code_fts` only |
| `markdown` | Whole-file extractor | `prose_fts`; one span per `.md` / `.markdown` file |
| `plain-text` | Whole-file extractor | `prose_fts`; one span per `.txt` / `.rst` file |
| `xml-doc` | Roslyn extractor | `prose_fts`; one span per C# `///` block, tags stripped |
| `line-comment-block` | Roslyn / regex extractor | `prose_fts`; one span per contiguous run of `//` or `#` or `--` lines |
| `block-comment` | Roslyn / regex extractor | `prose_fts`; one span per `/* */`, `<# #>`, `<!-- -->`, `(* *)` |

### 4.5 Index size and cost

FTS5 trigram indexes typically land at 1.5–3× the indexed source bytes (more than a hand-rolled mmap posting store, less than an over-configured Lucene n-gram index). For this repo's size — tens of thousands of tracked files including the `lib/` snapshots — the index stays well under a gigabyte. Rebuilds from scratch are cheap (delete the `.db`; the next rescan repopulates it).

### 4.6 Fallback: Lucene.NET

If FTS5 limits become binding, the migration to Lucene.NET is contained:

- Swap `Indexed.Core`'s engine wrapper.
- `Indexed.Extractors` and its `ProseSpan` contract are unaffected.
- The HTTP/CLI contracts in `Indexed.Abstractions` are unaffected.

Clear triggers for migration would be (a) demand for per-field relevance across mixed code/prose, (b) highlighter requirements, or (c) richer query DSL. None are on the v1 radar.

## 5. Content extraction and span model

### 5.1 Extractor tiers

Extraction is dispatched by file extension (with shebang override for extension-less scripts). v1 ships three tiers:

1. **Roslyn extractor for C#** (`*.cs`). Uses `CSharpSyntaxTree` to walk:
   - `DocumentationCommentTriviaSyntax` → one `xml-doc` span per block, tag-stripped (drops `<summary>` / `<param>` / `<remarks>` / `<returns>` / `<example>` element names; preserves their inner text; preserves `cref` targets as plain-text tokens; flattens `<para>` / `<list>` / `<code>` into prose).
   - Contiguous `SingleLineCommentTrivia` runs → one `line-comment-block` span.
   - `MultiLineCommentTrivia` → one `block-comment` span.

   Roslyn is the v1 baseline for C# because [`XML_DOCUMENTATION_STANDARD.md`](https://github.com/VladimirReshetnikov/Tools/blob/main/XML_DOCUMENTATION_STANDARD.md) makes XML doc content a first-class concern, and regex cannot cleanly separate tag names from prose.

2. **Regex extractors** for the broad middle:
   - C-family (`.c`, `.cpp`, `.h`, `.hpp`, `.js`, `.ts`, `.tsx`, `.jsx`, `.go`, `.java`, `.kt`, `.swift`, `.rs`): `//`, `/* */`.
   - F# (`.fs`, `.fsi`): `//`, `(* *)`.
   - Hash-family (`.py`, `.ps1`, `.sh`, `.rb`, `.yaml`, `.toml`, `.r`): `#`. PowerShell also `<# #>` (emitted as `block-comment`).
   - SQL (`.sql`): `--`, `/* */`.
   - XML / HTML (`.xml`, `.html`, `.xhtml`, `.svg`, `.xaml`, `.csproj`, `.props`, `.targets`): `<!-- -->`.

   Regex extractors are deliberately simple and tolerate some false positives (e.g., `//` inside a string literal being captured as a comment). False positives produce noise spans, not wrong answers; precision improvements are a post-v1 concern.

3. **Whole-file prose** for files that are prose end to end:
   - `.md`, `.markdown` → one `markdown` span.
   - `.rst`, `.txt`, `.adoc` → one `plain-text` span.

Files not matching any extractor yield only a `code_fts` row — no prose extraction. This is safe: at worst those languages have weaker prose recall, which a future extractor fixes without schema changes.

### 5.2 String literals — deferred

Extracting string literals (`"..."`, `@"..."`, raw strings, backticks) is **not in v1**. Correctly tracking string state requires a per-language lexer; the gain (stemmable error messages, UI copy) does not justify the cost now. Revisit post-v1 with evidence of demand.

### 5.3 Extraction contract

```csharp
public enum SpanKind
{
    Markdown,
    PlainText,
    XmlDoc,
    LineCommentBlock,
    BlockComment
}

public readonly record struct ProseSpan(
    int StartLine,            // 1-based, inclusive
    int EndLine,              // 1-based, inclusive
    SpanKind Kind,
    string Content);          // tag-stripped prose text

public interface IContentExtractor
{
    // Returns prose spans for a file. Implementations are pure functions of
    // (path, bytes); they do not read other files or the index.
    IEnumerable<ProseSpan> Extract(string path, ReadOnlyMemory<byte> fileBytes);
}
```

`Indexed.Extractors` maintains a static registry mapping extensions → extractor. The plugin seam — letting an out-of-tree language contribute an extractor — is **not v1**; if it becomes wanted, the registration point is narrow enough to add without breaking callers.

## 6. Project layout

Mirroring repository conventions (Near, CilTools), the workspace is split into narrow projects with clear ownership. All target `net10.0-windows` and share `Directory.Build.props`.

```
src/Indexed/
    Indexed.sln
    Directory.Build.props
    README.md
    docs/
        Indexed-Architecture-Proposal.md       ← this file
        (later) architecture/                  ← normative docs once implementation lands
    src/
        Indexed.Abstractions/    Public contracts: query / result / kind / freshness DTOs
        Indexed.Core/            FTS5 wrapper, query planner, snapshot lifecycle, merge/dedupe
        Indexed.Extractors/      Per-language content extraction (Roslyn + regex)
        Indexed.Git/             git ls-files / check-attr / exclude-standard wrappers
        Indexed.Watcher/         FileSystemWatcher + debouncer + periodic rescan scheduler
        Indexed.Service/         Long-running daemon host; HTTP/JSON on localhost
        Indexed.Cli/             CLI client; daemon lifecycle (start/stop/status)
    tests/
        Indexed.Core.Tests/
        Indexed.Extractors.Tests/
        Indexed.Git.Tests/
        Indexed.Watcher.Tests/
        Indexed.Service.Tests/
```

### 6.1 Layer ownership (normative for v1)

| Layer | Project | Owns | Must NOT |
|-------|---------|------|----------|
| Public contracts | `Indexed.Abstractions` | Query / Result / Freshness DTOs, `SpanKind` enum, `QueryMode` enum, config options, error codes | Depend on other Indexed projects |
| Index engine | `Indexed.Core` | FTS5 schema management, connection pool, query planning (`auto`/`code`/`prose`), snapshot lifecycle, code↔prose merge and dedupe, BM25 tiebreaking | Know anything about a specific language or comment syntax; call `git` directly; touch `FileSystemWatcher` |
| Content extraction | `Indexed.Extractors` | Per-language extractors producing `ProseSpan` sequences; Roslyn integration for C#; regex extractors for the rest | Know about SQL, FTS5, or storage; call `git`; do I/O beyond the bytes handed to it |
| Git adapter | `Indexed.Git` | `git ls-files`, `--others --exclude-standard`, `check-attr binary`, repo-root discovery, `HEAD` probing | Know what a span, trigram, or FTS5 row is |
| Watcher | `Indexed.Watcher` | `FileSystemWatcher`, debounce / coalesce, periodic safety rescan | Touch the index directly — it raises change events |
| Service | `Indexed.Service` | Daemon bootstrap, HTTP/JSON API, port-file, lifecycle (idle-exit, graceful shutdown) | Contain query or extraction logic beyond orchestration |
| CLI | `Indexed.Cli` | Client UX, daemon auto-start, output formatting (text / JSON) | Contain any indexing logic — all searches go through the daemon |

### 6.2 Shared dependencies with other projects in the repo

- **`Near.Text`, `Near.IO`, `Near.Platform`** — *No dependency.* Indexed is a standalone product with its own release cadence, test surface, and consumer audience. Duplicating a small BOM-detection helper is cheaper than coupling two otherwise-independent workspaces. Revisit after v1 if genuine sharing opportunities appear.
- **Microsoft.CodeAnalysis.CSharp** — new dependency in `Indexed.Extractors` for the Roslyn-based C# extractor. No other project in Indexed takes this dependency.
- **Microsoft.Data.Sqlite** (recommended) or **System.Data.SQLite** — new dependency in `Indexed.Core`. Pick the same driver as Near/Nmux so operational tooling (`sqlite3` CLI, backup scripts) stays consistent.

## 7. Background indexing pipeline

```
┌──────────────┐    ┌───────────────┐    ┌──────────────────┐    ┌──────────────┐
│  git enum    │    │  filesystem   │    │  indexer worker  │    │   SQLite     │
│ (startup +   ├───▶│    watcher    ├───▶│  per-file:       ├───▶│  index.db    │
│  periodic +  │    │ (debounce 250 │    │   read → classify│    │  (WAL mode)  │
│  HEAD change)│    │  ms per path) │    │   → extract spans│    │              │
└──────────────┘    └───────────────┘    │   → transaction  │    └──────────────┘
                            ▲            └──────────────────┘            │
                            │                                            ▼
                            │            ┌──────────────┐       ┌──────────────┐
                            └────────────┤  rescan      │◀──────│    query     │
                                         │  scheduler   │       │   readers    │
                                         │ (every 5 min)│       │  (parallel)  │
                                         └──────────────┘       └──────────────┘
```

### 7.1 Stages

1. **Startup enumeration.** `Indexed.Git` lists every file; `Indexed.Core` diffs against the `files` table; only changed/new files are scheduled.
2. **Watcher.** `FileSystemWatcher` rooted at repo root, `IncludeSubdirectories = true`. Events are coalesced per path with a 250 ms quiet window. `.git/` is hard-excluded. Rename = delete + add.
3. **Indexer worker** (single thread, single SQLite writer). For each file in a batch:
   1. Read bytes (size cap, binary recheck — mtime alone is not enough if a file was truncated).
   2. Classify language from extension (+ shebang for extension-less scripts).
   3. Compute SHA-256; if unchanged from `files.sha256`, skip.
   4. Dispatch to `Indexed.Extractors` → `IEnumerable<ProseSpan>`.
   5. In **one SQLite transaction**:
      - `UPSERT` into `files`.
      - `DELETE FROM code_fts WHERE rowid = file_id; INSERT` with raw bytes.
      - `DELETE FROM prose_fts WHERE file_id = ?; INSERT` one row per span.
      - Commit.

   Batching: up to 200 files or 250 ms per transaction, whichever comes first. WAL mode lets readers proceed during commit.

4. **Rescan scheduler.** Every 5 minutes (configurable) re-runs `git ls-files` to catch events the watcher missed. Also fires on `POST /rescan`.
5. **HEAD change detection.** On every rescan, `git rev-parse HEAD` + `.git/index` mtime are checked. A HEAD change (branch switch, `git reset`, `git checkout`) forces a full rescan.

### 7.2 Debouncing policy

- Per-path debounce: 250 ms of quiet after the last event.
- Global commit cadence: at most one transaction per 500 ms.
- Batched file reads happen in parallel bounded by `min(CPU count, 8)`, since work is I/O-bound plus cheap extraction.

### 7.3 Freshness accounting

`meta` rows carry `indexed_head`, `last_full_scan_at`. The daemon maintains in-memory `pending_file_count`. Every `/search` response includes a freshness block:

```
freshness = { indexedHead, currentHead, pendingFileCount, lastFullScanAt, isStale }
```

`isStale = (pendingFileCount > 0) || (indexedHead != currentHead)`.

## 8. Query model

### 8.1 Request contract (`POST /search`)

```json
{
  "pattern": "lifetimes",
  "mode": "auto",                            // auto | code | prose
  "caseSensitive": true,                     // ignored in prose mode
  "kindFilter": ["xml-doc", "code"],         // optional; omit for no restriction
  "pathGlob": "src/**/*.cs",
  "excludeGlob": ["**/bin/**", "**/obj/**"],
  "contextBefore": 2,
  "contextAfter": 2,
  "maxMatches": 200,
  "maxMatchesPerFile": 20,
  "sortBy": "path",                          // path (default) | relevance
  "timeoutMs": 2000
}
```

- `mode`:
  - `code` — queries `code_fts` only. Trigram-narrow to candidate files, run .NET `Regex` (or byte-literal scan) on the candidates. Honors `caseSensitive`.
  - `prose` — queries `prose_fts` only. Pattern is stemmed through porter. Always case-insensitive. Results carry `kind`, `start_line`, `end_line` directly from the row.
  - `auto` (default) — runs both plans and merges. The merger emits one match per `(path, line)` pair; when both tables hit the same line, the richer prose hit wins and carries its `span` unless `mode: "code"` was explicit.
- `kindFilter` is applied post-retrieval. Omitting it matches everything. In `code` mode, only `"code"` is meaningful; in `prose` mode, only non-`code` kinds are meaningful.
- `pathGlob` / `excludeGlob` are gitignore-style globs. Applied against repo-relative POSIX paths. Filtering happens before FTS5 retrieval where possible (`file_id IN (...)`).
- `maxMatches` is hard-capped at 10 000.
- `sortBy = "relevance"` uses `bm25(prose_fts)` for prose hits and stable-by-path for code hits; `sortBy = "path"` orders by (path, line) with BM25 as an intra-line tiebreak for prose.
- `timeoutMs` bounds total query; default 2 s, hard cap 30 s.

### 8.2 Response contract

```json
{
  "freshness": {
    "indexedHead": "9d348b9f...",
    "currentHead": "9d348b9f...",
    "pendingFileCount": 0,
    "lastFullScanAt": "2026-04-15T03:10:12Z",
    "isStale": false
  },
  "matches": [
    {
      "path": "src/Indexed/src/Indexed.Core/Snapshot.cs",
      "line": 42,
      "column": 8,
      "byteOffset": 1284,
      "text": "    /// Loads the segment manifest. Returns null if not present.",
      "kind": "xml-doc",
      "span": { "startLine": 40, "endLine": 44 },
      "contextBefore": ["...", "..."],
      "contextAfter":  ["...", "..."]
    }
  ],
  "truncated": false,
  "totalMatches": 17,
  "elapsedMs": 4
}
```

- `kind` is always present. `span` is present whenever `kind != "code"`.
- `matches` ordered per `sortBy`; default `path` → (path, line).
- `truncated` reflects any cap hit (`maxMatches`, `maxMatchesPerFile`, `timeoutMs`).

### 8.3 Merge and dedupe for `mode: "auto"`

1. Run code plan and prose plan in parallel, each capped at `maxMatches`.
2. Collect matches keyed by `(path, line)`.
3. On collision, keep the prose match if it exists (it carries the richer `kind` + `span`); demote the code match.
4. Apply `kindFilter`.
5. Apply global caps.

### 8.4 Query planning details

- **Literal `code` queries.** Extract all 3-byte trigrams from the lowercase pattern; issue `code_fts MATCH '"abc" AND "bcd" AND ...'` (FTS5 trigram tokenizer accepts literal 3-char phrase queries directly). Candidate file IDs come back; read bytes; byte-literal scan with `caseSensitive` honored.
- **Regex `code` queries.** Derive a required-trigram expression from the regex AST (Russ Cox's algorithm, ~500 lines in `Indexed.Core.RegexTrigrams`); feed it to FTS5; run .NET `Regex` on the candidates. Patterns with no extractable trigrams (e.g., `.{3}`) fall back to a full scan of the globbed file set with a warning in the response.
- **Prose queries.** Pass the pattern directly to FTS5: `SELECT file_id, start_line, end_line, kind, content, bm25(prose_fts) FROM prose_fts WHERE prose_fts MATCH ? AND file_id IN (globbed)`. No file re-read — row content is authoritative for prose.

### 8.5 CLI surface

```
idx find "IndexManifest" --glob "src/**/*.cs"
idx find -e "cls\s+\w+Store" --regex
idx find --mode prose "lifetimes" --kind xml-doc
idx status                 # daemon health + freshness
idx rescan                 # force rescan
idx stop                   # graceful daemon shutdown
```

CLI auto-starts the daemon, discovers the port via `daemon.json`, and emits ripgrep-style `path:line:col:text` unless `--json` is passed. Agents should prefer the HTTP endpoint directly.

## 9. Service lifecycle

### 9.1 Daemon bootstrap

- Single-instance per `repoId` via named mutex (`Global\Indexed-<repoId[0:12]>`).
- Listens on `127.0.0.1:0` (OS-chosen ephemeral port). Writes `daemon.json` atomically (temp file + rename). Removes it on graceful shutdown.
- Stale `daemon.json` (unclean shutdown) is detected by the CLI via a `/status` probe; refused/timed-out = start a new daemon.
- Detaches from the CLI parent process (`DETACHED_PROCESS`, no console window) when auto-started.

### 9.2 Idle exit

The daemon exits after 30 minutes (configurable) of no requests **and** no pending index work. Watcher activity resets the idle timer, so a daemon attached to active development does not spin down.

### 9.3 Endpoints

| Endpoint | Purpose |
|----------|---------|
| `GET /status` | Health, freshness, daemon version, schema version, pid |
| `POST /search` | Query |
| `POST /rescan` | Force full rescan (clears queue, re-enumerates) |
| `POST /shutdown` | Graceful exit; loopback + process-token check |

No authentication beyond loopback binding plus process-token check on destructive endpoints.

## 10. Concurrency and consistency model

- **Writes are single-threaded.** One indexer worker is the sole SQLite writer. This matches WAL-mode's single-writer model and avoids lock contention entirely.
- **Reads are unbounded.** WAL-mode readers proceed concurrently with the writer; every `/search` opens a short-lived read transaction that sees a consistent snapshot.
- **Eventual consistency is explicit.** `freshness.isStale` tells callers whether to retry. There is no query-time wait for indexing to catch up.
- **`POST /rescan` enqueues behind in-flight events**, so a rescan never skips a change the watcher already observed.

## 11. Failure modes and recovery

| Failure | Detection | Recovery |
|---------|-----------|----------|
| Database corruption | `PRAGMA integrity_check` at startup | Delete `index.db`; trigger full rescan |
| Schema version mismatch | `meta.schema_version` ≠ current | Delete `index.db`; full rescan |
| Crash mid-indexing | SQLite WAL checkpoint on next open | Last committed transaction is durable; the in-flight file is re-enqueued by the startup rescan |
| Watcher dropped events (high churn) | Periodic 5-min rescan observes divergence | Affected files re-enqueued; `isStale` remains true until caught up |
| HEAD change (branch switch, `reset`) | `git rev-parse HEAD` on rescan | Full rescan |
| `git` binary missing | Startup probe | Daemon fails fast; no partial index |
| Disk full during commit | `SqliteException` during transaction | Abort commit; previous state preserved; surface via `/status` |
| Repo root moved | `repoId` mismatch | Treat as new repo; old state retained at old directory |

No orphan temp-segment files to manage — SQLite owns durability.

## 12. Security and scope

- **Localhost-only.** Listener binds `127.0.0.1`.
- **Single-user.** No authentication beyond loopback + process-token for destructive endpoints.
- **Path containment.** Daemon refuses to read anything outside the repo root it was started against. Symlinks pointing outside are not followed (target-resolution prefix check).
- **No outbound network.**
- **No write access to the repo tree.** Files are opened read-only. Writable state lives under `%APPDATA%\Indexed\<repoId>\`.

## 13. Testing strategy

Testing mirrors the layered split.

| Test project | Coverage |
|--------------|----------|
| `Indexed.Core.Tests` | Schema migration, query planning (`code`/`prose`/`auto`), regex→trigram planner against a pattern corpus, merge/dedupe semantics, BM25 tiebreak, FTS5 wrapper lifecycle, freshness accounting |
| `Indexed.Extractors.Tests` | **Golden-file tests per language.** Each supported extension has a checked-in input file + expected `ProseSpan[]`. Roslyn extractor: `<summary>` / `<param>` / `<remarks>` / `<returns>` / `<example>` / `<see cref/>` / `<para>` / `<list>` / `<code>` stripping; XML-doc block contiguity; tag-stripped content preservation. Regex extractors: BOM handling, shebang lines, `//` inside string literals (accepted as noise), CRLF vs. LF |
| `Indexed.Git.Tests` | File-set enumeration against synthetic on-disk repos (tracked / untracked / ignored / binary), rename and delete handling, HEAD-change detection |
| `Indexed.Watcher.Tests` | Debounce coalescing under burst writes, rename = delete + add, periodic rescan catching missed events |
| `Indexed.Service.Tests` | HTTP contract — request/response shapes; `mode` / `kindFilter` through the HTTP surface; error codes; freshness accuracy; shutdown token check; port-file atomic write |

**Property tests** (FsCheck) for regex-trigram equivalence on small corpora: for a random pattern and a random small corpus, the indexed answer must match the linear-scan answer.

**Integration / benchmark test.** Spin up the daemon in-process against `C:\Tools2\Tools` itself (read-only); run a pre-recorded query suite including prose queries like `xml-doc` matches for `"lifetime"`; assert expected counts and a latency envelope. Doubles as a regression benchmark — refactors that double latency fail the test.

## 14. Performance targets

Calibrated against this repository on a warm daemon:

| Workload | Target |
|----------|--------|
| Literal identifier query returning < 100 matches (`code`) | ≤ 10 ms |
| Regex with strong literal anchor, e.g. `class\s+FooBar` (`code`) | ≤ 30 ms |
| Regex with weak trigrams, e.g. `..foo..` (`code`) | ≤ 250 ms (globbed set) |
| Prose query, e.g. `"lifetime"` (`prose`) | ≤ 15 ms (no file re-read) |
| Mixed `auto` query | ≤ max of the two underlying plans + merge overhead (~2 ms) |
| Cold startup → first query (warm `index.db`) | ≤ 2 s |
| Cold rebuild (no `index.db`) | ≤ 60 s for this repo |
| Index size (`index.db`) | ≤ 3× indexed source bytes |
| Watcher → queryable | ≤ 2 s single-file edit; ≤ 10 s 100-file bulk edit |

Targets are pinned by the integration benchmark in §13.

## 15. Staging / delivery plan

The proposal is sized for incremental delivery. Each stage ends with a runnable daemon + CLI.

1. **S1 — Enumeration + CLI shim.** `Indexed.Git`, `Indexed.Service`, `Indexed.Cli` with a ripgrep-fallback implementation of `/search`. No FTS5 yet. Locks the HTTP contract, CLI ergonomics, freshness DTO. Already useful to agents as a uniform JSON endpoint.
2. **S2 — FTS5 code index.** `Indexed.Core` with `code_fts` (trigram) table + `files` + `meta`. Full-scan indexer on startup, no watcher. Supports `mode: "code"` only. Proves the FTS5 wrapper and query planner.
3. **S3 — Prose index + extraction.** `Indexed.Extractors` (Roslyn C# + regex extractors) and `prose_fts` table. `mode: "prose"` and `mode: "auto"` added; response `kind` / `span` populated.
4. **S4 — Incremental indexer.** `Indexed.Watcher`, per-file transactional updates, periodic rescan, HEAD-change rescan. The background-update promise materializes here.
5. **S5 — Productionization.** Idle exit, crash-recovery polish, benchmark suite, integration tests, first real agent usage. Documentation pass that promotes this proposal to `src/Indexed/docs/architecture/Architecture.md`.
6. **S6 (optional) — Richer extractors.** Tree-sitter bindings or language-specific lexers for languages where regex false positives bite. String-literal extraction if evidence of demand appears. Lucene.NET migration if §4.6 triggers fire.

## 16. Open questions

Deliberately unresolved; decided during implementation.

1. **BM25 tuning.** FTS5 default BM25 weights may over-rank short doc spans. Tune `bm25(prose_fts, k1, b)` once real agent queries exist.
2. **`kindFilter` default.** Should `mode: "auto"` default to all kinds, or exclude `line-comment-block` / `block-comment` (often noisy TODOs)? Start with all kinds; revisit on feedback.
3. **`see cref` targets.** Currently preserved as plain-text tokens. Option to expose them as a structured sub-field later (e.g., `xrefFilter: "System.IDisposable"`).
4. **Roslyn workspace vs. standalone trees.** v1 uses `CSharpSyntaxTree.ParseText` per file. Using `MSBuildWorkspace` would enable symbol-level extraction (method → its XML doc) but re-introduces a heavy dependency (MSBuild discovery, targets). Defer to S6.
5. **Multi-root support.** Defer.
6. **String literal extraction.** S6, evidence-gated.
7. **Content-less FTS5 vs. content-embedded.** v1 embeds content in FTS5 rows. Content-less with external-content mode saves ~30 % space but complicates reads; revisit if index size becomes painful.
8. **Extractor plugin loading.** Static registration for v1. DI/plugin discovery only if out-of-tree extractors become a real demand.

## 17. What this proposal does not prescribe

To keep the proposal actionable without over-specifying:

- **HTTP framework.** `HttpListener`, Kestrel minimal APIs, or a hand-rolled loopback listener.
- **SQLite driver.** `Microsoft.Data.Sqlite` is recommended for consistency with Near/Nmux; `System.Data.SQLite` is acceptable. Both support FTS5 with the trigram and porter tokenizers.
- **JSON serializer.** Default: `System.Text.Json` with source generators for `Indexed.Abstractions` DTOs.
- **Logging sink.** Structured logs to `%APPDATA%\Indexed\<repoId>\logs\` with daily rotation. Library choice is open.
- **AOT publishing** for the CLI. Desirable for startup latency; deferred until S5 if it conflicts with other priorities.

---

*This proposal is intended to be promoted to a normative architecture document under `src/Indexed/docs/architecture/Architecture.md` once S3 ships. Until then it describes target state and is binding only on the directional choices called out as "normative" in section 6.1.*
