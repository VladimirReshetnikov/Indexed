# Indexed — Index-Size Reduction Strategies

- Created (UTC): 2026-04-15T00:00:00Z
- Repository HEAD: d3999a08fd558eb3ff9eae719b4cfb02865e49a5

## Scope

This document surveys strategies for shrinking the on-disk footprint of
Indexed's per-repo `index.db` relative to the total bytes of the indexed source
tree. It spans pure tuning (parameter flips), incremental architectural
changes (tokenizer, content storage), and fundamental format replacements
(segment-merged stores, compressed suffix indexes). For each approach the
document explains what it costs — query latency, incremental-update cost,
implementation complexity, freshness semantics — so the trade-offs are visible
before any line of code is written.

The Google Desktop Search reference point (~10% index-to-source ratio) is
used throughout as a rough anchor: what techniques plausibly produce a
one-order-of-magnitude index and which of those are reachable from Indexed's
current design.

## 1. Current state and baseline

### 1.1 Index layout today

Per `SqliteSchema.cs` (version 1), the on-disk database contains:

| Object | Purpose | Size driver |
|---|---|---|
| `files` row | metadata (path, mtime, size, sha256, language) | ~150 B/file |
| `files_path_glob` | UNIQUE/ordinary index on `path` | ~path length × 2 |
| `code_fts` | FTS5 trigram virtual table over full file text | **dominant** |
| `prose_fts` | FTS5 porter+unicode61 over extracted prose chunks | moderate |
| `meta` | small KV table | negligible |

FTS5 virtual tables expand into several shadow tables (`_data`, `_idx`,
`_content`, `_docsize`, `_config`). Of these, `_content` alone stores the
indexed text verbatim, and `_data` holds the inverted index (segments of
posting lists).

### 1.2 Rough size accounting

For a typical source tree (say, 100 MB of text):

| Component | Approximate on-disk cost |
|---|---|
| `code_fts._content` (stored source text) | ~100 MB (1× source) |
| `code_fts._data` (trigram posting lists) | ~60–120 MB (0.6–1.2× source) |
| `code_fts._docsize` / `_idx` | small |
| `prose_fts.*` shadow tables | ~10–20% of prose fraction of corpus |
| `files` + path index | <1 MB |

**Expected ratio today: ~1.8×–2.5× the indexed source tree** — roughly
**20×** the Google Desktop Search anchor. This is not a pathology; it is
consistent with FTS5's default settings and the trigram tokenizer's
fundamental properties (every 3-character window is a term).

### 1.3 Why trigrams are expensive

Trigram posting lists are dense. For a source file of length N:

- N−2 trigrams are emitted, with strong repetition within files but broad
  coverage across the repo.
- Common trigrams like ` th`, `the`, `ion`, `   `, `();` produce posting
  lists that are read in virtually every query.
- The inverted index size is O(total trigram occurrences), not O(unique
  trigrams), because FTS5 trigram tokenizer is positional — it stores
  per-occurrence positions to support phrase/adjacency queries.

Word-tokenized indexes (porter/unicode61) are typically 3–5× smaller than
trigram indexes on the same corpus because:

- Fewer terms per byte of source (≈1 word per 5–7 bytes vs. 1 trigram per
  byte).
- Terms are themselves larger, so dictionary overhead amortizes better.
- Posting lists compress more effectively (longer gaps between doc hits).

The trigram choice is deliberate: it supports substring and regex-style
queries without query-rewrite logic, which is essential for a code search
tool. Any size reduction strategy that abandons trigrams must either provide
equivalent substring semantics through another mechanism or accept reduced
query flexibility.

## 2. How Google Desktop Search plausibly reached ~10%

Google Desktop Search was closed-source, so this is informed reconstruction
rather than direct reporting. Techniques commonly cited and consistent with
public descriptions:

1. **Word-level tokenization with stemming.** No trigram index. Substring
   queries would not have been first-class; token-prefix queries were.
2. **Variable-byte encoded delta posting lists.** Doc-ID differences, not
   absolute IDs; one byte per small delta, expanding only when needed.
3. **No stored content** in the inverted index itself — snippet rehydration
   pulled from the original document on disk at query time.
4. **Compressed snippet cache** (a small, separate store of preview-worthy
   fragments, typically the first ~KB, LZ-compressed).
5. **Shared stop-word elision** and a frequency cutoff for very common tokens.
6. **Segment merging in the background** so the steady-state layout was near
   the compressed optimum.

Items 1, 3, 4, and 2 are the big wins. Each translates directly into an
Indexed option below; item 1 is the hardest to adopt because it breaks the
code-search use case unless paired with a substring index.

## 3. Approaches, grouped by invasiveness

Each subsection is self-contained: "what it changes", "expected size impact",
"what it costs", "migration cost".

### 3.1 Tuning — no schema break

#### 3.1.1 Disable FTS5 content storage (`content=''`)

**Change.** Declare `code_fts` with `content=''`. FTS5 no longer stores the
indexed text; `MATCH` still works, but `snippet()`, `highlight()`, and column
reads return empty strings. Snippet generation must read from the file on
disk (or a separate cache).

**Size impact.** Drops the `_content` shadow table entirely. This alone
removes roughly **1× the source size** from the index, cutting the ratio
from ~2× to ~1×.

**Cost.**
- Snippet queries now pay a file read + tokenize + offset-reconstruct on
  every hit. For a query returning 50 matches across 50 files on a warm page
  cache, this is tens of milliseconds; on cold cache it can be hundreds.
- `CodeQueryExecutor` already reads files for line-context assembly (see
  `MatchExtraction`), so the incremental cost is smaller than it looks — in
  practice we are already re-reading files. The savings come from not paying
  for it twice (once in the index, once at query time).
- **Freshness failure mode changes.** If the on-disk file has been modified
  since indexing, the snippet may not reflect the indexed state. Currently,
  FTS5's stored content is a consistent snapshot; disk content is the live
  file. Mitigation: compare `sha256`/`mtime_utc` in `files` and either
  recompute or flag staleness.

**Migration cost.** Single-table rebuild. Schema version bump.

**Verdict.** Highest leverage-per-line-of-code change in this document.
Essentially free size reduction at the cost of a modest query-path rework.

#### 3.1.2 Aggressive `PRAGMA` tuning

`PRAGMA page_size = 8192` (or 16384) improves FTS5 compression ratio marginally
because FTS5 segments align to pages. `PRAGMA auto_vacuum = INCREMENTAL` keeps
the file from bloating after deletes. `VACUUM INTO` occasionally reclaims
tombstoned segments.

**Size impact.** 5–15% on a steady-state database, one-off. Not a strategy,
just hygiene.

**Cost.** Negligible.

#### 3.1.3 Exclude lockfiles, generated files, binary-adjacent text

`ExcludeFilter` already handles binary-attributed paths. Extending the default
glob list to exclude `*.lock`, `package-lock.json`, `*.min.js`, `*.map`, and
similar high-noise, low-value files reduces corpus size without code changes.

**Size impact.** Repo-dependent; commonly 10–40% of a JS/TS repo lives in
lockfiles and minified bundles.

**Cost.** User must opt out if they actually want to search lockfiles.
Recommend making this a tunable default with a clear escape hatch.

### 3.2 Architectural — breaks schema, preserves SQLite

#### 3.2.1 External-content FTS5 pointing at `files`

**Change.** `CREATE VIRTUAL TABLE code_fts USING fts5(content='files', content_rowid='file_id', ...)`. The FTS5 table indexes text read from `files` (or a content-blob column added to it). Text stored once.

**Size impact.** Similar to `content=''` if we store the content in `files`
anyway (neutral); smaller if we drop stored content entirely (equivalent to
§3.1.1).

**Cost.** `INSERT`/`UPDATE`/`DELETE` on `files` must be mirrored into
`code_fts` via triggers. Rebuild/repair requires `INSERT INTO code_fts(code_fts) VALUES('rebuild')`.

**Verdict.** A reasonable intermediate step if we want a content column
managed as ordinary table data (e.g., to apply column-level compression —
see §3.2.2) while keeping FTS5 semantics.

#### 3.2.2 zstd-compressed content column

**Change.** Store file text in `files.content_zstd BLOB` compressed with
zstd (dictionary-trained on repo content). Use the SQLite zstd extension or
compress at the application layer.

**Size impact.** Text compresses ~3–4× with default zstd, ~5–8× with a
trained dictionary. If paired with external-content FTS5 (§3.2.1), this
eliminates one copy of the source tree at the cost of CPU on read.

**Cost.**
- Every snippet rehydration pays decompression (sub-millisecond for files
  under a few KB).
- Adds a native dependency (zstd) or a managed implementation.
- Dictionary training is a one-time offline step; re-training on large repo
  changes is a background job.

**Verdict.** Strong combination with §3.2.1. Moves Indexed from ~2× to
~0.4–0.6× of source size for the content portion; with trigram posting
lists still at ~1×, total ratio lands near **~1.2×**.

#### 3.2.3 Replace trigram tokenizer with word-level + bigram fallback

**Change.** Primary tokenizer: unicode61 + case-fold, stripping common code
punctuation into word boundaries. Add a secondary **bigram-of-words** index
for identifier-part queries (`Foo.Bar` → `foo bar` bigram). Implement
substring queries via a rewrite layer that decomposes a substring into
token-prefix + intersect constraints and filters false positives at the
file level with a stream-scan verification pass.

**Size impact.** Expected **5–10× reduction** in posting-list size relative
to trigrams on code corpora. Combined with §3.1.1, this puts the index in
the **0.15–0.25×** range before further compression.

**Cost.**
- Substring queries lose constant-time dispatch. The worst case is a query
  that decomposes into a single high-frequency token and must verify many
  candidate files — query latency can degrade from milliseconds to
  hundreds of milliseconds on large repos.
- Regex/glob queries need a separate path (or are not supported).
- Identifier boundary heuristics (camelCase, snake_case, kebab) must be
  tuned per-language. Getting this wrong has UX consequences.
- `CodeQueryExecutor` becomes substantially more complex.

**Verdict.** This is the single biggest lever toward the 10% target, but the
user-visible change is large. Requires a robust fallback for the "paste this
weird substring" use case that trigram indexes handle trivially.

#### 3.2.4 Posting-list compression (manual)

FTS5's posting lists are already VarInt-encoded and delta-compressed; this
is largely handled internally. The win here is bounded, but:

**Change.** Periodically force FTS5 segment merges (`INSERT INTO code_fts(code_fts) VALUES('optimize')` or `('merge', pages)`). Live-write segments are under-compressed; merged segments are tighter.

**Size impact.** 10–25% on a freshly incrementally-updated database.
Effectively free if run as a background task.

**Cost.** A full `optimize` is O(index size) I/O and can stall concurrent
writes for minutes on a large repo. Prefer incremental `merge` with a page
budget. `DaemonHost` would need a periodic low-priority job.

**Verdict.** Should be done regardless of other choices.

### 3.3 Fundamental — replace the index format

These require abandoning FTS5 as the primary inverted index. Each is a
multi-month project; they are included to make the full design space
visible.

#### 3.3.1 Lucene-style segmented store with custom posting-list codec

**Change.** Replace `code_fts` with a set of immutable segment files plus a
transaction log. Posting lists encoded as PFOR-delta or Elias-Fano blocks.
Segments compressed with zstd. Live writes go to a small mutable segment;
a background compactor merges segments into larger, more compressed tiers
(Lucene's TieredMergePolicy is the canonical reference).

**Size impact.** With word-level tokenization (§3.2.3), Lucene-class
indexes typically hit 0.2–0.3× of source. With trigrams, 0.6–0.8× — still a
meaningful improvement over FTS5's trigram store.

**Cost.**
- Enormous engineering effort: segment merge policy, crash recovery, readers
  during compaction, tombstoning deleted documents.
- `Indexed.Core` becomes a storage engine, not a thin SQLite wrapper.
- Tooling ecosystem (SQLite CLI, `sqlite3`, FTS5 rebuild) is gone; we must
  build our own.

**Verdict.** Only justified if §3.2 has been tapped and the ratio is still
unacceptable, **and** there is an ongoing maintenance owner for a storage
engine.

#### 3.3.2 FM-index / compressed suffix array

**Change.** Build a Burrows–Wheeler-transformed compressed self-index over
the concatenated repo text. Supports arbitrary substring search in O(m log n)
with no tokenization at all. Representative libraries: `sdsl-lite`,
`seqan3`, `libdivsufsort`.

**Size impact.** FM-indexes regularly achieve **0.5–1.5× of source** and
subsume content storage (the index *is* the compressed text plus
structures). For code search specifically, **0.7–1.0×** is realistic.

**Cost.**
- **Immutable.** Updating an FM-index for a single file change requires
  re-building the entire structure. Viable only in a segmented form where
  each segment is a separate FM-index and segment merges rebuild offline.
  This combines the complexity of §3.3.1 with the algorithmic weight of
  suffix arrays.
- Requires a native dependency or a substantial C# port; no mature managed
  FM-index libraries exist.
- Query code is specialized; no SQL fallback.

**Verdict.** Academic-grade size reduction, industrial-grade implementation
cost. Not a serious option for this project in the current cycle.

#### 3.3.3 Bloom-filter shard with on-demand verification

**Change.** Per-file bloom filter of trigrams (or tokens) at a tuned
false-positive rate. Queries test the bloom filter first to produce a
candidate file list, then linearly scan candidate files with a standard
substring matcher (ripgrep-style).

**Size impact.** At 1% FPR with 3 hash functions, ~10 bits per trigram.
For typical code, that's **~0.3× of source**. Content is not stored in the
index; file reads at query time provide ground truth.

**Cost.**
- Per-query cost is no longer independent of file count. For a query hit on
  20 files, we scan 20 files in full — fast on SSD and page cache, slow on
  cold cache or for queries with many candidates.
- False positives on the bloom filter cause wasted scans. Tuning the FPR is
  a size/latency dial.
- Phrase/adjacency queries are degraded — bloom filters are set-membership
  only; position data is lost. Must rescan to verify.

**Verdict.** A credible **hybrid tier**: bloom filter as a small primary
index for most queries, full trigram posting lists retained only for the
hottest terms. See §4.2.

#### 3.3.4 Contentless index with external compressed content tier

**Change.** Index stores only the inverted structure (no text). A separate
CAS (content-addressable store) keyed by `sha256` holds zstd-compressed file
blobs, deduplicated across identical files. Snippet rehydration reads from
the CAS.

**Size impact.** Equivalent to §3.1.1 + §3.2.2, generalized: files with
identical contents (vendored libraries, generated code) are stored once.

**Cost.**
- Git-like object store adds a new subsystem.
- GC becomes a real concern: orphaned blobs after deletes must be reclaimed.
- Cross-process reader coordination on the CAS (mmap, locking).

**Verdict.** Worth it if the repo has high content-level duplication (many
repos do, via `node_modules`, vendored dependencies, generated protobuf).
Diminishing returns on clean source trees.

## 4. Trade-off matrix

Each row is a single coherent design; combinations are discussed in §5.

| # | Approach | Expected ratio | Query latency Δ | Update cost Δ | Impl. effort | Breaks FTS5? |
|---|---|---|---|---|---|---|
| 3.1.1 | FTS5 `content=''` | ~1.0× | small (already read files) | none | S | no |
| 3.1.3 | Exclude lockfiles/minified | −10–40% | none | none | XS | no |
| 3.2.1 | External-content FTS5 | ~1.0–1.8× | none | small (trigger mgmt) | M | no |
| 3.2.2 | zstd content column | ~1.2× (with 3.2.1) | +0.2–1 ms/snippet | +CPU on write | M | no |
| 3.2.3 | Word tokens + bigram + verify | ~0.4× | +10–200 ms worst case | none | L | yes |
| 3.2.4 | Forced segment merges | −10–25% | brief stall during merge | periodic CPU/IO | S | no |
| 3.3.1 | Lucene-style custom index | ~0.2–0.3× | variable | needs compaction | XL | yes |
| 3.3.2 | FM-index / CSA | ~0.7–1.0× | +constant factor | offline rebuild | XXL | yes |
| 3.3.3 | Bloom shard + scan | ~0.3× | +file scan on hit | rebuild per-file filter | M | yes |
| 3.3.4 | Contentless + CAS | additive | +decompress on snippet | GC overhead | L | no |

Legend: XS < 1 day, S ~1 week, M ~1 month, L ~3 months, XL ~6+ months.

## 5. Plausible compositions

### 5.1 "Safe, near-term" (target ~1.0× of source)

Combine **3.1.1 + 3.1.3 + 3.2.4**. All SQLite, no format revolution, modest
engineering. Expected ratio drops from ~2× to **~0.9–1.1×**. Everything below
this compounds on top of this baseline.

### 5.2 "Compress the content tier" (target ~0.6×)

Add **3.2.1 + 3.2.2** on top of 5.1. Index remains FTS5 trigrams; content
layer becomes an external, zstd-compressed blob column with optional dict.
Snippet rehydration pays a decompression but avoids the re-tokenize pass.

### 5.3 "Word-tokenized primary + trigram fallback" (target ~0.25×)

Add **3.2.3** with a narrow trigram sidecar used only when a query defeats
the token boundary heuristic (e.g., searches inside identifiers or contains
non-word characters). The sidecar is an order of magnitude smaller than a
full trigram index because it only indexes *identifier-internal* substrings,
not whole-file prose.

This is the path that most closely matches Google Desktop Search's profile
and is realistic to reach the **10–25%** range.

### 5.4 "Full rewrite" (target ~0.15× or below)

Only if 5.3 is insufficient. Pick one of §3.3.1 or §3.3.3 as the primary
index. Retain 5.3's content tier as-is. Accept that Indexed becomes a
storage-engine project, not a SQLite consumer.

## 6. Freshness, concurrency, and correctness considerations

Smaller indexes tend to depend more on the filesystem and less on snapshot
copies. Several correctness constraints shift as a result.

- **Staleness window.** Once the indexed snapshot is dropped (§3.1.1 or
  §3.3.4), any snippet is only as fresh as the file on disk. If the indexer
  has recorded a file at sha A but disk now has sha B, the snippet is a
  mismatch. The query path must either: (a) verify the sha and skip stale
  results, (b) re-index the file inline, or (c) warn the user. Current
  `CodeQueryExecutor` does not do this and would need to.
- **Reader/writer coordination.** Segmented stores (§3.3.1) need a manifest
  protocol so readers pin a consistent view across segment merges. SQLite
  WAL does this for us today; we lose it if we leave SQLite.
- **Crash recovery.** FTS5 + WAL handles torn writes. Custom stores need
  their own journal/fsync discipline, or the first unclean shutdown will
  surface as mysteriously lost posting lists.

## 7. Recommendation

Execute §5.1 first. It is cheap, reversible, and measurable.

After §5.1 is in and benchmarked on real repos, re-evaluate the ratio
against the original motivation. If the ratio is acceptable, stop. If not,
proceed to §5.2 — still a single FTS5 schema, still fully reversible.

Only consider §5.3 if the use case genuinely demands it, because the UX
impact (query semantics shift, latency variance) is far larger than any of
the earlier steps. Do not build §5.4 on speculation.

Throughout, keep the `SqliteSchema.Version` discipline: every format change
bumps the version and triggers a rebuild. Rebuilds are fast for this
project (<60 s per repo today), so migration pressure does not justify
in-place conversion scripts in the v1 cycle.

## 8. Measurement plan

No size-reduction work should start without a reproducible baseline. Before
any change:

1. Pick 3–5 representative repositories spanning size, language mix, and
   vendored-dependency density.
2. Record: total text bytes (post-exclude), `index.db` size, per-shadow-table
   size (`SELECT name, SUM(pgsize) FROM dbstat GROUP BY name`), p50/p95 query
   latency across a fixed query set, indexing wall time.
3. After each change, re-run the same measurements. Ratio reductions that
   come with 5× latency regressions are not wins.

A CI job that enforces "index size ≤ target × source size" on a canary repo
is the cheapest way to keep gains from silently eroding.
