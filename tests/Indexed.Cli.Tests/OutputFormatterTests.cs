using System;
using System.IO;
using Indexed.Abstractions;
using Indexed.Cli;
using Xunit;

namespace Indexed.Cli.Tests;

public sealed class OutputFormatterTests
{
    [Fact]
    public void WriteSearchText_RipgrepStyle()
    {
        var response = new SearchResponse(
            new Freshness(null, "abc", 0, null, true, "ripgrep-backed"),
            new[]
            {
                new Match(
                    Path: "src/a.cs", Line: 42, Column: 8, ByteOffset: 0,
                    Text: "    var x = 1;",
                    Kind: SpanKind.Code, Span: null,
                    ContextBefore: Array.Empty<string>(),
                    ContextAfter: Array.Empty<string>()),
            },
            Truncated: false, TotalMatches: 1, ElapsedMs: 1);

        using var sw = new StringWriter();
        OutputFormatter.WriteSearchText(sw, response);

        var text = sw.ToString();
        Assert.Contains("src/a.cs:42:8:    var x = 1;", text);
        Assert.Contains("# stale: ripgrep-backed", text);
    }

    [Fact]
    public void WriteSearchText_ContextLinesUseDashSeparator()
    {
        var response = new SearchResponse(
            new Freshness(null, "abc", 0, null, false),
            new[]
            {
                new Match(
                    Path: "a.cs", Line: 10, Column: 1, ByteOffset: 0,
                    Text: "match line",
                    Kind: SpanKind.Code, Span: null,
                    ContextBefore: new[] { "before1", "before2" },
                    ContextAfter: new[] { "after1", "after2" }),
            },
            Truncated: false, TotalMatches: 1, ElapsedMs: 0);

        using var sw = new StringWriter();
        OutputFormatter.WriteSearchText(sw, response);

        var lines = sw.ToString().TrimEnd('\r', '\n').Split('\n');
        // Context-before lines carry their own line numbers, not the match line.
        // Match on line 10 with 2 context-before lines → lines 8, 9.
        Assert.Contains("a.cs-8-before1", lines[0]);
        Assert.Contains("a.cs-9-before2", lines[1]);
        Assert.Contains("a.cs:10:1:match line", lines[2]);
        Assert.Contains("a.cs-11-after1", lines[3]);
        Assert.Contains("a.cs-12-after2", lines[4]);
    }

    [Fact]
    public void WriteSearchJson_RoundTrips()
    {
        var response = new SearchResponse(
            new Freshness(null, "abc", 0, null, true),
            Array.Empty<Match>(), false, 0, 3);

        using var sw = new StringWriter();
        OutputFormatter.WriteSearchJson(sw, response);

        Assert.Contains("\"isStale\":true", sw.ToString());
        Assert.Contains("\"matches\":[]", sw.ToString());
    }

    [Fact]
    public void WriteError_IncludesCodeAndMessage()
    {
        var err = new ErrorResponse(IndexedErrorCode.NotImplemented, "prose pending", "see Stage 3");
        using var sw = new StringWriter();
        OutputFormatter.WriteError(sw, err);

        Assert.Contains("NotImplemented", sw.ToString());
        Assert.Contains("prose pending", sw.ToString());
        Assert.Contains("see Stage 3", sw.ToString());
    }
}
