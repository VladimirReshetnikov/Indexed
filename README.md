# Indexed

Background-indexed full-text search service for a local git repository, aimed primarily at AI coding agents that need millisecond-class code search across the working tree.

- Created (UTC): 2026-04-15T17:00:00Z
- Repository HEAD: cd463ca87356b067e49fe274a1ebcb6e92376c1d

## Status

Stages 0 through 2, 4, and 5 are implemented and tested. Stage 3 (prose extraction via Roslyn/regex) is pending. Code search with trigram indexing, incremental updates via FSW + HEAD polling, and productionization hardening are fully operational.

| Stage | Description | Status |
|-------|-------------|--------|
| S0 | Workspace scaffolding | Complete |
| S1 | Enumeration + CLI + daemon bootstrap | Complete |
| S2 | FTS5 code index + query planner | Complete |
| S3 | Prose index + content extraction | Pending |
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
        Indexed-Usage-Guide.md
        Indexed-Architecture-Proposal.md
        Indexed-Implementation-Plan.md
    src/
        Indexed.Abstractions/    DTOs: SearchRequest, SearchResponse, Freshness, Match, etc.
        Indexed.Git/             git.exe wrapper: process runner, repository operations
        Indexed.Core/            SQLite+FTS5 index, query planner, full/incremental indexers
        Indexed.Service/         HTTP daemon host, idle-exit, lifecycle management
        Indexed.Cli/             CLI client (output: idx): argument parsing, daemon launcher
    tests/
        Indexed.Abstractions.Tests/
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

# Check daemon status
dotnet run --project src/Indexed.Cli -- status

# Force a reconciliation rescan
dotnet run --project src/Indexed.Cli -- rescan

# Shut down the daemon
dotnet run --project src/Indexed.Cli -- stop
```

Requires the .NET 10 SDK. All projects target `net10.0-windows`.

## Documentation

- [Architecture](docs/Indexed-Architecture.md) — current-state system architecture, layer ownership, data flow, concurrency model, failure handling.
- [Usage guide](docs/Indexed-Usage-Guide.md) — CLI reference, HTTP API, configuration, data directory layout, troubleshooting.
- [Architecture proposal](docs/Indexed-Architecture-Proposal.md) — original design document (historical).
- [Implementation plan](docs/Indexed-Implementation-Plan.md) — per-stage task breakdown and exit criteria (historical).

## Workspace boundary

`src/Indexed` is an independent workspace with its own source, tests, and documentation lifecycle. It takes **no dependency on any `Near.*` project**; see the [architecture proposal §6.2](docs/Indexed-Architecture-Proposal.md) for rationale.
