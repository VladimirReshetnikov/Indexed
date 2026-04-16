using System;
using Indexed.Abstractions;
using Indexed.Core;
using Xunit;

namespace Indexed.Core.Tests;

/// <summary>Planner shape tests: trigram extraction and regex validation.</summary>
public sealed class CodeQueryPlannerTests
{
    [Fact]
    public void LiteralPattern_BuildsAndOfTrigramWindows()
    {
        var plan = CodeQueryPlanner.Build(new SearchRequest("IndexManifest", Mode: QueryMode.Code));

        Assert.False(plan.IsRegex);
        Assert.False(plan.FullScan);
        Assert.NotNull(plan.Fts5MatchExpression);
        // The expression should mention several lowercase trigrams from "indexmanifest".
        Assert.Contains("\"ind\"", plan.Fts5MatchExpression);
        Assert.Contains("\"man\"", plan.Fts5MatchExpression);
    }

    [Fact]
    public void TwoCharLiteral_TriggersFullScan()
    {
        var plan = CodeQueryPlanner.Build(new SearchRequest("hi", Mode: QueryMode.Code));
        Assert.True(plan.FullScan);
        Assert.Null(plan.Fts5MatchExpression);
    }

    [Fact]
    public void RegexPattern_CompilesAndExtractsTrigrams()
    {
        var plan = CodeQueryPlanner.Build(new SearchRequest(
            Pattern: @"Index\w+Manifest",
            Mode: QueryMode.Code,
            IsRegex: true));

        Assert.True(plan.IsRegex);
        Assert.NotNull(plan.Compiled);
        // Index + Manifest both survive analysis; at least one trigram from each
        // should remain in the expression (they'll be AND'd or OR'd together).
        Assert.NotNull(plan.Fts5MatchExpression);
    }

    [Fact]
    public void InvalidRegex_ThrowsPatternInvalid()
    {
        var ex = Assert.Throws<CodeQueryPlanException>(() =>
            CodeQueryPlanner.Build(new SearchRequest("(unclosed", IsRegex: true, Mode: QueryMode.Code)));
        Assert.Equal(IndexedErrorCode.PatternInvalid, ex.Code);
    }

    [Fact]
    public void WeakRegex_FallsBackToFullScan()
    {
        // A weak pattern like "f.o" extracts nothing narrowing — should full-scan.
        var plan = CodeQueryPlanner.Build(new SearchRequest(
            Pattern: "f.o", Mode: QueryMode.Code, IsRegex: true));
        Assert.True(plan.FullScan);
    }

    [Fact]
    public void RegexPattern_MatchTimeout_FollowsRequestTimeoutMs()
    {
        // Wire contract: a caller-supplied TimeoutMs must flow through to the
        // compiled Regex so catastrophic-backtracking patterns trip within
        // the request's overall deadline. Before this was wired, the
        // Regex's MatchTimeout was a hard-coded 5 seconds regardless of how
        // small a budget the caller passed.
        var plan = CodeQueryPlanner.Build(new SearchRequest(
            Pattern: @"Index\w+Manifest",
            Mode: QueryMode.Code,
            IsRegex: true,
            TimeoutMs: 500));
        Assert.NotNull(plan.Compiled);
        Assert.Equal(TimeSpan.FromMilliseconds(500), plan.Compiled!.MatchTimeout);
    }

    [Fact]
    public void RegexPattern_MatchTimeout_ClampedToFiftyMs_OnTinyBudget()
    {
        // A 1-ms TimeoutMs is theoretically legal (validator range [1, 30000])
        // but would leave the regex engine unable to compile even trivial
        // patterns. The planner clamps the per-match timeout up to 50 ms so
        // the engine always gets a minimum working budget; the overall
        // request still honors the 1-ms deadline separately via a linked
        // CancellationTokenSource in SqliteSearchBackend.
        var plan = CodeQueryPlanner.Build(new SearchRequest(
            Pattern: @"a+b",
            Mode: QueryMode.Code,
            IsRegex: true,
            TimeoutMs: 1));
        Assert.NotNull(plan.Compiled);
        Assert.Equal(TimeSpan.FromMilliseconds(50), plan.Compiled!.MatchTimeout);
    }
}
