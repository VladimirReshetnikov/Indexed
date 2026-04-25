# Indexed

Background-indexed full-text search service for a local workspace target, aimed primarily at AI coding agents that need millisecond-class code search across a git repository, a standalone directory tree, or an explicit multi-directory workspace.

- Created (UTC): 2026-04-15T17:00:00Z
- Updated (UTC): 2026-04-25T22:44:28Z
- Repository HEAD: 6b75c7c68d5467d8952993deb0e2161e59058d77

## Project goals

- **Warm, millisecond-class code search** for workspaces you search repeatedly.
- **Agent-friendly** JSON and HTTP API on localhost (stable DTOs, explicit freshness).
- **Configurable update policy**: live background updates by default, or manual-only refresh through explicit rescans.
- **Low operational friction**: auto-start on first use, auto-exit on idle, self-healing rebuilds.

## Non-goals

- Replacing `rg` for one-off searches or ad-hoc “search everything including vendored/generated blobs” workflows.
- Code navigation (xref, “go to definition”, semantic symbol search).
- Cross-target search across unrelated daemon targets in a single query.

## Tech stack

- **C# / .NET 10** (`net10.0-windows`, nullable enabled, preview language features).
- **SQLite + FTS5** via `Microsoft.Data.Sqlite` (code index uses the trigram tokenizer).
- **Incremental indexing** via `FileSystemWatcher` + optional git HEAD polling + reconciliation in live mode.
- **HTTP/JSON daemon** on `127.0.0.1` (service) with a thin CLI client (`idx`).
- **Serialization** via `System.Text.Json` source generation (`IndexedJsonContext`).

## Feature set (current)

- **Literal search** over indexed code with ripgrep-style output (or JSON).
- **Regex search** with trigram-based narrowing + .NET `Regex` verification.
- **Prose search** over extracted XML docs, comment blocks, Markdown, and plain-text files.
- **Truthful `auto` mode** that merges code and prose results, preferring prose on exact same-line collisions.
- **Path filtering** via gitignore-style globs: `--glob` and `--exclude`.
- **Index-time include/exclude filtering** (`--include-index`, `--exclude-index`) plus curated default excludes for lockfiles/minified/generated outputs.
- **Configurable file-size cap** (`--max-indexable-file-mb` / `--max-indexable-file-bytes`) with status-visible non-indexable-file telemetry.
- **Manual update mode** (`--index-updates manual`) for static or externally-controlled targets where only startup scans and explicit `idx rescan` should change the index.
- **Directory-tree and directory-set targets** via repeated `--root` flags, including continuous background indexing outside git.
- **Context lines** (`-A`, `-B`, `-C`) without re-running a full scan per query.
- **Kind-aware text output** for prose hits plus stable JSON contracts for agents.
- **Explicit freshness** (`indexedRevisionToken`, `currentRevisionToken`, `pendingFileCount`, `isStale`) for agents and scripts.
- **Daemon discovery by target** with `idx daemons` plus target-aware `daemon.json` metadata.
- **Crash-safe persistence** (SQLite WAL mode) + background compaction (bounded FTS5 merges).

## Known limitations

- **Windows-only** today (`net10.0-windows`).
- **Huge files are whole-file indexed today**: files larger than the configured cap are treated as non-indexable. The default cap is 50 MiB; raising it can increase memory use because current indexing still reads each accepted file as one byte array before tokenization.
- **Multiline regex** is not supported (matches are line-oriented).
- **Prose query syntax is FTS5 MATCH syntax**. `--mode prose` does not interpret `pattern` as a .NET regex; in `--mode auto`, `--regex` therefore runs the code side only.
- **Directory-set queries use a logical-path namespace** (`label/relative/path`) that is stable once chosen; relabeling creates a distinct target.
- Index footprint can be large for trigram indexing; see the size-reduction docs in `docs/`.

## Status

Stages 0 through 5 are implemented and tested. Indexed now ships code search, prose extraction, truthful `auto` mode, background incremental indexing, workspace-target support, and productionization hardening in one coherent implementation.

| Stage | Description | Status |
|-------|-------------|--------|
| S0 | Workspace scaffolding | Complete |
| S1 | Enumeration + CLI + daemon bootstrap | Complete |
| S2 | FTS5 code index + query planner | Complete |
| S3 | Prose index + content extraction | Complete |
| S4 | Incremental indexer (FSW, HEAD polling, reconciliation) | Complete |
| S5 | Productionization hardening | Complete |

## Layout

```text
src/Indexed/
    Indexed.sln
    Directory.Build.props
    README.md
    docs/
        Indexed-Architecture.md
        Indexed-FAQ.md
        Indexed-Workspace-Targets-Proposal.md
        Indexed-Tutorial.md
        Indexed-Usage-Guide.md
        Indexed-Architecture-Proposal.md
        Indexed-Implementation-Plan.md
        Indexed-Index-Size-Reduction-Strategies.md
        Indexed-Size-Reduction-SafeNearTerm-Plan.md
        Indexed-Stage4-Incremental-Indexer-Plan.md
    src/
        Indexed.Abstractions/    DTOs: SearchRequest, SearchResponse, Freshness, Match, etc.
        Indexed.Targets/         target identity, directory targets, logical-path rules
        Indexed.Git/             git.exe wrapper: process runner, repository operations
        Indexed.Extractors/      Roslyn + regex-based prose extraction pipeline
        Indexed.Core/            SQLite+FTS5 index, query planner, full/incremental indexers
        Indexed.Service/         HTTP daemon host, idle-exit, lifecycle management
        Indexed.Cli/             CLI client (output: idx): argument parsing, daemon launcher
    tests/
        Indexed.Abstractions.Tests/
        Indexed.Extractors.Tests/
        Indexed.Git.Tests/
        Indexed.Core.Tests/
        Indexed.Service.Tests/
        Indexed.Cli.Tests/
```

## Quick start

```bash
# Build and test
cd src/Indexed
dotnet build
dotnet test

# Run a search (CLI auto-starts the daemon)
dotnet run --project src/Indexed.Cli -- find "SearchRequest" --glob "src/**/*.cs"

# Search extracted prose only
dotnet run --project src/Indexed.Cli -- find "lifetime" --mode prose --kind xml-doc

# Merge code + prose
dotnet run --project src/Indexed.Cli -- find "DisposeAsync" --mode auto

# Run against a non-git directory tree
dotnet run --project src/Indexed.Cli -- find "TargetId" --root C:\src\scratch

# Run against an explicit multi-root workspace
dotnet run --project src/Indexed.Cli -- find "OpenOrCreate" --root core=C:\src\proj\src --root docs=C:\src\proj\docs

# Check daemon status
dotnet run --project src/Indexed.Cli -- status

# List discovered daemon descriptors
dotnet run --project src/Indexed.Cli -- daemons

# Force a reconciliation rescan
dotnet run --project src/Indexed.Cli -- rescan

# Shut down the daemon
dotnet run --project src/Indexed.Cli -- stop
```

Requires the .NET 10 SDK. All projects target `net10.0-windows`.

## Installing `idx`

From inside this repository, `dotnet run` is the simplest path (see Quick start).

To use Indexed in arbitrary repositories or directory workspaces, publish **both** the CLI and the
daemon into the same directory and add it to `PATH`:

```bash
cd src/Indexed
$dest = "$env:LOCALAPPDATA\\Programs\\Indexed"
dotnet publish src/Indexed.Cli -c Release -o $dest
dotnet publish src/Indexed.Service -c Release -o $dest
```

The CLI must be able to locate `Indexed.Service.exe` to start the daemon. If
you cannot publish side-by-side, set `INDEXED_SERVICE_EXE` to the full path of
`Indexed.Service.exe` (see `docs/Indexed-Usage-Guide.md` §1.3).

## Documentation

- [Tutorial](docs/Indexed-Tutorial.md) — learning-oriented walkthrough for humans; read this first.
- [Usage guide](docs/Indexed-Usage-Guide.md) — CLI reference, HTTP API, configuration, data directory layout, troubleshooting.
- [FAQ](docs/Indexed-FAQ.md) — short answers for common operational questions about target identity, status output, and freshness.
- [Architecture](docs/Indexed-Architecture.md) — current-state architecture, layer ownership, data flow, concurrency model, failure handling.
- [Workspace targets proposal](docs/Indexed-Workspace-Targets-Proposal.md) — design record for the target model that now backs git, directory-tree, and directory-set indexing.
- [Proposed improvements](docs/Indexed-Proposed-Improvements__b8e57a4a6c7f.md) — prioritized next-step recommendations after workspace-target support landed.
- [Index size reduction strategies](docs/Indexed-Index-Size-Reduction-Strategies.md) — why trigram FTS5 is large and what can be done about it.
- [Size reduction near-term plan](docs/Indexed-Size-Reduction-SafeNearTerm-Plan.md) — concrete “what to do next” plan for shrinking `index.db` safely.
- [Architecture proposal](docs/Indexed-Architecture-Proposal.md) — original design document (historical).
- [Implementation plan](docs/Indexed-Implementation-Plan.md) — per-stage task breakdown and exit criteria (historical).

## Workspace boundary

`src/Indexed` is an independent workspace with its own source, tests, and documentation lifecycle. It takes **no dependency on any `Near.*` project**; see the [architecture proposal §6.2](docs/Indexed-Architecture-Proposal.md) for rationale.
