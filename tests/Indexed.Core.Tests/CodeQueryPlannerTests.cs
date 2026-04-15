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
}
