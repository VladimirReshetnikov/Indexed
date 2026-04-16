using System;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Abstractions;
using Indexed.Core;

namespace Indexed.Service;

/// <summary>
/// Stage 2 <see cref="ISearchBackend"/> that serves <see cref="QueryMode.Code"/>
/// from the per-repo SQLite FTS5 index via <see cref="CodeQueryExecutor"/>.
/// </summary>
/// <remarks>
/// <para>
/// Prose and auto modes still return <see cref="IndexedErrorCode.NotImplemented"/>:
/// Stage 3 adds <c>prose_fts</c> population, a prose planner, and the auto
/// merge. The <c>Stage 2</c>-specific contract here is: code-mode queries
/// are answered entirely from the index, with no file system I/O on the hot
/// path after initial indexing.
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

        if (request.Mode is QueryMode.Prose or QueryMode.Auto)
        {
            return SearchBackendResult.Fail(new ErrorResponse(
                IndexedErrorCode.NotImplemented,
                "prose and auto modes require the extractor layer (Stage 3); use mode=code for now"));
        }

        if (request.MaxMatches is < 1 or > 10_000)
            return BadRequest("maxMatches must be in [1, 10000]");
        if (request.MaxMatchesPerFile is < 1)
            return BadRequest("maxMatchesPerFile must be positive");
        if (request.ContextBefore is < 0 or > 50)
            return BadRequest("contextBefore must be in [0, 50]");
        if (request.ContextAfter is < 0 or > 50)
            return BadRequest("contextAfter must be in [0, 50]");
        if (request.TimeoutMs is < 1 or > 30_000)
            return BadRequest("timeoutMs must be in [1, 30000]");

        CodeQueryPlan plan;
        try
        {
            plan = CodeQueryPlanner.Build(request);
        }
        catch (CodeQueryPlanException ex)
        {
            return SearchBackendResult.Fail(new ErrorResponse(ex.Code, ex.Message));
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(request.TimeoutMs);

        try
        {
            var executor = new CodeQueryExecutor(_index, _contentProvider, _repairQueue);
            var result = await executor.ExecuteAsync(request, plan, timeoutCts.Token).ConfigureAwait(false);

            return SearchBackendResult.Ok(new SearchResponse(
                Freshness: _freshnessProvider(),
                Matches: result.Matches,
                Truncated: result.Truncated,
                TotalMatches: result.ReportedTotal,
                ElapsedMs: result.ElapsedMs));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return SearchBackendResult.Fail(new ErrorResponse(
                IndexedErrorCode.TimeoutExceeded,
                $"search exceeded timeoutMs={request.TimeoutMs}"));
        }
    }

    private static SearchBackendResult BadRequest(string message)
        => SearchBackendResult.Fail(new ErrorResponse(IndexedErrorCode.BadRequest, message));
}
