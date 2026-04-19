# Indexed — Tutorial

- Created (UTC): 2026-04-16T19:14:30Z
- Repository HEAD: bd61955fe5079ea3a4b6bd8a5f64628ddc5fd9fc

This tutorial is a **learning-oriented walkthrough** for humans who want to use
Indexed to search their own repositories. It is deliberately narrative and
task-shaped, and it assumes nothing about prior use of the tool.

If you want a complete, lookup-style option listing, see the companion document
[`Indexed-Usage-Guide.md`](./Indexed-Usage-Guide.md). The two documents are
meant to be read together: this one teaches you the workflow; the usage guide
is the reference you reach for once you know what you are doing.

## Contents

1. [What is Indexed, and when should I use it?](#1-what-is-indexed-and-when-should-i-use-it)
2. [First run: let the daemon come up](#2-first-run-let-the-daemon-come-up)
3. [Your first searches](#3-your-first-searches)
4. [Narrowing results: globs, kinds, and context](#4-narrowing-results-globs-kinds-and-context)
5. [Regular expressions and case sensitivity](#5-regular-expressions-and-case-sensitivity)
6. [Managing the daemon day-to-day](#6-managing-the-daemon-day-to-day)
7. [Configuring what gets indexed](#7-configuring-what-gets-indexed)
8. [Understanding freshness](#8-understanding-freshness)
9. [JSON output for scripts and agents](#9-json-output-for-scripts-and-agents)
10. [Working in multiple repositories](#10-working-in-multiple-repositories)
11. [Troubleshooting checklist](#11-troubleshooting-checklist)
12. [What to read next](#12-what-to-read-next)

---

## 1. What is Indexed, and when should I use it?

Indexed is a **per-repository code search daemon** for Windows. You talk to it
with the `idx` command-line tool; it replies with ripgrep-style output in the
terminal, or with JSON if you ask for it.

The design goal is **"`rg` speed without `rg`'s per-query warm-up"**. On a
small repo the difference is invisible. On a repo with tens of thousands of
files the first query is still slow (the daemon has to build its index), but
every subsequent query lands in tens of milliseconds because:

- The daemon keeps a trigram index of every indexed file in a SQLite database.
- A file-system watcher picks up edits as they happen, so the index stays
  current while you work.
- The daemon stays resident for ~30 minutes after your last request (and after
  indexing work goes quiet) and shuts itself down quietly if nothing comes in.

**Reach for Indexed when:**

- You search the same repository dozens of times in a session.
- You want symmetric context lines (`-A`, `-B`, `-C`) with low latency.
- You want structured JSON output that a script or agent can consume.
- Your queries include `--kind` filters (code-vs-comment-vs-markdown) that
  `rg` cannot express directly.

**Reach for `rg` instead when:**

- You are doing a one-off search in a repo you will not revisit.
- You want to search files that are deliberately excluded from the index
  (lockfiles, minified bundles — see §7.2). `rg` ignores nothing by default.
- You need features Indexed does not yet expose (for example, replace-in-place
  or multiline regex).

The two tools are complementary. Most contributors keep both on PATH.

## 2. First run: let the daemon come up

Once `idx` is on PATH, there is **no separate init step**. From inside any git
repository, run:

```bash
cd C:\path\to\your\repo
idx status
```

On first use you will see a short pause (a few seconds on a small repo, up to
a couple of minutes on a hundred-thousand-file repo). Under the hood:

1. `idx` computes a stable **repo ID** from the repo root path and the first
   commit SHA.
2. It looks for `%LOCALAPPDATA%\Indexed\<repoId>\daemon.json`. Not finding one,
   it launches the daemon.
3. The daemon enumerates every file that is either git-tracked or
   untracked-but-not-ignored (`git ls-files` plus
   `git ls-files --others --exclude-standard`), classifies each as code, prose,
   or binary, and builds the trigram index in `index.db`.
4. When indexing finishes, `idx status` returns.

Expected output after the first run:

```
Indexed daemon v0.1.0  pid=12345
  repo:    C:\path\to\your\repo
  repoId:  a1b2c3d4e5f6
  schema:  2
  started: 2026-04-16T19:14:30Z
  head:    bd61955fe507... (indexed: bd61955fe507...)
  stale:   no
  pending: 0 files
  last scan: 2026-04-16T19:14:42Z
```

Key things to notice:

- **`head` matches `indexed`**. That means the index is fully caught up.
- **`stale` is `no`**. Any query you run right now will return fresh results.
- **`pending` is 0**. No edits are queued for re-indexing.

If `pending` is non-zero or `stale` is `yes`, give it a moment and re-run
`idx status`. The daemon typically clears the backlog in a few seconds on
everything but the largest initial scans.

## 3. Your first searches

The verb for every search is `idx find`. The simplest form takes a literal
string:

```bash
idx find "SqliteIndex"
```

You will get output like:

```
src/Indexed.Core/SqliteIndex.cs:42:14:    public static SqliteIndex OpenOrCreate(string dbPath)
src/Indexed.Core/SqliteIndex.cs:150:24:    public async ValueTask DisposeAsync()
src/Indexed.Service/DaemonHost.cs:88:27:        var index = SqliteIndex.OpenOrCreate(dbPath);
```

The format is `path:line:column:text`. This is intentionally close to
ripgrep's default so your eye already knows how to read it, and so shell
pipelines that already consume `rg` output keep working.

**Quoting.** Wrap your pattern in double quotes if it contains a space, a
shell metacharacter, or anything you do not want the shell to expand. When
in doubt, quote it.

**Short patterns.** Patterns of one or two characters will trigger a
**full-scan** — the trigram index needs at least three characters per
"window" to be useful. A full scan still works, it is just slower. If you
find yourself searching for something like `if`, consider adding context
(e.g. a regex like `\bif\s*\(`) instead.

**Exit codes worth knowing:**

| Code | Meaning                                              |
|------|------------------------------------------------------|
| 0    | At least one match                                   |
| 1    | Query ran but produced zero matches                  |
| 2    | Bad command-line arguments                           |
| 3    | The daemon replied with an error                     |
| 4    | The daemon could not be reached or launched          |

Exit code 1 is the one most people forget: if you chain Indexed into a
script with `&&`, zero matches will stop the chain just like a failure would.
Use `|| true` if that is not what you want.

## 4. Narrowing results: globs, kinds, and context

### 4.1 Glob filters

Most searches benefit from a path-shape filter. Use `--glob` (or `-g`):

```bash
idx find "Dispose" --glob "src/**/*.cs"
idx find "TODO"    --glob "**/*.md"
idx find "import"  --glob "src/**/*.{ts,tsx}"
```

The glob syntax is gitignore-style:

- `*` — any run of non-slash characters
- `**` — any run of directory components (or none)
- `?` — exactly one character

On Windows, globs are case-insensitive; paths in queries and output are
normalized to forward slashes so your patterns stay portable.

You can also **exclude** paths for this single query:

```bash
idx find "RunAsync" --exclude "**/tests/**" --exclude "**/generated/**"
```

`--exclude` is per-query. It does not change what the daemon indexes — see
§7 for that.

### 4.2 Kind filters

Indexed classifies every line it indexes by **span kind**: `code`,
`markdown`, `plain-text`, `xml-doc`, `line-comment-block`, `block-comment`.
This lets you do things a plain text search cannot:

```bash
# Find "deadline" mentioned in doc-comments only, ignoring the same word in code
idx find "deadline" --kind xml-doc --kind block-comment

# Find "fixme" anywhere outside comments (unusual but occasionally useful)
idx find "fixme" --kind code
```

`--kind` is repeatable; each occurrence **adds** to the set of allowed
kinds. If you pass no `--kind` at all, every kind is allowed.

### 4.3 Context lines

Context flags behave the way they do in `grep` and `rg`:

```bash
# Two lines before, two lines after each match
idx find "throw new InvalidOperationException" -C 2

# Asymmetric
idx find "catch" -B 1 -A 4
```

Context lines are separated by `-` in the text output. They cost the daemon
very little — pulling neighbouring lines from the working tree is cheap — so
do not hesitate to use them liberally.

## 5. Regular expressions and case sensitivity

Pass `--regex` (or `-e`) to switch to .NET regex syntax:

```bash
idx find -e "class\s+\w+Index"
idx find -e "^\s*public\s+async\s+Task"
```

Two things worth understanding:

1. **Indexed runs the regex on the working-tree lines**, not on some
   rewritten form. What you type is what gets matched.
2. **The trigram index is still used to narrow which files are opened**
   whenever Indexed can extract literal trigrams from your regex. Your
   regex `Index\w+Manifest` gets matched only in files that contain both
   `ind`/`nde`/`dex` trigrams *and* `man`/`ani`/`nif` etc. Patterns that
   expose no strong literals (for example, `f.o`) fall back to a full
   scan — still correct, just not as fast.

Use `--case-sensitive` (`-s`) to flip from the default
case-insensitive match to exact case. Literal and regex queries both
honour it:

```bash
idx find "httpClient" -s
idx find -e "^\s*[A-Z][a-z]+Service$" -s
```

**Regex timeouts.** Indexed compiles your regex with a `MatchTimeout`
that tracks the request's `--timeoutMs` (default 2000). A
catastrophically-backtracking pattern will fail fast with a
`timeout-exceeded` error rather than hang the daemon.

## 6. Managing the daemon day-to-day

Indexed deliberately makes the daemon **invisible when it works**. The
three commands below are all you need for routine operation.

### 6.1 `idx status` — health check

```bash
idx status
```

Use it when:

- You want to know whether the daemon is running.
- Recent results seemed wrong and you want to check whether the index is
  caught up.
- You are about to run an important query and want to confirm
  `stale: no`.

### 6.2 `idx rescan` — force reconciliation

```bash
idx rescan
```

The daemon normally keeps up via a file-system watcher, but watcher events
can be missed (for example, when a large sync or bulk rebase writes
thousands of files at once). `idx rescan` tells the daemon to diff its
index against the working tree and correct any drift.

`idx rescan` **returns immediately**. The actual work happens in the
background. Follow it with `idx status` to see progress.

### 6.3 `idx stop` — graceful shutdown

```bash
idx stop
```

This drains in-flight work, checkpoints the SQLite WAL, and deletes
`daemon.json`. You rarely need it — the 30-minute idle timeout handles
most cases. Reach for it when:

- You want to free memory immediately (closing a laptop, for example).
- You are about to `git clean -xdf` or otherwise nuke the working tree.
- You are upgrading Indexed and want to make sure the old daemon is gone
  before you launch the new binary.

### 6.4 Idle timeout

By default the daemon exits after 30 minutes without a request (and with no
pending index work). You can override this on a CLI invocation that launches a
new daemon:

```bash
# Keep the daemon alive for 8 hours of idle
idx status --idle-timeout-seconds 28800
```

The override applies to that daemon instance; the next launch will use the
default unless you pass the flag again. If a daemon is already running,
`idx status` will adopt it and ignore the override — run `idx stop` (or wait
for the idle timeout) first if you need a new value to take effect.

## 7. Configuring what gets indexed

### 7.1 The built-in default excludes

Out of the box, Indexed skips files that inflate the index without
providing useful search value:

- JavaScript/TypeScript lockfiles and bundles
  (`package-lock.json`, `yarn.lock`, `pnpm-lock.yaml`, `*.min.js`, `*.map`)
- Ecosystem lockfiles
  (`Cargo.lock`, `Gemfile.lock`, `go.sum`, `packages.lock.json`, etc.)
- Generated C# files
  (`*.generated.cs`, `*.g.cs`, `*.g.i.cs`, `*.Designer.cs`)

The full, authoritative list lives in §5.2 of the usage guide.

### 7.2 Searching inside excluded files

Most of the time you want the defaults. Occasionally you actually do need
to search a lockfile — for example, to identify which dependency pinned a
specific version. Two ways to do that:

**Temporary (single query):**

```bash
idx find "lockfileVersion" --no-default-excludes
```

This widens the search *after the fact*, so only files already in the
index are returned. If the daemon started with defaults on, excluded
files are not in the index and will not match.

**Permanent for the session (restart the daemon without defaults):**

```bash
idx stop                                          # kill the current daemon
idx status --no-default-excludes                  # start fresh, indexing everything
idx find "lockfileVersion"                        # now works
```

When you are done, another `idx stop` followed by a plain `idx status`
puts things back.

### 7.3 Adding your own per-repo excludes

Paths you never want indexed (generated output directories, vendored
dependencies) should be excluded at daemon launch:

```bash
idx stop
idx status --exclude-index "src/Generated/**" --exclude-index "vendor/**"
```

`--exclude-index` is repeatable and **composes** with the built-in
defaults (it does not replace them unless you also pass
`--no-default-excludes`).

### 7.4 Query-only excludes vs. index-only excludes

It is worth being deliberate about the difference:

| Flag              | Scope          | Who sees it | When it applies                    |
|-------------------|----------------|-------------|-------------------------------------|
| `--exclude`       | This one query | `idx find`  | Filters matches after the fact      |
| `--exclude-index` | Daemon session | Daemon      | Files never enter the index at all  |

Rule of thumb: use `--exclude` to mute noise in a single search, and
`--exclude-index` to keep the index itself lean.

## 8. Understanding freshness

Every `idx find` and `idx status` response carries a small **freshness**
block with four fields:

```json
"freshness": {
  "indexedHead":    "abc123...",
  "currentHead":    "abc123...",
  "pendingFileCount": 0,
  "lastFullScanAt": "2026-04-16T19:14:42Z",
  "isStale": false
}
```

Read them like this:

- **`indexedHead` vs. `currentHead`.** If they differ, HEAD has moved
  (commit, branch switch, rebase) and the daemon has not finished
  processing the delta yet. Give it a second or two.
- **`pendingFileCount`.** Non-zero means the file-system watcher has
  enqueued edits that are still being processed. On a typical edit burst
  this drops to zero within 100–500 ms.
- **`isStale`.** The convenience field. `true` iff either of the above
  conditions holds. If your workflow is "query, read, think", you can
  ignore this. If you are scripting — especially if you are scripting an
  agent — check `isStale` and retry after a short delay.

Freshness is **advisory**, not a promise. A `stale: no` response reflects
the moment the query ran; edits landing after that are still picked up
for the next query.

## 9. JSON output for scripts and agents

The commands that return structured data (`idx find` and `idx status`) accept
`--json` and emit a structured response that matches the HTTP API shape. The
JSON is stable; the human text is for humans.

```bash
idx find "IndexedErrorCode" --json
idx status --json
```

The shape is documented in §3 of the usage guide. The two fields you will
most often script against:

```json
{
  "matches":   [ ... ],   // array of { path, line, column, text, kind, ... }
  "truncated": false,     // true if the response hit --max-matches
  ...
}
```

A minimal jq pipeline to list the top hits:

```bash
idx find "SqliteIndex" --json | jq -r '.matches[] | "\(.path):\(.line)"'
```

## 10. Working in multiple repositories

Each repository gets its **own** daemon, its **own** index, and its
**own** directory under `%LOCALAPPDATA%\Indexed\<repoId>\`. Two daemons
for two repos can happily run side-by-side — they listen on separate
ephemeral ports and do not share any state.

The repo ID is derived from the absolute path **and** the first commit
SHA, so:

- Two clones of the same repo at different paths are treated as
  **different** repositories (separate indexes).
- A repo with a rewritten history (new first commit) is treated as a
  **new** repository. The old index becomes orphaned and you can delete
  its directory.

If you have many repos and want to reclaim space, it is safe to delete
any `%LOCALAPPDATA%\Indexed\<repoId>\` subdirectory whose `daemon.json`
is absent (i.e., no daemon is currently running for it). The directory
will be regenerated the next time you run `idx` inside that repo.

## 11. Troubleshooting checklist

Pick the symptom that matches yours.

### 11.1 `idx find` hangs or exits with code 4

The daemon could not be reached or launched.

1. Is `git` on PATH? `git --version` should succeed.
2. Can the CLI locate `Indexed.Service.exe`? If you see an error about the
   daemon executable, publish/install `idx.exe` and `Indexed.Service.exe`
   side-by-side or set `INDEXED_SERVICE_EXE` (see the usage guide §1.3).
3. Is the current directory inside a git repo? `git rev-parse --show-toplevel`.
4. Is a stale `daemon.json` pointing at a dead PID? Check
   `%LOCALAPPDATA%\Indexed\<repoId>\daemon.json`. Delete it (or kill the
   PID it names) and retry.
5. Look at the daemon log at
   `%LOCALAPPDATA%\Indexed\<repoId>\logs\`.

### 11.2 Results look stale no matter what

`idx status` says `stale: yes` persistently and `pending` never reaches 0.

1. Is the initial scan still running? For a large repo this can take
   minutes. `last scan` gives you a timestamp.
2. Did something externally overwrite half the working tree (rebase, bulk
   sync, partial checkout)? Run `idx rescan` and watch `pending` drain.
3. As a last resort: `idx stop`, delete the `index.db*` files in the repo's
   app-data directory, and `idx status` again to trigger a rebuild.

### 11.3 Results include files you thought were excluded

1. Check your query: `--exclude` filters results but files already in the
   index still get matched. Use `--exclude-index` instead (after
   restarting the daemon) if you do not want them indexed at all.
2. Check if you passed `--no-default-excludes` on the daemon's first
   launch, which would have disabled the built-in list for the whole
   session.

### 11.4 One particular regex hangs or times out

A pattern like `(a+)+b` can backtrack catastrophically on adversarial
input. Indexed's compiled regex has a per-match timeout that you can tune
with `--timeoutMs` (via the JSON API) or by passing a shorter
`--max-matches` budget. Rewriting the pattern to avoid nested quantifiers
is the real fix.

### 11.5 Disk usage is surprising

Trigram indexing is fast, but it can be space-hungry. It is normal for
`index.db` to be a significant fraction of the indexed corpus (and for very
large repos, potentially comparable to it).

Also note that much of what you see in `%LOCALAPPDATA%\Indexed\<repoId>\` at
any given moment can be the SQLite **WAL** (`index.db-wal`) — it grows during
bursts of indexing and collapses on graceful shutdown or the next checkpoint.
If you are concerned, `idx stop` forces a checkpoint and often shrinks the
on-disk footprint noticeably.

If disk usage is a real problem:

- Add `--exclude-index` patterns for large low-value trees (`lib/**`, vendored
  bundles, etc.).
- Consider disabling the default exclude list only when needed
  (`--no-default-excludes` makes the index larger).
- Read `Indexed-Index-Size-Reduction-Strategies.md` and
  `Indexed-Size-Reduction-SafeNearTerm-Plan.md` for the deeper trade-offs.

### 11.6 I changed schema version or upgraded Indexed

The daemon detects the schema mismatch, deletes the old `index.db`, and
rebuilds on next start. The first `idx status` after an upgrade will
therefore be slow; subsequent calls are normal. No manual migration
step is ever required.

## 12. What to read next

- [`Indexed-Usage-Guide.md`](./Indexed-Usage-Guide.md) — the reference
  companion to this tutorial. Every flag, every field, every endpoint.
- [`Indexed-Architecture.md`](./Indexed-Architecture.md) — how the
  daemon, indexer, and query planner fit together. Read this if you want
  to understand *why* Indexed behaves the way it does under
  churn.
- [`Indexed-Index-Size-Reduction-Strategies.md`](./Indexed-Index-Size-Reduction-Strategies.md)
  — advanced reading for very large repos.

You should now be able to run Indexed productively on any git repo on
your machine. When something does not behave the way you expect, the
three commands `idx status`, `idx rescan`, and `idx stop` — in that
order — resolve the overwhelming majority of day-to-day problems.
