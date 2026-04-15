using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Indexed.Core;
using Indexed.Core.RegexTrigrams;
using Xunit;

namespace Indexed.Core.Tests;

/// <summary>
/// Pin the Russ Cox analyzer's core semantics: literal patterns collapse to
/// AND of windows, alternations union, and the correctness invariant holds
/// under a small random property check.
/// </summary>
public sealed class RegexTrigramsTests
{
    [Fact]
    public void LiteralFoo_RequiresFoo()
    {
        var expr = TrigramAnalyzer.Analyze("foo");
        var fts5 = expr.ToFts5MatchExpression();
        Assert.NotNull(fts5);
        Assert.Contains("\"foo\"", fts5);
    }

    [Fact]
    public void FooOrBar_RequiresFooOrBar()
    {
        var fts5 = TrigramAnalyzer.Analyze("foo|bar").ToFts5MatchExpression();
        Assert.NotNull(fts5);
        Assert.Contains("\"foo\"", fts5);
        Assert.Contains("\"bar\"", fts5);
        Assert.Contains(" OR ", fts5);
    }

    [Fact]
    public void FooDotStarBar_RequiresBothFooAndBar()
    {
        var fts5 = TrigramAnalyzer.Analyze(@"foo.*bar").ToFts5MatchExpression();
        Assert.NotNull(fts5);
        Assert.Contains("\"foo\"", fts5);
        Assert.Contains("\"bar\"", fts5);
    }

    [Fact]
    public void WeakPattern_ReturnsTrue()
    {
        // Too weak to extract anything — just two literal chars in a short pattern.
        Assert.Null(TrigramAnalyzer.Analyze("f.o").ToFts5MatchExpression());
    }

    [Fact]
    public void OptionalEntirely_ReturnsTrue()
    {
        // Entire pattern optional: nothing is required.
        Assert.Null(TrigramAnalyzer.Analyze("foo?").ToFts5MatchExpression());
    }

    [Fact]
    public void AnchoredLiteral_RequiresLiteralTrigrams()
    {
        var fts5 = TrigramAnalyzer.Analyze(@"^hello\b").ToFts5MatchExpression();
        Assert.NotNull(fts5);
        Assert.Contains("\"hel\"", fts5);
    }

    [Fact]
    public void Property_CandidatesSupersetOfRealMatches()
    {
        // Tiny corpus; the invariant: if the regex matches a document, the
        // analyzer's required trigrams must all appear in the document.
        var corpus = new[]
        {
            "alpha bravo charlie",
            "the quick brown fox jumps",
            "FooBarBaz == FooBar",
            "// IndexedErrorCode.TimeoutExceeded",
            "class SqliteIndex {}",
            "namespace Indexed.Core.RegexTrigrams;",
            "no matches here",
            "short",
            "",
        };

        string[] patterns =
        {
            "foo",
            "bar",
            "foo|bar",
            @"Foo\w+Baz",
            "Indexed",
            "Timeout.*Exceeded",
            "Sqlite",
            "class",
            "namespace",
            @"[Tt]rigram",
        };

        foreach (var pattern in patterns)
        {
            var expr = TrigramAnalyzer.Analyze(pattern, caseSensitive: false);
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (var doc in corpus)
            {
                if (!regex.IsMatch(doc)) continue;
                Assert.True(
                    Satisfies(expr, doc),
                    $"pattern='{pattern}' doc='{doc}' expr='{expr.ToFts5MatchExpression()}' but match exists");
            }
        }
    }

    // Evaluate a TrigramExpr against the lowercased trigram windows of doc.
    private static bool Satisfies(TrigramExpr expr, string doc)
    {
        var windows = TrigramExtraction.WindowsOf(doc);
        return SatisfiesImpl(expr, windows);
    }

    private static bool SatisfiesImpl(TrigramExpr expr, HashSet<string> windows) => expr switch
    {
        TrigramExpr.TrueExpr => true,
        TrigramExpr.Literal lit => windows.Contains(lit.Trigram),
        TrigramExpr.And a => a.Children.All(c => SatisfiesImpl(c, windows)),
        TrigramExpr.Or o => o.Children.Any(c => SatisfiesImpl(c, windows)),
        _ => throw new InvalidOperationException("unknown TrigramExpr node"),
    };
}
