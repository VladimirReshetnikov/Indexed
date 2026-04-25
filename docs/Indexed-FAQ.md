# Indexed - FAQ

- Created (UTC): 2026-04-25T21:34:59Z
- Updated (UTC): 2026-04-25T22:44:28Z
- Repository HEAD: 6b75c7c68d5467d8952993deb0e2161e59058d77

This document collects short answers to operational questions that tend to
come up while running Indexed against real local corpora. For complete command
syntax and JSON contracts, see [Indexed-Usage-Guide.md](./Indexed-Usage-Guide.md).

## Status And Target Identity

### Why do I need to pass `--index-updates manual` when I am only checking status?

Because the current Indexed target identity includes index-time settings, not
just the root path. Update mode is one of those target-defining settings.

These two commands address different target ids:

```powershell
idx status --root C:\TestData\wolfram --index-updates manual
idx status --root C:\TestData\wolfram
```

The second command defaults to `--index-updates live`. If no live-mode daemon
exists, it may start a separate live-update daemon and a separate index for the
same root. That behavior is intentional in the current model because live and
manual indexes have different freshness semantics and background services.

Repeat the same target-defining options for `status`, `find`, `rescan`, and
`stop` when you want to address an existing non-default target. In practice,
that means keeping flags such as `--index-updates manual`, `--include-index`,
`--exclude-index`, `--max-indexable-file-*`, `--no-default-excludes`, and
`--no-default-directory-excludes` consistent for the life of that target.

Use `idx daemons` when you only want to list running daemon descriptors. It
does not need target options because it enumerates `%LOCALAPPDATA%\Indexed`.

### What does `rev kind=None current=?, indexed=?` mean?

It means the target has no revision tracker.

Git-backed targets can report revision freshness by comparing the current
working revision with the revision captured by the index:

```text
rev     kind=Git current=abc123def456..., indexed=abc123def456...
```

Plain directory targets selected with `--root` are not inherently versioned, so
Indexed has no current or indexed revision token to display:

```text
rev     kind=None current=?, indexed=?
```

For a directory target, `stale True` is therefore not caused by a revision
mismatch. Common causes are `initial-scan`, queued file events, an in-flight
batch, or an indexing error reported in the status note.

### Does Indexed report indexing progress as a percentage?

No. Indexed currently reports work-observable counters, not a percentage.

During an initial scan, status includes:

```text
files   indexed=3896 skipped=1 maxFileBytes=52428800 updates=Manual initial-scan
note    initial full scan is still running.
```

The `indexed` and `skipped` counts show how many files have already been
processed, but the daemon does not expose a total denominator for the scan, so
it cannot truthfully report a percentage. Completion is indicated by
`initial-scan` disappearing and `stale False (pending=0)`.

For scripts, prefer JSON status:

```powershell
idx status --root C:\TestData\wolfram --index-updates manual --json |
  jq '{initialScanInProgress: .index.initialScanInProgress, isStale: .freshness.isStale, pendingFileCount: .freshness.pendingFileCount, indexedFileCount: .index.indexedFileCount}'
```

The scan is complete when `initialScanInProgress` is `false`,
`isStale` is `false`, and `pendingFileCount` is `0`.

## Query Behavior

### What is the search timeout for?

The timeout bounds one search request so a broad query, expensive regex, or
catastrophically-backtracking pattern cannot tie up the daemon indefinitely.
When the budget elapses, the daemon returns `timeout-exceeded` instead of
partial matches.

The default CLI and HTTP timeout is 10,000 ms, with a hard service cap of
30,000 ms. Use `--timeout-ms` when a deliberately broad corpus query needs a
larger budget:

```powershell
idx find "SparseArray" --mode auto --timeout-ms 30000
```

Prefer narrowing with `--mode code`, `--glob`, `--max-matches`, and
`--max-matches-per-file` before increasing the timeout; tighter queries keep
the daemon responsive and usually produce more useful result sets.
