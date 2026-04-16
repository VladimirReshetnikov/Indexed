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
/// Modes: <see cref="QueryMode.Code"/> and <see cref="QueryMode.Auto"/> are
/// answered by this backend; until Stage 3 lands a prose planner,
/// <c>Auto</c> is treated as an alias for <c>Code</c> so that a caller who
/// accepts the DTO default gets sensible results rather than a
/// <see cref="IndexedErrorCode.NotImplemented"/>. Explicit
/// <see cref="QueryMode.Prose"/> still returns <c>NotImplemented</c>. The
/// <c>Stage 2</c>-specific contract is: code-mode queries resolve candidate
/// file ids from the index and rehydrate content from the working tree —
/// no per-request file scan over the whole repo.
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

        // Auto falls back to Code until Stage 3 ships a prose planner —
        // the DTO default is Auto, and returning NotImplemented to callers
        // who simply omitted Mode would be a usability regression.
        if (request.Mode == QueryMode.Prose)
        {
            return SearchBackendResult.Fail(new ErrorResponse(
                IndexedErrorCode.NotImplemented,
                "prose mode requires the extractor layer (Stage 3); use mode=code"));
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

        // SortBy: path-order is honored by the executor (deterministic
        // ordinal sort after collection); relevance requires FTS5 BM25
        // scoring that Stage 2 does not compute. Reject up front rather
        // than silently return path-ordered results.
        if (request.SortBy == SortBy.Relevance)
        {
            return SearchBackendResult.Fail(new ErrorResponse(
                IndexedErrorCode.NotImplemented,
                "sortBy=relevance requires BM25 scoring (Stage 3); use sortBy=path"));
        }

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
