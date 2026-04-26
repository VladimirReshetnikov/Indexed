# Indexed vs ripgrep performance on the Wolfram corpus

- Created (UTC): 2026-04-26T02:15:57Z
- Repository HEAD: 61037266f3664b750ab84c6186eced4cd9b12632

## Scope

This report compares `idx` and `rg` on `C:\TestData\wolfram`, using the current
manual Indexed target:

```text
idx status --root C:\TestData\wolfram --index-updates manual
```

Environment summary:

- Indexed daemon: `v1.0.0+b24452d61db6921da27474a63cb085305ae7e11c`.
- Indexed target: `DirectoryTree 884c7c3ea875`, manual update mode.
- Indexed file count: 5,982 indexed, 9 skipped.
- Indexed database: `%LOCALAPPDATA%\Indexed\884c7c3ea875\index.db`,
  7,347,920,896 bytes.
- Corpus tree size: 8,822 files, 8,062,544,856 bytes.
- ripgrep: `ripgrep 15.0.0 (rev 3a612f88b8)`.

The measurements include CLI process startup, argument parsing, daemon HTTP
round-trip for `idx`, and normal `rg` process startup. That is intentional:
these are the wall-clock costs a user sees from PowerShell. Existing OS file
cache state was not flushed, so this is a warm-machine comparison rather than a
cold-boot storage benchmark.

## Method

Each main scenario was run three times with a 30,000 ms per-process guard.
Most commands used count-like output to avoid measuring terminal rendering.
`idx` commands used `--json --max-matches 1 --max-matches-per-file 1` for the
main top-k/search-path comparison unless noted otherwise. `rg` commands used:

```powershell
rg --no-ignore --hidden --color never --no-heading --count-matches ...
```

This deliberately measures command completion, not time-to-first-streamed-line.
`rg` can often print early matches before it has scanned the whole tree, but a
complete `rg --count-matches` command still has to read the selected files.
Conversely, the main `idx` measurements constrain result materialization to a
small result set; the large-cap table below separately measures the cost of
asking Indexed to return thousands of matches.

The first notebook literal benchmark produced timeout interference because
killing an `idx` CLI process can leave the daemon request finishing its own
timeout budget. The notebook cases were therefore re-run in isolation without
forced early termination; those isolated figures are the authoritative notebook
figures below.

## Results

Median wall-clock times:

| Scenario | idx | rg | Faster | Notes |
| --- | ---: | ---: | --- | --- |
| Whole corpus no-match literal: `Needle_Not_Present_20260426` | 833 ms | 5,141 ms | idx 6.2x | Indexed avoids scanning the 8 GB tree. |
| Whole corpus literal: `SparseArray` | 847 ms | 4,917 ms | idx 5.8x | Broad corpus query with constrained result materialization. |
| Whole corpus short literal: `If` | 513 ms | 4,902 ms | idx 9.6x | Top-k path only; high result caps are much slower. |
| Notebook literal: `RowBox[` in `**/*.nb` | 676 ms | 4,815 ms | idx 7.1x | Isolated run after removing timeout interference. |
| Notebook regex: `(RowBox\|TemplateBox\|GraphicsBox)\[` in `**/*.nb` | 769 ms | 6,481 ms | idx 8.4x | Indexed regex trigram prefilter is effective here. |
| Test files: `VerificationTest` in `**/*.{wlt,mt}` | 674 ms | 160 ms | rg 4.2x | Narrow glob over small files favors native linear scan. |
| Markdown prose: `parser` in Markdown | 624 ms | 151 ms | rg 4.1x | Indexed service work is tiny; CLI startup dominates. |

High result caps change the picture. With `idx --max-matches 5000
--max-matches-per-file 5000`, daemon-side elapsed times were:

| Query | Reported total matches | Returned matches | idx daemon elapsed |
| --- | ---: | ---: | ---: |
| `SparseArray` across all code | 5,141 | 5,000 | 7,474 ms |
| `RowBox[` in notebooks | 10,652 | 5,000 | 1,307 ms |
| box-form regex in notebooks | 11,043 | 5,000 | 3,818 ms |
| `VerificationTest` in `.wlt`/`.mt` | 3,992 | 3,992 | 1,845 ms |
| Markdown prose `parser` | 5 | 5 | 10 ms |
| `If` across all code | 5,011 | 5,000 | 9,950 ms |

The large-cap table is daemon-side elapsed time from `idx` JSON, not total CLI
wall-clock. It shows that result materialization and snippet rehydration are a
major cost once the caller asks for thousands of matches.

## Interpretation

Indexed wins when the search scope is large. For whole-corpus searches and
notebook-heavy searches, `idx` is usually 6x to 10x faster than `rg` in the
measured top-k/count-style scenarios because it queries an FTS index instead of
rescanning gigabytes of notebook/source text.

`rg` wins when the search scope is already small. The `.wlt`/`.mt` and Markdown
cases are narrow enough that ripgrep's native startup plus direct scan beats
the .NET `idx` CLI startup and daemon round-trip. The prose Markdown case is
especially telling: Indexed's daemon reported only 10 ms for the large-cap
query, but the user-visible CLI command is still hundreds of milliseconds.

The `idx` CLI has meaningful fixed overhead. A 10-run process-overhead probe
measured median wall-clock times of about 1,156 ms for `idx status`, 1,832 ms
for an `idx` no-match JSON query, and 467 ms for `rg --version`. These numbers
vary, but the shape is stable: for tiny searches, process startup can dominate
Indexed's actual query engine time.

Result caps matter. `idx` is strongest when the user wants the first useful
screen of results. Asking for thousands of matches turns the query into a
rehydration, line/column, and serialization workload. For broad terms like
`If`, this can cost about 10 seconds even though the index found candidates
quickly.

## Practical Guidance

Use `idx` for broad corpus and notebook searches:

```powershell
$wlidx = @('--root', 'C:\TestData\wolfram', '--index-updates', 'manual')
idx find 'SparseArray' --mode code --case-sensitive @wlidx
idx find 'RowBox[' --mode code --case-sensitive --glob '**/*.nb' @wlidx
idx find -e '(RowBox|TemplateBox|GraphicsBox)\[' --mode code --case-sensitive --glob '**/*.nb' @wlidx
```

Use `rg` for narrow one-off scans over small file classes:

```powershell
rg --no-ignore --hidden --fixed-strings --glob '**/*.wlt' --glob '**/*.mt' 'VerificationTest' C:\TestData\wolfram
rg --no-ignore --hidden --ignore-case --fixed-strings --glob '**/*.md' 'parser' C:\TestData\wolfram
```

Keep `idx` result caps low unless the caller really needs a large export:

```powershell
idx find 'If' --mode code --case-sensitive --max-matches 200 --max-matches-per-file 20 @wlidx
```

## Improvement Opportunities

1. Add explicit count/files modes to `idx find`. A `--count`, `--count-matches`,
   or `--files-with-matches` mode could avoid snippet rehydration and large JSON
   payloads, giving Indexed a better equivalent for `rg --count-matches`.
2. Reduce CLI fixed overhead. NativeAOT, a smaller client executable, or a
   persistent shell/session client would materially improve tiny-query and
   status-check latency.
3. Make benchmarking use the HTTP API directly for daemon-side measurements.
   CLI wall-clock is the right UX number, but engine tuning needs a harness
   that does not conflate .NET process startup, daemon work, and timeout
   interference.
4. Continue tuning result materialization. Large caps show that reading files
   back, finding line/column positions, building snippets, and serializing
   matches can dominate broad queries after FTS has done its job.
