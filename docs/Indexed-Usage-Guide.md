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
- **Git (git mode only)**: `git.exe` must be on `PATH` when the selected target is a git repository. Plain directory targets do not require git.

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

### Option C: Publish for use in other repositories and workspaces

To use Indexed in arbitrary repositories or explicit directory workspaces (not just inside this checkout),
publish **both** the CLI and the daemon into the same directory and add it to
`PATH`:

```bash
cd src/Indexed
$dest = "$env:LOCALAPPDATA\\Indexed\\bin"
dotnet publish src/Indexed.Cli -c Release -o $dest
dotnet publish src/Indexed.Service -c Release -o $dest
```

Now `idx` can be invoked from any git repo or explicit directory workspace, and it will discover and launch the
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
idx status [--json] [--repo-root <dir>] [--root <dir>|<label=dir>]...
idx rescan [--repo-root <dir>] [--root <dir>|<label=dir>]...
idx stop [--repo-root <dir>] [--root <dir>|<label=dir>]...
idx daemons [--json]
idx --help
```

**Daemon launch options (any command):**

These options can be passed to **any** verb. They only take effect when that
invocation launches a new daemon; if an existing daemon is adopted via
`daemon.json`, the already-running daemon's settings remain in effect.

- `--exclude-index <glob>` (repeatable)
- `--no-default-excludes`
- `--no-default-directory-excludes` (directory targets only)
- `--idle-timeout-seconds <n>`

**Target selection rules:**

- No `--root` flags: preserve current git-mode behavior (discover the enclosing repository, or use `--repo-root`).
- One `--root <dir>`: serve a `directory-tree` target rooted at that directory.
- Two or more `--root <label=dir>` flags: serve a `directory-set` target with logical paths in the form `label/relative/path`.
- `--repo-root` and `--root` are mutually exclusive.

### 2.2 `idx find`

Search the indexed target for a pattern.

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
                   [--root <dir>|<label=dir>]...
                   [--no-default-directory-excludes]
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
| `--repo-root` | cwd | Override repository root detection (git mode only). |
| `--root` | none | Select a directory target. One bare path creates a `directory-tree`; repeated `LABEL=PATH` forms create a `directory-set`. |
| `--idle-timeout-seconds` | (daemon default) | Override daemon idle-exit window (seconds). Applies only when this invocation launches a new daemon. |
| `--no-default-directory-excludes` | off | Directory targets only. Do not apply the built-in directory-mode exclude list (`.git`, `node_modules`, `bin/obj`, caches, build outputs, etc.). |

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

# Search a non-git directory tree
idx find "TargetId" --root C:\src\scratch

# Search a multi-root workspace
idx find "OpenOrCreate" --root core=C:\src\proj\src --root docs=C:\src\proj\docs

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
daemon v0.1.0 pid=12345 schema=3
target  GitRepository a1b2c3d4e5f6
root    C:\Tools2\Tools
repo    C:\Tools2\Tools
repoId  a1b2c3d4e5f6
started 2026-04-15T10:00:00.0000000+00:00
rev     kind=Git current=abc123def456..., indexed=abc123def456...
stale   False (pending=0)
recon   2026-04-15T10:05:00.0000000+00:00
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

### 2.6 `idx daemons`

List the daemon descriptors currently discoverable under `%LOCALAPPDATA%\Indexed`.

```bash
idx daemons
idx daemons --json
```

Text output shows target kind, target id, PID, start time, and all roots. The command is read-only: it does not maintain a registry and does not mutate daemon state.

## 3. HTTP API reference

The daemon listens on `http://127.0.0.1:<port>/` where `<port>` is an OS-assigned ephemeral port written to `daemon.json`.

### 3.1 `GET /status`

Returns daemon health and freshness metadata.

**Response (200):**

```json
{
  "daemonVersion": "0.1.0",
  "schemaVersion": 3,
  "pid": 12345,
  "targetKind": "GitRepository",
  "targetId": "a1b2c3d4e5f6",
  "roots": [
    {
      "name": null,
      "absolutePath": "C:\\Tools2\\Tools",
      "isPrimary": true
    }
  ],
  "primaryRoot": {
    "name": null,
    "absolutePath": "C:\\Tools2\\Tools",
    "isPrimary": true
  },
  "repoRoot": "C:\\Tools2\\Tools",
  "repoId": "a1b2c3d4e5f6",
  "startedAt": "2026-04-15T10:00:00Z",
  "freshness": {
    "indexedHead": "abc123def456...",
    "currentHead": "abc123def456...",
    "pendingFileCount": 0,
    "lastFullScanAt": "2026-04-15T10:00:12Z",
    "isStale": false,
    "indexedRevisionToken": "abc123def456...",
    "currentRevisionToken": "abc123def456...",
    "revisionKind": "Git",
    "lastReconciliationAt": "2026-04-15T10:05:00Z"
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

1. Resolve a target selection from CLI arguments:
   - no `--root`: git repository target;
   - one `--root <dir>`: directory-tree target;
   - repeated `--root <label=dir>`: directory-set target.
2. Compute the target ID from the canonical target spec.
3. Read `%LOCALAPPDATA%\Indexed\<targetId>\daemon.json`.
4. If the file exists, probe `GET /status` on the advertised port.
5. If the probe succeeds, use the daemon. If it fails or the file is missing, launch the daemon.

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
| `<repo-root>` | cwd | Legacy positional git-repository root (compatibility form) |
| `--repo-root` | cwd | Explicit git-repository root |
| `--root` | (repeatable) | Directory target root selector (`<dir>` or `LABEL=PATH`) |
| `--idle-timeout-seconds` | 1800 (30 min) | Seconds of inactivity before daemon exits |
| `--app-data` | `%LOCALAPPDATA%\Indexed` | Base directory for state files |
| `--exclude-index` | (none) | Glob patterns to exclude from indexing (repeatable) |
| `--no-default-excludes` | off | Do not apply the built-in default exclude list |
| `--no-default-directory-excludes` | off | Directory targets only. Disable the directory-mode default excludes |

### 5.2 Built-in default index exclude patterns

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

### 5.3 Directory-mode default excludes

Directory targets apply a second built-in list by default to avoid walking obvious low-value or hazardous trees such as:

- VCS metadata: `.git/**`, `.hg/**`, `.svn/**`, `.bzr/**`
- dependency/install caches: `node_modules/**`, `.venv/**`, `venv/**`, `__pycache__/**`, `.tox/**`, `.pytest_cache/**`, `.mypy_cache/**`
- build outputs: `bin/**`, `obj/**`, `target/**`, `build/**`, `dist/**`, `out/**`, `.next/**`, `.nuxt/**`, `coverage/**`
- IDE/tooling state: `.idea/**`, `.vs/**`, `.vscode/**`, `.gradle/**`
- platform noise: `Thumbs.db`, `.DS_Store`, `$RECYCLE.BIN/**`, `System Volume Information/**`

Disable this list with `--no-default-directory-excludes` when you intentionally want those trees indexed.

## 6. Data directory layout

```
%LOCALAPPDATA%\Indexed\
    <targetId>/
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
  "targetKind": "GitRepository",
  "targetId": "a1b2c3d4e5f6",
  "roots": [
    {
      "name": null,
      "absolutePath": "C:\\Tools2\\Tools",
      "isPrimary": true
    }
  ],
  "primaryRoot": {
    "name": null,
    "absolutePath": "C:\\Tools2\\Tools",
    "isPrimary": true
  },
  "repoRoot": "C:\\Tools2\\Tools",
  "repoId": "a1b2c3d4e5f6",
  "startedAt": "2026-04-15T10:00:00+00:00",
  "daemonVersion": "0.1.0",
  "shutdownToken": "base64-encoded-32-random-bytes"
}
```

Written atomically via temp-file + rename. Deleted on graceful shutdown. A stale file (from an unclean exit) is detected by the CLI via a failed `/status` probe.

### 6.2 `index.db` structure

SQLite database in WAL mode. Contains (schema version 3):

| Table | Content |
|-------|---------|
| `roots` | Target roots: label, absolute path, primary-root flag |
| `files` | File metadata keyed by `root_id + relative_path`, with stable `logical_path` returned to the caller |
| `code_fts` | FTS5 virtual table with trigram tokenizer; **contentless** (`content = ''`, `contentless_delete = 1`). Only the posting list is stored — match snippets are read from the working tree at query time. |
| `prose_fts` | FTS5 virtual table with porter+unicode61 tokenizer (Stage 3) |
| `meta` | Key-value metadata: schema version, target identity, indexed revision token, scan/reconciliation timestamps |

Inspect with `sqlite3`:

```bash
sqlite3 "%LOCALAPPDATA%\Indexed\<targetId>\index.db" "SELECT * FROM meta;"
sqlite3 "%LOCALAPPDATA%\Indexed\<targetId>\index.db" "SELECT count(*) FROM files;"
sqlite3 "%LOCALAPPDATA%\Indexed\<targetId>\index.db" "SELECT logical_path FROM files LIMIT 20;"
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
2. If you are using git mode, is `git` on PATH? The daemon fails fast only for git-backed targets.
3. If you are using git mode, is the current directory inside a git repository? Run `git rev-parse --show-toplevel`.
4. Is another daemon already running? Run `idx daemons` or inspect `%LOCALAPPDATA%\Indexed\<targetId>\daemon.json` for a stale PID. Kill the process or delete the file.
5. Check logs at `%LOCALAPPDATA%\Indexed\<targetId>\logs\`.

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
del "%LOCALAPPDATA%\Indexed\<targetId>\index.db"
del "%LOCALAPPDATA%\Indexed\<targetId>\index.db-wal"
del "%LOCALAPPDATA%\Indexed\<targetId>\index.db-shm"
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

Patterns match against logical POSIX-style paths (forward slashes). For git and single-root directory targets this is the root-relative path. For directory-set targets it is `label/relative/path`. Matching is case-insensitive on Windows.
