using System;
using Indexed.Abstractions;
using Indexed.Core.RegexTrigrams;

namespace Indexed.Core;

/// <summary>
/// Converts a <see cref="SearchRequest"/> into a <see cref="CodeQueryPlan"/> —
/// the FTS5 MATCH expression (if any) and the concrete matcher the executor
/// will re-verify candidates against.
/// </summary>
/// <remarks>
/// <para>
/// Two paths:
/// </para>
/// <list type="bullet">
///   <item><description>Literal pattern: emit <c>AND(t₁, …, tₙ)</c> over the trigram windows of the pattern (lowercased, since FTS5's trigram tokenizer is case-insensitive by default). Re-verification uses a case-appropriate <see cref="string.IndexOf(string, StringComparison)"/>.</description></item>
///   <item><description>Regex pattern: delegate to <see cref="TrigramAnalyzer.Analyze"/> for the candidate trigrams, then re-verify with a compiled <see cref="System.Text.RegularExpressions.Regex"/>.</description></item>
/// </list>
/// <para>
/// When the planner cannot extract any trigram constraint
/// (<see cref="TrigramExpr.True"/>), the resulting plan is flagged
/// <see cref="CodeQueryPlan.FullScan"/>: the executor scans every indexed
/// file. That branch is slow but correct — a weak pattern like <c>f.o</c>
/// cannot be narrowed.
/// </para>
/// </remarks>
public static class CodeQueryPlanner
{
    /// <summary>Build a plan for a pre-validated code-mode <see cref="SearchRequest"/>.</summary>
    /// <exception cref="ArgumentException">Thrown when the regex is syntactically invalid (tested path → falls back to opaque).</exception>
    public static CodeQueryPlan Build(SearchRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrEmpty(request.Pattern))
            throw new ArgumentException("pattern must be non-empty", nameof(request));

        return request.IsRegex
            ? BuildRegex(request)
            : BuildLiteral(request);
    }

    private static CodeQueryPlan BuildLiteral(SearchRequest request)
    {
        var expr = TrigramExtraction.AndOfWindows(request.Pattern);
        var fts5 = expr.ToFts5MatchExpression();
        return new CodeQueryPlan(
            Fts5MatchExpression: fts5,
            FullScan: fts5 is null,
            IsRegex: false,
            Pattern: request.Pattern,
            CaseSensitive: request.CaseSensitive,
            Compiled: null);
    }

    private static CodeQueryPlan BuildRegex(SearchRequest request)
    {
        var opts = System.Text.RegularExpressions.RegexOptions.CultureInvariant
            | System.Text.RegularExpressions.RegexOptions.Compiled;
        if (!request.CaseSensitive)
            opts |= System.Text.RegularExpressions.RegexOptions.IgnoreCase;

        // Bound the per-match regex runtime to the caller's overall query
        // deadline. A catastrophic-backtracking pattern like `(a+)+b` against
        // a long non-matching line would otherwise spin in a single IsMatch
        // call well past the executor's own deadline — the executor checks
        // the token only between matches. Clamp to [50 ms, TimeoutMs] so a
        // trivially small TimeoutMs still leaves the engine room to finish
        // short patterns; TimeoutMs itself is already validated to
        // [1, 30000] by the request validator.
        var perMatch = TimeSpan.FromMilliseconds(Math.Max(50, request.TimeoutMs));

        System.Text.RegularExpressions.Regex compiled;
        try
        {
            compiled = new System.Text.RegularExpressions.Regex(request.Pattern, opts, perMatch);
        }
        catch (ArgumentException ex)
        {
            throw new CodeQueryPlanException(
                IndexedErrorCode.PatternInvalid,
                $"invalid regex: {ex.Message}");
        }

        var expr = TrigramAnalyzer.Analyze(request.Pattern, caseSensitive: request.CaseSensitive);
        var fts5 = expr.ToFts5MatchExpression();
        return new CodeQueryPlan(
            Fts5MatchExpression: fts5,
            FullScan: fts5 is null,
            IsRegex: true,
            Pattern: request.Pattern,
            CaseSensitive: request.CaseSensitive,
            Compiled: compiled);
    }
}

/// <summary>
/// Execution plan consumed by <see cref="CodeQueryExecutor"/>.
/// </summary>
/// <param name="Fts5MatchExpression">
/// Expression string to pass to <c>code_fts MATCH ?</c>, or <c>null</c> when
/// the pattern yielded no usable trigrams (triggers a full scan).
/// </param>
/// <param name="FullScan"><c>true</c> iff the executor will iterate every indexed file.</param>
/// <param name="IsRegex">Whether <paramref name="Pattern"/> is interpreted as a regex.</param>
/// <param name="Pattern">The original pattern — used for literal match re-verification.</param>
/// <param name="CaseSensitive">Re-verification case-sensitivity.</param>
/// <param name="Compiled">Pre-compiled regex for the <paramref name="IsRegex"/>=true path.</param>
public sealed record CodeQueryPlan(
    string? Fts5MatchExpression,
    bool FullScan,
    bool IsRegex,
    string Pattern,
    bool CaseSensitive,
    System.Text.RegularExpressions.Regex? Compiled);

/// <summary>
/// Thrown by <see cref="CodeQueryPlanner.Build"/> when the request is
/// inherently invalid (bad regex). The <see cref="IndexedErrorCode"/>
/// communicates the HTTP mapping the service layer will apply.
/// </summary>
public sealed class CodeQueryPlanException : Exception
{
    public IndexedErrorCode Code { get; }

    public CodeQueryPlanException(IndexedErrorCode code, string message)
        : base(message) { Code = code; }
}
