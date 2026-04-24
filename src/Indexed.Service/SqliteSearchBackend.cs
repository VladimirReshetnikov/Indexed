using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Abstractions;
using Indexed.Core;
using Microsoft.Data.Sqlite;

namespace Indexed.Service;

/// <summary>
/// SQLite-backed <see cref="ISearchBackend"/> for code, prose, and auto search.
/// </summary>
/// <remarks>
/// <para>
/// Code-mode queries resolve candidate file ids from the contentless trigram
/// index and rehydrate snippets from the working tree. Prose-mode queries hit
/// <c>prose_fts</c> directly and use FTS5 highlighting to project a
/// representative matched line inside the extracted prose span. Auto mode runs
/// both sides in parallel unless the request shape proves one side cannot
/// contribute (for example, a regex query cannot run against prose, and a
/// prose-only <see cref="SearchRequest.KindFilter"/> makes the code plan
/// pointless).
/// </para>
/// <para>
/// Timeout handling: the request's <see cref="SearchRequest.TimeoutMs"/> is
/// enforced by a linked <see cref="CancellationTokenSource"/> that fires
/// after the budget elapses. The backend never throws for timeouts — it
/// returns a <see cref="IndexedErrorCode.TimeoutExceeded"/> error that the
/// HTTP layer maps to 504.
/// </para>
/// </remarks>
internal sealed class SqliteSearchBackend : ISearchBackend
{
    private readonly SqliteIndex _index;
    private readonly Func<Freshness> _freshnessProvider;
    private readonly FileContentProvider _contentProvider;
    private readonly DebouncingEventQueue? _repairQueue;

    public SqliteSearchBackend(
        SqliteIndex index,
        Func<Freshness> freshnessProvider,
        FileContentProvider contentProvider,
        DebouncingEventQueue? repairQueue = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _freshnessProvider = freshnessProvider ?? throw new ArgumentNullException(nameof(freshnessProvider));
        _contentProvider = contentProvider ?? throw new ArgumentNullException(nameof(contentProvider));
        _repairQueue = repairQueue;
    }

    public async ValueTask<SearchBackendResult> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Pattern))
            return BadRequest("pattern must be non-empty");

        if (request.MaxMatches is < 1 or > 10_000)
            return BadRequest("maxMatches must be in [1, 10000]");
        if (request.MaxMatchesPerFile is < 1)
            return BadRequest("maxMatchesPerFile must be positive");
        if (request.MaxMatchesPerFile > request.MaxMatches)
            return BadRequest("maxMatchesPerFile must be in [1, maxMatches]");
        if (request.ContextBefore is < 0 or > 50)
            return BadRequest("contextBefore must be in [0, 50]");
        if (request.ContextAfter is < 0 or > 50)
            return BadRequest("contextAfter must be in [0, 50]");
        if (request.TimeoutMs is < 1 or > 30_000)
            return BadRequest("timeoutMs must be in [1, 30000]");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(request.TimeoutMs);

        try
        {
            var response = request.Mode switch
            {
                QueryMode.Code => await ExecuteCodeAsync(request, timeoutCts.Token).ConfigureAwait(false),
                QueryMode.Prose => await ExecuteProseAsync(request, timeoutCts.Token).ConfigureAwait(false),
                QueryMode.Auto => await ExecuteAutoAsync(request, timeoutCts.Token).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"unknown query mode {request.Mode}"),
            };

            return SearchBackendResult.Ok(response);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return SearchBackendResult.Fail(new ErrorResponse(
                IndexedErrorCode.TimeoutExceeded,
                $"search exceeded timeoutMs={request.TimeoutMs}"));
        }
        catch (CodeQueryPlanException ex)
        {
            return SearchBackendResult.Fail(new ErrorResponse(ex.Code, ex.Message));
        }
        catch (SqliteException ex) when (LooksLikeFtsPatternError(ex))
        {
            return SearchBackendResult.Fail(new ErrorResponse(
                IndexedErrorCode.PatternInvalid,
                $"invalid prose query: {ex.Message}"));
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            // A catastrophic-backtracking pattern tripped the per-match
            // timeout configured in CodeQueryPlanner. The caller deliberately
            // bounded the overall request via TimeoutMs, so we surface this
            // as a timeout rather than a generic 500 — the outcome is the
            // same from the user's perspective: the pattern could not be
            // evaluated within their deadline.
            return SearchBackendResult.Fail(new ErrorResponse(
                IndexedErrorCode.TimeoutExceeded,
                $"regex match exceeded timeoutMs={request.TimeoutMs}"));
        }
    }

    private static SearchBackendResult BadRequest(string message)
        => SearchBackendResult.Fail(new ErrorResponse(IndexedErrorCode.BadRequest, message));

    private async Task<SearchResponse> ExecuteCodeAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        var plan = CodeQueryPlanner.Build(request);
        var executor = new CodeQueryExecutor(_index, _contentProvider, _repairQueue);
        var result = await executor.ExecuteAsync(request, plan, cancellationToken).ConfigureAwait(false);

        return new SearchResponse(
            Freshness: _freshnessProvider(),
            Matches: result.Matches,
            Truncated: result.Truncated,
            TotalMatches: result.ReportedTotal,
            ElapsedMs: result.ElapsedMs);
    }

    private async Task<SearchResponse> ExecuteProseAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        var executor = new ProseQueryExecutor(_index);
        var result = await executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

        return new SearchResponse(
            Freshness: _freshnessProvider(),
            Matches: ExtractMatches(result.Matches),
            Truncated: result.Truncated,
            TotalMatches: result.ReportedTotal,
            ElapsedMs: result.ElapsedMs);
    }

    private async Task<SearchResponse> ExecuteAutoAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        var shouldRunCode = ShouldRunCode(request);
        var shouldRunProse = ShouldRunProse(request);

        Task<ExecuteResult>? codeTask = null;
        Task<ProseExecuteResult>? proseTask = null;

        if (shouldRunCode)
        {
            var plan = CodeQueryPlanner.Build(request);
            codeTask = new CodeQueryExecutor(_index, _contentProvider, _repairQueue)
                .ExecuteAsync(request, plan, cancellationToken)
                .AsTask();
        }

        if (shouldRunProse)
        {
            proseTask = new ProseQueryExecutor(_index)
                .ExecuteAsync(request, cancellationToken)
                .AsTask();
        }

        if (codeTask is not null)
            await codeTask.ConfigureAwait(false);
        if (proseTask is not null)
            await proseTask.ConfigureAwait(false);

        var merged = MergeAutoResults(
            request,
            codeTask?.Result,
            proseTask?.Result);

        return new SearchResponse(
            Freshness: _freshnessProvider(),
            Matches: merged.Matches,
            Truncated: merged.Truncated,
            TotalMatches: merged.ReportedTotal,
            ElapsedMs: merged.ElapsedMs);
    }

    private static bool ShouldRunCode(SearchRequest request)
    {
        if (request.KindFilter is not { Count: > 0 })
            return true;

        for (var i = 0; i < request.KindFilter.Count; i++)
        {
            if (request.KindFilter[i] == SpanKind.Code)
                return true;
        }

        return false;
    }

    private static bool ShouldRunProse(SearchRequest request)
    {
        if (request.IsRegex)
            return false;

        if (request.KindFilter is not { Count: > 0 })
            return true;

        for (var i = 0; i < request.KindFilter.Count; i++)
        {
            if (request.KindFilter[i] != SpanKind.Code)
                return true;
        }

        return false;
    }

    private static AutoMergeResult MergeAutoResults(
        SearchRequest request,
        ExecuteResult? code,
        ProseExecuteResult? prose)
    {
        var combined = new List<AutoRankedMatch>();
        var proseLines = new HashSet<(string Path, int Line)>();

        if (prose is not null)
        {
            foreach (var proseMatch in prose.Matches)
            {
                combined.Add(new AutoRankedMatch(proseMatch.Match, proseMatch.Rank, IsProse: true));
                proseLines.Add((proseMatch.Match.Path, proseMatch.Match.Line));
            }
        }

        if (code is not null)
        {
            foreach (var codeMatch in code.Matches)
            {
                if (proseLines.Contains((codeMatch.Path, codeMatch.Line)))
                    continue;

                combined.Add(new AutoRankedMatch(codeMatch, Rank: null, IsProse: false));
            }
        }

        combined.Sort(request.SortBy == SortBy.Relevance
            ? AutoRankedMatch.CompareByRelevance
            : AutoRankedMatch.CompareByPath);

        var observedTotal = combined.Count;
        var truncated = (code?.Truncated ?? false)
            || (prose?.Truncated ?? false)
            || combined.Count > request.MaxMatches;

        if (combined.Count > request.MaxMatches)
            combined.RemoveRange(request.MaxMatches, combined.Count - request.MaxMatches);

        var elapsed = Math.Max(code?.ElapsedMs ?? 0, prose?.ElapsedMs ?? 0);
        return new AutoMergeResult(
            Matches: ExtractMatches(combined),
            Truncated: truncated,
            ReportedTotal: observedTotal,
            ElapsedMs: elapsed);
    }

    private static IReadOnlyList<Match> ExtractMatches(IReadOnlyList<RankedMatch> matches)
    {
        var list = new List<Match>(matches.Count);
        for (var i = 0; i < matches.Count; i++)
            list.Add(matches[i].Match);
        return list;
    }

    private static IReadOnlyList<Match> ExtractMatches(IReadOnlyList<AutoRankedMatch> matches)
    {
        var list = new List<Match>(matches.Count);
        for (var i = 0; i < matches.Count; i++)
            list.Add(matches[i].Match);
        return list;
    }

    private static bool LooksLikeFtsPatternError(SqliteException ex)
        => ex.SqliteErrorCode == 1
            && (ex.Message.Contains("fts5", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("match", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("syntax", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("unterminated string", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase));
}

internal readonly record struct AutoRankedMatch(Match Match, double? Rank, bool IsProse)
{
    public static int CompareByRelevance(AutoRankedMatch left, AutoRankedMatch right)
    {
        if (left.IsProse != right.IsProse)
            return left.IsProse ? -1 : 1;

        if (left.IsProse)
        {
            var rankCompare = Nullable.Compare(left.Rank, right.Rank);
            if (rankCompare != 0)
                return rankCompare;
        }

        return CompareByPath(left, right);
    }

    public static int CompareByPath(AutoRankedMatch left, AutoRankedMatch right)
    {
        var byPath = string.CompareOrdinal(left.Match.Path, right.Match.Path);
        if (byPath != 0)
            return byPath;

        var byLine = left.Match.Line.CompareTo(right.Match.Line);
        if (byLine != 0)
            return byLine;

        return left.Match.Column.CompareTo(right.Match.Column);
    }
}

internal readonly record struct AutoMergeResult(
    IReadOnlyList<Match> Matches,
    bool Truncated,
    int ReportedTotal,
    long ElapsedMs);
