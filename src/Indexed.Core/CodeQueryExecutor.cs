using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Abstractions;

namespace Indexed.Core;

/// <summary>
/// Runs a <see cref="CodeQueryPlan"/> against a <see cref="SqliteIndex"/> and
/// produces <see cref="Abstractions.Match"/> records with line/column/context.
/// </summary>
/// <remarks>
/// <para>
/// Flow: ask the index for candidate file IDs (FTS5 MATCH or full scan), pull
/// the corresponding <c>(path, sha256)</c> rows in bounded batches, read the
/// live on-disk content via <see cref="FileContentProvider"/>, and scan each
/// content string for real matches using the pre-compiled regex or a plain
/// <see cref="string.IndexOf(string, int, StringComparison)"/>. Line and
/// column numbers are computed by walking the content and counting newline
/// offsets; context lines are extracted by re-slicing the same string.
/// </para>
/// <para>
/// Correctness under contentless FTS5 (schema v2). The index's trigram
/// posting list is a candidate oracle — the scanner verifies every match
/// against the live working tree. Three staleness classes are bounded:
/// </para>
/// <list type="bullet">
///   <item><description>Stale candidates (index ahead of disk edits): the disk scan
///   naturally returns zero hits for content that no longer exists.</description></item>
///   <item><description>Missing files: <see cref="FileContentProvider.ReadAsync"/>
///   yields <c>null</c>; the executor optionally enqueues a
///   <see cref="FileChanged"/> repair event so the incremental indexer
///   self-heals.</description></item>
///   <item><description>Fresh edits not yet indexed: same staleness class as before
///   — the candidate list may miss a file whose trigrams the indexer has
///   not yet tokenized. Not a regression from v1.</description></item>
/// </list>
/// <para>
/// Caps and timeouts:
/// </para>
/// <list type="bullet">
///   <item><description>Per-file cap (<see cref="SearchRequest.MaxMatchesPerFile"/>) truncates aggressive hits in auto-generated files.</description></item>
///   <item><description>Global cap (<see cref="SearchRequest.MaxMatches"/>) stops the executor mid-stream; the unseen remainder is reported via <see cref="SearchResponse.Truncated"/>.</description></item>
///   <item><description>The caller's <paramref name="cancellationToken"/> is honored between files and during SQLite round trips.</description></item>
/// </list>
/// <para>
/// Path globs are filtered application-side against the <c>files.path</c>
/// column after the candidate query — the candidate set is already small for
/// literal queries, and glob filtering on SQLite <c>LIKE</c> is strictly
/// worse for arbitrary globs (<c>**</c>, <c>?</c>) than running a compiled
/// regex over the returned paths.
/// </para>
/// </remarks>
public sealed class CodeQueryExecutor
{
    private readonly SqliteIndex _index;
    private readonly FileContentProvider _contentProvider;
    private readonly DebouncingEventQueue? _repairQueue;

    /// <summary>
    /// Create an executor that reads content from the working tree via
    /// <paramref name="contentProvider"/>. Optionally wire
    /// <paramref name="repairQueue"/> so missing-file observations are
    /// converted into <see cref="FileChanged"/> events for the incremental
    /// indexer.
    /// </summary>
    public CodeQueryExecutor(
        SqliteIndex index,
        FileContentProvider contentProvider,
        DebouncingEventQueue? repairQueue = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _contentProvider = contentProvider ?? throw new ArgumentNullException(nameof(contentProvider));
        _repairQueue = repairQueue;
    }

    /// <summary>
    /// Execute <paramref name="request"/> and return a <see cref="SearchResponse"/>
    /// honoring the request caps and timeout. The caller is responsible for
    /// building the <see cref="Freshness"/> — this class focuses on matches.
    /// </summary>
    public async ValueTask<ExecuteResult> ExecuteAsync(
        SearchRequest request,
        CodeQueryPlan plan,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var matches = new List<Abstractions.Match>();
        var truncated = false;
        var total = 0;

        var includeRegex = CompileOptionalGlob(request.PathGlob, include: true);
        var excludeFilter = new ExcludeFilter(request.ExcludeGlob);

        var candidates = await _index
            .QueryCodeCandidatesAsync(plan.Fts5MatchExpression, cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return new ExecuteResult(matches, truncated, total, sw.ElapsedMilliseconds);
        }

        // Pull files in batches to cap memory. Smaller repos finish in one batch.
        const int FileBatch = 256;
        for (var offset = 0; offset < candidates.Count; offset += FileBatch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkCount = Math.Min(FileBatch, candidates.Count - offset);
            var chunk = new long[chunkCount];
            for (var i = 0; i < chunkCount; i++) chunk[i] = candidates[offset + i];

            var rows = await _index.GetFilesAsync(chunk, cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!PathAllowed(row.Path, includeRegex, excludeFilter)) continue;

                var outcome = await _contentProvider
                    .ReadAsync(row.Path, cancellationToken)
                    .ConfigureAwait(false);
                if (!outcome.IsOk)
                {
                    // Disk confirmed the index row is stale. Enqueue the
                    // right repair event so the incremental indexer converges:
                    //   - Missing / OutOfRoot → the file is gone from the
                    //     caller's point of view; FileDeleted lets the indexer
                    //     remove the row on the next batch.
                    //   - Oversize / Unreadable → transient from the caller's
                    //     point of view; FileChanged prompts a retry where the
                    //     indexer will re-observe the condition and skip, but
                    //     a later write will re-add.
                    if (_repairQueue is not null)
                    {
                        IndexEvent repair = outcome.Status switch
                        {
                            FileReadStatus.Missing => new FileDeleted(row.Path),
                            FileReadStatus.OutOfRoot => new FileDeleted(row.Path),
                            _ => new FileChanged(row.Path),
                        };
                        _repairQueue.Enqueue(repair);
                    }
                    continue;
                }

                var hits = ScanFile(request, plan, row.Path, outcome.Content!, out var fileTruncated);
                total += hits.ReportedTotal;
                if (fileTruncated) truncated = true;

                foreach (var m in hits.Matches)
                {
                    matches.Add(m);
                    if (matches.Count >= request.MaxMatches)
                    {
                        truncated = true;
                        return new ExecuteResult(matches, truncated, total, sw.ElapsedMilliseconds);
                    }
                }
            }
        }

        return new ExecuteResult(matches, truncated, total, sw.ElapsedMilliseconds);
    }

    private static bool PathAllowed(string path, Regex? include, ExcludeFilter excludeFilter)
    {
        var norm = path.Replace('\\', '/');
        if (include is not null && !include.IsMatch(norm)) return false;
        if (excludeFilter.IsExcluded(path)) return false;
        return true;
    }

    private static Regex? CompileOptionalGlob(string? glob, bool include)
    {
        if (string.IsNullOrEmpty(glob)) return null;
        return PathGlob.Compile(glob);
    }

    // ----- scanning -----

    private static FileHits ScanFile(
        SearchRequest request,
        CodeQueryPlan plan,
        string path,
        string content,
        out bool fileTruncated)
    {
        fileTruncated = false;
        var matches = new List<Abstractions.Match>();
        var reportedTotal = 0;

        var lineOffsets = MatchExtraction.ComputeLineOffsets(content);

        foreach (var hit in FindHits(content, plan))
        {
            reportedTotal++;
            if (matches.Count >= request.MaxMatchesPerFile)
            {
                fileTruncated = true;
                continue;
            }

            var (line, col) = MatchExtraction.LineAndColumnOf(hit.Index, lineOffsets);
            var lineText = MatchExtraction.LineTextAt(content, lineOffsets, line);
            var before = MatchExtraction.ContextBefore(content, lineOffsets, line, request.ContextBefore);
            var after = MatchExtraction.ContextAfter(content, lineOffsets, line, request.ContextAfter);

            matches.Add(new Abstractions.Match(
                Path: path,
                Line: line,
                Column: col,
                ByteOffset: hit.Index, // character offset for Stage 2 — UTF-8 byte mapping is Stage 3+
                Text: lineText,
                Kind: SpanKind.Code,
                Span: null,
                ContextBefore: before,
                ContextAfter: after));
        }
        return new FileHits(matches, reportedTotal);
    }

    private static IEnumerable<(int Index, int Length)> FindHits(string content, CodeQueryPlan plan)
    {
        if (plan.IsRegex)
        {
            var regex = plan.Compiled!;
            var m = regex.Match(content);
            while (m.Success)
            {
                if (m.Length == 0)
                {
                    // Zero-length matches would loop forever; advance by 1 and continue.
                    yield return (m.Index, 0);
                    if (m.Index + 1 > content.Length) yield break;
                    m = regex.Match(content, m.Index + 1);
                    continue;
                }
                yield return (m.Index, m.Length);
                m = m.NextMatch();
            }
        }
        else
        {
            var cmp = plan.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var pat = plan.Pattern;
            var idx = 0;
            while (idx <= content.Length - pat.Length)
            {
                var hit = content.IndexOf(pat, idx, cmp);
                if (hit < 0) yield break;
                yield return (hit, pat.Length);
                idx = hit + pat.Length;
            }
        }
    }

    private readonly record struct FileHits(List<Abstractions.Match> Matches, int ReportedTotal);
}

/// <summary>Execution result rolled up from a single <see cref="CodeQueryExecutor.ExecuteAsync"/> call.</summary>
/// <param name="Matches">Matches (already capped and truncated).</param>
/// <param name="Truncated"><c>true</c> when some matches were dropped.</param>
/// <param name="ReportedTotal">
/// Total number of real matches scanned (pre-cap), used to populate
/// <see cref="SearchResponse.TotalMatches"/>.
/// </param>
/// <param name="ElapsedMs">Wall-clock spent inside the executor.</param>
public sealed record ExecuteResult(
    IReadOnlyList<Abstractions.Match> Matches,
    bool Truncated,
    int ReportedTotal,
    long ElapsedMs);
