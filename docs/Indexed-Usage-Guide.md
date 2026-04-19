# Indexed — Usage Guide

- Created (UTC): 2026-04-15T17:00:00Z
- Repository HEAD: cd463ca87356b067e49fe274a1ebcb6e92376c1d

## 1. Overview

Indexed provides full-text code search via two interfaces:

- **`idx` CLI** — Human-friendly terminal tool with ripgrep-style output.
- **HTTP/JSON API** — Agent-friendly programmatic interface on localhost.

The daemon starts automatically on first CLI use and shuts down after 30 minutes of inactivity.

## 1.1 Prerequisites

- **Windows**: all projects target `net10.0-windows`.
- **.NET 10**:
  - To **run** a published `idx.exe`, install the .NET 10 runtime.
  - To **build from source**, install the .NET 10 SDK.
- **Git**: `git.exe` must be on `PATH` for repository discovery and enumeration.

## 1.2 Installing / getting `idx` on PATH

### Option A: Run from source (this repo)

From `src/Indexed/`, you can run the CLI via `dotnet run`:

```bash
cd src/Indexed
dotnet run --project src/Indexed.Cli -- status
dotnet run --project src/Indexed.Cli -- find "SearchRequest" --glob "src/**/*.cs"
```

### Option B: Use build outputs (this repo)

Build once, then run the built executable directly:

```bash
cd src/Indexed
dotnet build -c Release
.\src\Indexed.Cli\bin\Release\net10.0-windows\idx.exe status
```

This works as long as `idx.exe` remains inside the repo checkout: the launcher
can locate `Indexed.Service.exe` by walking the build tree.

### Option C: Publish for use in other repositories

To use Indexed in arbitrary repositories (not just inside this checkout),
publish **both** the CLI and the daemon into the same directory and add it to
`PATH`:

```bash
cd src/Indexed
$dest = "$env:LOCALAPPDATA\\Indexed\\bin"
dotnet publish src/Indexed.Cli -c Release -o $dest
dotnet publish src/Indexed.Service -c Release -o $dest
```

Now `idx` can be invoked from any git repo, and it will discover and launch the
side-by-side `Indexed.Service.exe` as needed.

## 1.3 Daemon executable discovery (`Indexed.Service.exe`)

The CLI must be able to locate `Indexed.Service.exe` in order to start the
daemon. Resolution order is:

1. `INDEXED_SERVICE_EXE` environment variable (explicit override).
2. `Indexed.Service.exe` next to `idx.exe` (publish / install layout).
3. A best-effort walk of the build tree (works when running inside this repo).

If you see an error like “Could not locate Indexed.Service.exe”, either
re-run after `dotnet build`, set `INDEXED_SERVICE_EXE`, or publish both
executables side-by-side (Option C above).

## 2. CLI reference (`idx`)

### 2.1 Syntax

```
idx find <pattern> [options]
idx status [--json] [--repo-root <dir>]
idx rescan [--repo-root <dir>]
idx stop [--repo-root <dir>]
idx --help
```

**Daemon launch options (any command):**

These options can be passed to **any** verb. They only take effect when that
invocation launches a new daemon; if an existing daemon is adopted via
`daemon.json`, the already-running daemon's settings remain in effect.

- `--exclude-index <glob>` (repeatable)
- `--no-default-excludes`
- `--idle-timeout-seconds <n>`

### 2.2 `idx find`

Search the indexed repository for a pattern.

```
idx find <pattern> [--mode auto|code|prose]
                   [--regex | -e]
                   [--case-sensitive | -s]
                   [--glob | -g <glob>]
                   [--exclude <glob>]*
                   [--exclude-index <glob>]*
                   [--no-default-excludes]
                   [--kind <kind>]*
                   [--context-before | -B <n>]
                   [--context-after  | -A <n>]
                   [--context | -C <n>]
                   [--max-matches <n>]
                   [--max-matches-per-file <n>]
                   [--json]
                   [--repo-root <dir>]
```

**Options:**

| Option | Default | Description |
|--------|---------|-------------|
| `--mode` | `auto` | Query mode: `auto`, `code`, or `prose`. Code uses trigram narrowing; prose uses porter stemming (Stage 3). |
| `--regex`, `-e` | off | Treat pattern as a .NET regular expression. |
| `--case-sensitive`, `-s` | off | Case-sensitive matching. Ignored in prose mode. |
| `--glob`, `-g` | none | Restrict search to files matching this glob (e.g., `src/**/*.cs`). |
| `--exclude` | none | Exclude files matching this glob from query results. Repeatable. |
| `--exclude-index` | none | Exclude files from indexing entirely. Repeatable. Passed to the daemon at launch. |
| `--no-default-excludes` | off | Do not apply the built-in default exclude list (lockfiles, minified bundles, generated C#). See §5.2 for the full list. |
| `--kind` | all | Filter results by span kind: `code`, `markdown`, `plain-text`, `xml-doc`, `line-comment-block`, `block-comment`. Repeatable. |
| `-B`, `--context-before` | 0 | Lines of context before each match. |
| `-A`, `--context-after` | 0 | Lines of context after each match. |
| `-C`, `--context` | 0 | Symmetric context (sets both before and after). |
| `--max-matches` | 200 | Global maximum number of matches returned. Hard cap: 10,000. |
| `--max-matches-per-file` | 20 | Maximum matches per file. |
| `--json` | off | Emit raw JSON `SearchResponse` instead of text output. |
| `--repo-root` | cwd | Override repository root detection. |
| `--idle-timeout-seconds` | (daemon default) | Override daemon idle-exit window (seconds). Applies only when this invocation launches a new daemon. |

**Examples:**

```bash
# Literal search
idx find "SearchRequest"

# Literal search with file glob
idx find "SqliteIndex" --glob "src/**/*.cs"

# Regex search
idx find -e "class\s+\w+Index" --case-sensitive

# Search with context lines
idx find "Dispose" -C 3

# JSON output for agent consumption
idx find "BuildFreshness" --json

# Search excluding test files
idx find "RunAsync" --exclude "**/tests/**"
```

**Output format (text mode):**

```
src/Indexed.Core/SqliteIndex.cs:42:8:    public static SqliteIndex OpenOrCreate(string dbPath)
src/Indexed.Core/SqliteIndex.cs:150:12:    public async ValueTask DisposeAsync()
```

Format: `path:line:column:text`. Context lines use `-` as the separator character.

**Exit codes:**

| Code | Meaning |
|------|---------|
| 0 | Success; at least one match found (find) or clean completion (status/rescan/stop) |
| 1 | Successful request but zero matches (find only) |
| 2 | Argument parse error or help displayed |
| 3 | Daemon returned an error response |
| 4 | Transport or daemon launch failure |

### 2.3 `idx status`

Display daemon health and index freshness.

```bash
idx status
idx status --json
```

**Text output:**

```
Indexed daemon v0.1.0  pid=12345
  repo:    C:\Tools2\Tools
  repoId:  a1b2c3d4e5f6
  schema:  2
  started: 2026-04-15T10:00:00Z
  head:    abc123def456... (indexed: abc123def456...)
  stale:   no
  pending: 0 files
  last scan: 2026-04-15T10:00:12Z
```

### 2.4 `idx rescan`

Trigger a reconciliation rescan. The daemon compares the git file set against the index and enqueues corrective events for any discrepancies. Returns immediately; the rescan runs asynchronously.

```bash
idx rescan
```

### 2.5 `idx stop`

Gracefully shut down the daemon. The daemon drains in-flight work, checkpoints the WAL, deletes `daemon.json`, and exits.

```bash
idx stop
```

## 3. HTTP API reference

The daemon listens on `http://127.0.0.1:<port>/` where `<port>` is an OS-assigned ephemeral port written to `daemon.json`.

### 3.1 `GET /status`

Returns daemon health and freshness metadata.

**Response (200):**

```json
{
  "daemonVersion": "0.1.0",
  "schemaVersion": 2,
  "pid": 12345,
  "repoRoot": "C:\\Tools2\\Tools",
  "repoId": "a1b2c3d4e5f6",
  "startedAt": "2026-04-15T10:00:00Z",
  "freshness": {
    "indexedHead": "abc123def456...",
    "currentHead": "abc123def456...",
    "pendingFileCount": 0,
    "lastFullScanAt": "2026-04-15T10:00:12Z",
    "isStale": false
  }
}
```

### 3.2 `POST /search`

Execute a search query.

**Request body:**

```json
{
  "pattern": "SqliteIndex",
  "mode": "code",
  "isRegex": false,
  "caseSensitive": false,
  "pathGlob": "src/**/*.cs",
  "excludeGlob": ["**/tests/**"],
  "contextBefore": 2,
  "contextAfter": 2,
  "maxMatches": 200,
  "maxMatchesPerFile": 20,
  "timeoutMs": 2000
}
```

All fields except `pattern` are optional:

| Field | Default | Description |
|-------|---------|-------------|
| `pattern` | (required) | Search pattern, literal or regex |
| `mode` | `"auto"` | `"auto"`, `"code"`, or `"prose"` |
| `isRegex` | `false` | Treat pattern as .NET regex |
| `caseSensitive` | `false` | Case-sensitive matching |
| `kindFilter` | (all) | Array of `SpanKind` values to include |
| `pathGlob` | (all files) | Glob to restrict file paths |
| `excludeGlob` | (none) | Array of globs to exclude |
| `contextBefore` | 0 | Lines of context before each match |
| `contextAfter` | 0 | Lines of context after each match |
| `maxMatches` | 200 | Global cap (hard max: 10,000) |
| `maxMatchesPerFile` | 20 | Per-file cap |
| `sortBy` | `"path"` | `"path"` (lex by path/line/col) or `"relevance"` (BM25 for prose) |
| `timeoutMs` | 2000 | Query timeout in milliseconds (hard cap: 30,000) |

**Response (200):**

```json
{
  "freshness": {
    "indexedHead": "abc123...",
    "currentHead": "abc123...",
    "pendingFileCount": 0,
    "lastFullScanAt": "2026-04-15T10:00:12Z",
    "isStale": false
  },
  "matches": [
    {
      "path": "src/Indexed.Core/SqliteIndex.cs",
      "line": 42,
      "column": 8,
      "byteOffset": 1284,
      "text": "    public static SqliteIndex OpenOrCreate(string dbPath)",
      "kind": "code",
      "contextBefore": ["    /// <summary>", "    /// Open or create the index database."],
      "contextAfter": ["    {", "        var dbDir = Path.GetDirectoryName(dbPath);"]
    }
  ],
  "truncated": false,
  "totalMatches": 3,
  "elapsedMs": 4
}
```

**Error response (4xx/5xx):**

```json
{
  "code": "pattern-invalid",
  "message": "regex parse error",
  "details": "unterminated group at position 5"
}
```

Error codes: `bad-request`, `pattern-invalid`, `timeout-exceeded`, `repo-not-found`, `unavailable`, `not-implemented`, `internal`.

### 3.3 `POST /rescan`

Trigger a reconciliation rescan. Returns status immediately; the rescan runs asynchronously.

**Response (200):** Same as `GET /status`.

### 3.4 `POST /shutdown`

Gracefully shut down the daemon. Requires the shutdown token from `daemon.json`.

**Headers:**

```
X-Indexed-Shutdown-Token: <base64-encoded-token-from-daemon.json>
```

**Response (204):** No content. Daemon begins shutdown.

**Response (403):** Token missing or invalid.

## 4. Agent integration

### 4.1 Daemon discovery

1. Compute the repo ID: `SHA1(abspath(repoRoot) + "\0" + firstCommitSha)[0:12]`.
2. Read `%LOCALAPPDATA%\Indexed\<repoId>\daemon.json`.
3. If the file exists, probe `GET /status` on the advertised port.
4. If the probe succeeds, use the daemon. If it fails or the file is missing, launch the daemon.

The `idx` CLI handles this automatically. Agents that use the HTTP API directly can invoke `DaemonLauncher` or simply run `idx status` to ensure the daemon is up.

### 4.2 Freshness-aware queries

Every response includes a `freshness` block. Recommended agent workflow:

1. Send `POST /search`.
2. Check `freshness.isStale`. If `true` and the result quality matters, wait 200–500 ms and retry.
3. If `pendingFileCount > 0`, the index is still catching up to recent edits.
4. If `indexedHead != currentHead`, a HEAD change (branch switch, commit) has not been fully processed yet.

### 4.3 Example: Claude Code integration

```bash
# Preferred over rg for full-repo searches once the daemon is warm:
idx find "SqliteIndex" --glob "src/**/*.cs" --json

# Status check:
idx status --json
```

For agents: prefer `--json` output for structured parsing. The JSON shape matches the HTTP API response exactly.

## 5. Configuration

### 5.1 Daemon options

The daemon accepts these command-line arguments (via `Indexed.Service` `Program.cs`):

| Argument | Default | Description |
|----------|---------|-------------|
| `<repo-root>` | (required) | Path to the git repository root |
| `--idle-timeout-seconds` | 1800 (30 min) | Seconds of inactivity before daemon exits |
| `--app-data` | `%LOCALAPPDATA%\Indexed` | Base directory for state files |
| `--exclude-index` | (none) | Glob patterns to exclude from indexing (repeatable) |
| `--no-default-excludes` | off | Do not apply the built-in default exclude list |

### 5.2 Built-in default exclude patterns

The daemon applies a curated default list of exclude patterns on every full
scan and incremental update. These patterns target files that inflate the FTS5
trigram index without providing meaningful search value.

**JS/TS lockfiles and bundles**

| Pattern | Reason |
|---------|--------|
| `**/package-lock.json` | NPM lockfile — large, machine-generated, no search value |
| `**/yarn.lock` | Yarn lockfile |
| `**/pnpm-lock.yaml` | pnpm lockfile |
| `**/npm-shrinkwrap.json` | NPM shrinkwrap |
| `**/*.min.js` | Minified JavaScript bundle |
| `**/*.min.css` | Minified CSS bundle |
| `**/*.map` | Source maps (can exceed the file they map) |

**Ecosystem lockfiles**

| Pattern | Reason |
|---------|--------|
| `**/Cargo.lock` | Rust |
| `**/composer.lock` | PHP Composer |
| `**/Gemfile.lock` | Ruby Bundler |
| `**/Pipfile.lock` | Python Pipenv |
| `**/poetry.lock` | Python Poetry |
| `**/go.sum` | Go modules checksum |
| `**/packages.lock.json` | .NET NuGet |

**Generated C# files**

| Pattern | Reason |
|---------|--------|
| `**/*.generated.cs` | Generic codegen output |
| `**/*.g.cs` | Protobuf / Roslyn source generators |
| `**/*.g.i.cs` | Roslyn incremental generator interface files |
| `**/*.Designer.cs` | WinForms / XAML designer files |

**Opting out**

To disable the default list and index all files, use `--no-default-excludes`:

```bash
idx find "lockfileVersion" --no-default-excludes
```

To disable defaults for the daemon session (persists until the daemon is
restarted), pass `--no-default-excludes` on the first `idx` invocation that
starts the daemon:

```bash
idx status --no-default-excludes   # ensures daemon starts without defaults
idx find "lockfileVersion"          # subsequent calls use the running daemon
```

User-supplied `--exclude-index` globs always compose with (or without) the
default list — they are not mutually exclusive.

## 6. Data directory layout

```
%LOCALAPPDATA%\Indexed\
    <repoId>/
        daemon.json         # Port, PID, startup time, shutdown token
        index.db            # SQLite database (WAL mode)
        index.db-wal        # WAL file (auto-managed)
        index.db-shm        # Shared memory file (auto-managed)
        logs/               # Daily-rotated structured logs
```

### 6.1 `daemon.json` format

```json
{
  "port": 54321,
  "pid": 12345,
  "repoRoot": "C:\\Tools2\\Tools",
  "repoId": "a1b2c3d4e5f6",
  "startedAt": "2026-04-15T10:00:00+00:00",
  "daemonVersion": "0.1.0",
  "shutdownToken": "base64-encoded-32-random-bytes"
}
```

Written atomically via temp-file + rename. Deleted on graceful shutdown. A stale file (from an unclean exit) is detected by the CLI via a failed `/status` probe.

### 6.2 `index.db` structure

SQLite database in WAL mode. Contains (schema version 2):

| Table | Content |
|-------|---------|
| `files` | File metadata: path, mtime, size, SHA-256, language, indexed timestamp |
| `code_fts` | FTS5 virtual table with trigram tokenizer; **contentless** (`content = ''`, `contentless_delete = 1`). Only the posting list is stored — match snippets are read from the working tree at query time. |
| `prose_fts` | FTS5 virtual table with porter+unicode61 tokenizer (Stage 3) |
| `meta` | Key-value metadata: schema version, repo ID, indexed HEAD, last scan time |

Inspect with `sqlite3`:

```bash
sqlite3 "%LOCALAPPDATA%\Indexed\<repoId>\index.db" "SELECT * FROM meta;"
sqlite3 "%LOCALAPPDATA%\Indexed\<repoId>\index.db" "SELECT count(*) FROM files;"
sqlite3 "%LOCALAPPDATA%\Indexed\<repoId>\index.db" "SELECT path FROM files LIMIT 20;"
```

### 6.3 Schema upgrades — one-time rebuild

On first start after an upgrade that bumps the schema version, the daemon
detects the mismatch, deletes the existing `index.db` (and its `-wal` /
`-shm` sidecars), and performs a full scan from scratch. The rebuild is
typically under a minute for a small repo and a few minutes for a large one.
No user action is required; only the first `/status` after upgrade is slow.

### 6.4 Background index compaction

The daemon runs a lightweight `IndexOptimizer` that periodically issues a
bounded FTS5 segment merge to reclaim fragmentation from incremental updates.
By default this is a 15-minute timer with a 512-page budget per tick; on
graceful shutdown it runs one final 1024-page merge before the WAL
checkpoint. The optimizer is fully transparent — it shares the same
writer-serialization semaphore as the incremental indexer, so merges can
never interleave with batch commits.

## 7. Troubleshooting

### Daemon won't start

**Symptom**: `idx find` hangs or returns exit code 4.

**Checks**:
1. Can the CLI locate `Indexed.Service.exe`? If not, publish/install side-by-side or set `INDEXED_SERVICE_EXE` (see §1.3).
2. Is `git` on PATH? The daemon fails fast if `git` is not available.
3. Is the current directory inside a git repository? Run `git rev-parse --show-toplevel`.
4. Is another daemon already running? Check `%LOCALAPPDATA%\Indexed\<repoId>\daemon.json` for a stale PID. Kill the process or delete the file.
5. Check logs at `%LOCALAPPDATA%\Indexed\<repoId>\logs\`.

### Stale results

**Symptom**: `freshness.isStale` is persistently `true`.

**Checks**:
1. Is the incremental indexer faulted? Check `idx status` — the daemon reports degraded state.
2. Are there many pending files? A large checkout or branch switch takes time to process.
3. Force a reconciliation: `idx rescan`.
4. Nuclear option: stop the daemon (`idx stop`), delete `index.db`, restart. The daemon will do a full rescan on startup.

### Corrupt database

**Symptom**: Daemon crashes on startup or returns errors.

**Fix**: Delete `index.db` and restart. The daemon auto-recreates the database and runs a full scan:

```bash
idx stop
del "%LOCALAPPDATA%\Indexed\<repoId>\index.db"
del "%LOCALAPPDATA%\Indexed\<repoId>\index.db-wal"
del "%LOCALAPPDATA%\Indexed\<repoId>\index.db-shm"
idx status   # restarts daemon and triggers full scan
```

### High memory or CPU

**Possible causes**:
- Large binary files slipping through the filter. Add `--exclude-index` patterns.
- Very large repository (hundreds of thousands of files). Initial scan is CPU-intensive; subsequent incremental updates are cheap.
- FileSystemWatcher buffer overflow causing repeated reconciliation. Check logs for FSW error events.

### Build errors

Ensure .NET 10 SDK is installed:

```bash
dotnet --version    # should be 10.x
```

Build the solution:

```bash
cd src/Indexed
dotnet build -c Release
```

Run tests:

```bash
dotnet test --nologo -v q
```

## 8. Glob pattern syntax

Indexed uses gitignore-style glob patterns for `--glob`, `--exclude`, and `--exclude-index`:

| Pattern | Matches |
|---------|---------|
| `*` | Any sequence of non-slash characters |
| `**` | Any path segment (zero or more directories) |
| `?` | Any single character |
| `src/**/*.cs` | All `.cs` files under `src/` |
| `**/tests/**` | Any path containing a `tests/` directory |
| `*.min.js` | Files ending in `.min.js` |

Patterns match against repo-relative POSIX-style paths (forward slashes). Case-insensitive on Windows.
