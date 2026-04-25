# Indexed - FAQ

- Created (UTC): 2026-04-25T21:34:59Z
- Updated (UTC): 2026-04-25T23:10:22Z
- Repository HEAD: e9a29010947432df3cf2b5f4286366a2e6a8ad25

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

## Code And Prose Search

### What is the difference between code indexing and prose indexing?

Code indexing is the raw searchable file surface. Every indexable text file is
decoded and added to the `code_fts` trigram index, regardless of whether
Indexed understands the file's programming language. A code match has
`kind=code`, no enclosing prose span, and its snippet is rehydrated from the
current file content at query time.

Prose indexing is an extracted human-language overlay. Only files with a
registered prose extractor contribute spans to `prose_fts`: whole Markdown and
plain-text documents, XML documentation, grouped line comments, and block
comments for the languages Indexed currently recognizes. A prose match carries
its real span kind, such as `markdown`, `plain-text`, `xml-doc`,
`line-comment-block`, or `block-comment`, plus the extracted span line range.

Use `--mode code` for exact source/corpus searches and regex searches. Use
`--mode prose` when the intent is documentation/comment search with prose
ranking and stemming. `--mode auto` searches both surfaces when possible and
merges the results; regex queries are code-only because the prose surface uses
FTS5 `MATCH` syntax rather than .NET regular expressions.

### How does Indexed decide what counts as prose?

Prose is extractor-driven, not language-guess-driven.

The default extractor registry currently treats these as whole-file prose:

- Markdown: `.md`, `.markdown`, `.mdown`, `.mkd`.
- Plain text: `.txt`, `.rst`, `.adoc`.

It also extracts comments from recognized source/document formats:

- C# XML docs and comments from `.cs`.
- C-family comments from extensions such as `.c`, `.cpp`, `.h`, `.java`,
  `.js`, `.ts`, `.css`, `.go`, `.rs`, and `.swift`.
- F# comments from `.fs`, `.fsi`, and `.fsx`.
- Hash-line comments from `.py`, `.rb`, shell scripts, YAML, and TOML.
- PowerShell comments from `.ps1`, `.psm1`, and `.psd1`.
- SQL comments from `.sql`.
- XML/HTML comments from `.xml`, `.html`, `.xaml`, `.svg`, `.csproj`,
  `.props`, `.targets`, and `.config`.

Files without a registered prose extractor still go into the code index if
they are text and pass the size/binary filters.

### How are code and prose defined for `C:\TestData\wolfram`?

The Wolfram corpus is primarily a code corpus. Its Wolfram Language and
notebook-like files, including `.nb`, `.wl`, `.m`, `.wls`, `.wlt`, and `.mt`,
are indexed as raw code text. They do not currently have a Wolfram-specific
prose extractor, so Wolfram comments such as `(* ... *)` and notebook text
cells are searchable in `--mode code`, but are not separately searchable as
`--mode prose` spans.

The current manual index for `C:\TestData\wolfram` contains 5,982 indexed files.
The largest raw-code extension groups are `.nb`, `.m`, `.wl`, `.wls`, `.wlt`,
and `.mt`. Its prose overlay is much smaller and comes from recognized
documentation-like files: 51 Markdown spans, 49 plain-text spans, and 2 HTML
block-comment spans at the time this FAQ entry was written.

Examples:

```powershell
idx find "RowBox[" --mode code --glob "**/*.nb" --root C:\TestData\wolfram --index-updates manual
idx find "VerificationTest" --mode code --glob "**/*.{wlt,mt}" --root C:\TestData\wolfram --index-updates manual
idx find "parser" --mode prose --kind markdown --root C:\TestData\wolfram --index-updates manual
```

A useful future extension would add a Wolfram prose extractor for `(* ... *)`
comments in `.wl`, `.m`, `.wls`, `.wlt`, and `.mt`, with a separate
notebook-aware extractor for text cells if those become useful search targets.
