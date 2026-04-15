# Indexed

Background-indexed full-text search service for this repository, aimed primarily at AI coding agents that need millisecond-class code and prose search across the working tree.

**Status: Stage 0 — scaffolding only.** No runtime functionality yet. The service surface, index engine, and extractor layer are delivered incrementally per the staging plan below.

## Layout

```text
src/Indexed/
├── Directory.Build.props
├── Indexed.sln
├── README.md
├── docs/
│   ├── Indexed-Architecture-Proposal.md
│   └── Indexed-Implementation-Plan.md
├── src/
│   └── Indexed.Abstractions/
└── tests/
    └── Indexed.Abstractions.Tests/
```

Projects arrive stage by stage:

| Project | Lands in |
|---------|----------|
| `Indexed.Abstractions` | S0 (scaffold) / S1 (DTOs) |
| `Indexed.Git` | S1 |
| `Indexed.Service` | S1 |
| `Indexed.Cli` | S1 |
| `Indexed.Core` | S2 |
| `Indexed.Extractors` | S3 |
| `Indexed.Watcher` | S4 |

## Documentation

- [Architecture proposal](docs/Indexed-Architecture-Proposal.md) — binding design (engine, schema, span model, layer ownership).
- [Implementation plan](docs/Indexed-Implementation-Plan.md) — per-stage task breakdown, test coverage, exit criteria.

## Build and test

```bash
cd src/Indexed
dotnet restore
dotnet build -c Release
dotnet test
```

Requires the .NET 10 SDK. Solution is in classic `.sln` format for consistency with the rest of the repository.

## Workspace boundary

`src/Indexed` is an independent workspace with its own source, tests, and documentation lifecycle. It takes **no dependency on any `Near.*` project**; see [the proposal §6.2](docs/Indexed-Architecture-Proposal.md) for rationale.
