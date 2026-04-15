using System.Threading;
using System.Threading.Tasks;
using Indexed.Abstractions;

namespace Indexed.Service;

/// <summary>
/// Pluggable search strategy used by the daemon to answer
/// <c>POST /search</c> requests.
/// </summary>
/// <remarks>
/// <para>
/// In Stage 1 the only implementation is <see cref="RipgrepSearchBackend"/>,
/// which shells out to <c>rg</c>. Stage 2 replaces it with a SQLite
/// FTS5–backed implementation. The interface stays small on purpose:
/// implementations own their own parsing, budgets, and freshness reporting
/// and are swapped end-to-end.
/// </para>
/// </remarks>
public interface ISearchBackend
{
    /// <summary>
    /// Execute <paramref name="request"/> and return a response honoring the
    /// request's caps and timeout.
    /// </summary>
    /// <remarks>
    /// Implementations never throw for expected failures (pattern invalid,
    /// timeout, repo not found) — they return a response whose
    /// <see cref="SearchResponse.Matches"/> may be empty and carry a
    /// <see cref="Freshness"/> note explaining the state. Unexpected
    /// exceptions escape and are mapped to
    /// <see cref="IndexedErrorCode.Internal"/> by the HTTP layer.
    /// </remarks>
    ValueTask<SearchBackendResult> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Discriminated result from an <see cref="ISearchBackend"/> call:
/// either a populated <see cref="SearchResponse"/> or an
/// <see cref="ErrorResponse"/> that the HTTP layer maps to a 4xx/5xx.
/// </summary>
/// <remarks>
/// Using a result wrapper rather than throwing keeps the hot path allocation-
/// free for common error cases like <see cref="IndexedErrorCode.PatternInvalid"/>.
/// </remarks>
public readonly record struct SearchBackendResult
{
    /// <summary>Successful response; <c>null</c> when <see cref="Error"/> is set.</summary>
    public SearchResponse? Response { get; init; }

    /// <summary>Error envelope; <c>null</c> when <see cref="Response"/> is set.</summary>
    public ErrorResponse? Error { get; init; }

    /// <summary>Construct a success result.</summary>
    public static SearchBackendResult Ok(SearchResponse response)
        => new() { Response = response };

    /// <summary>Construct an error result.</summary>
    public static SearchBackendResult Fail(ErrorResponse error)
        => new() { Error = error };
}
