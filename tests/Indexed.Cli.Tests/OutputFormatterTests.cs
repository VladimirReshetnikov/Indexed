using System;
using System.IO;
using Indexed.Abstractions;
using Indexed.Cli;
using Indexed.Service;
using Indexed.Targets;
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

    [Fact]
    public void WriteStatusText_IncludesAllRootsForDirectorySet()
    {
        var primary = new TargetRoot("docs", @"C:\src\docs", true);
        var secondary = new TargetRoot("sdk", @"C:\src\sdk", false);
        var response = new StatusResponse(
            DaemonVersion: "1.0.0",
            SchemaVersion: 3,
            Pid: 1234,
            RepoRoot: null,
            RepoId: null,
            StartedAt: DateTimeOffset.Parse("2026-04-23T19:00:00Z"),
            Freshness: new Freshness(null, null, 0, null, false, IndexedRevisionToken: null, CurrentRevisionToken: null, RevisionKind: RevisionKind.None, LastReconciliationAt: DateTimeOffset.Parse("2026-04-23T19:05:00Z")),
            Optimizer: null,
            TargetKind: TargetKind.DirectorySet,
            TargetId: "abcdef123456",
            Roots: new[] { primary, secondary },
            PrimaryRoot: primary);

        using var sw = new StringWriter();
        OutputFormatter.WriteStatusText(sw, response);

        var text = sw.ToString();
        Assert.Contains(@"root    docs=C:\src\docs", text);
        Assert.Contains(@"root    sdk=C:\src\sdk", text);
        Assert.Contains("recon   2026-04-23T19:05:00.0000000+00:00", text);
    }

    [Fact]
    public void WriteDaemonsText_ListsRoots()
    {
        var daemon = new DaemonInfo(
            Port: 43123,
            Pid: 2222,
            TargetKind: TargetKind.DirectorySet,
            TargetId: "abc123def456",
            Roots: new[]
            {
                new TargetRoot("docs", @"C:\src\docs", true),
                new TargetRoot("sdk", @"C:\src\sdk", false),
            },
            PrimaryRoot: new TargetRoot("docs", @"C:\src\docs", true),
            RepoRoot: null,
            RepoId: null,
            StartedAt: DateTimeOffset.Parse("2026-04-23T19:00:00Z"),
            DaemonVersion: "1.0.0",
            ShutdownToken: "token");

        using var sw = new StringWriter();
        OutputFormatter.WriteDaemonsText(sw, new[] { daemon });

        var text = sw.ToString();
        Assert.Contains("DirectorySet abc123def456 pid=2222", text);
        Assert.Contains(@"root docs=C:\src\docs primary", text);
        Assert.Contains(@"root sdk=C:\src\sdk", text);
    }
}
