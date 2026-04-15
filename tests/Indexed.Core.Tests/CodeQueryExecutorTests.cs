using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Indexed.Abstractions;
using Indexed.Core;
using Xunit;

namespace Indexed.Core.Tests;

/// <summary>
/// End-to-end tests for <see cref="CodeQueryExecutor"/>: seed a SQLite index,
/// run planner + executor, verify match shape and cap behavior.
/// </summary>
public sealed class CodeQueryExecutorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public CodeQueryExecutorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "IndexedExec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "index.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<SqliteIndex> SeedAsync((string path, string content)[] files)
    {
        var index = SqliteIndex.OpenOrCreate(_dbPath);
        await using var scope = await index.BeginWriteAsync();
        foreach (var (path, content) in files)
        {
            SqliteIndex.UpsertFile(
                scope: scope,
                path: path,
                mtimeUtc: 1,
                sizeBytes: content.Length,
                sha256: new byte[32],
                language: null,
                indexedAt: 1,
                content: content);
        }
        return index;
    }

    [Fact]
    public async Task LiteralMatch_ReturnsLineAndColumn()
    {
        await using var index = await SeedAsync(new[]
        {
            ("a.cs", "line one\nhello World\nline three\n"),
        });

        var request = new SearchRequest("hello", Mode: QueryMode.Code);
        var plan = CodeQueryPlanner.Build(request);
        var result = await new CodeQueryExecutor(index).ExecuteAsync(request, plan, default);

        var match = Assert.Single(result.Matches);
        Assert.Equal("a.cs", match.Path);
        Assert.Equal(2, match.Line);
        Assert.Equal(1, match.Column);
        Assert.Equal("hello World", match.Text);
    }

    [Fact]
    public async Task RegexMatch_ReturnsAllMatches()
    {
        await using var index = await SeedAsync(new[]
        {
            ("a.cs", "foo\nfoo bar\nfoo baz\n"),
        });

        var request = new SearchRequest(@"foo", IsRegex: true, Mode: QueryMode.Code);
        var plan = CodeQueryPlanner.Build(request);
        var result = await new CodeQueryExecutor(index).ExecuteAsync(request, plan, default);

        Assert.Equal(3, result.Matches.Count);
        Assert.Equal(new[] { 1, 2, 3 }, result.Matches.Select(m => m.Line));
    }

    [Fact]
    public async Task Crlf_LineNumbersMatchLfExpectation()
    {
        await using var index = await SeedAsync(new[]
        {
            ("a.cs", "line one\r\nhello\r\nline three\r\n"),
        });

        var request = new SearchRequest("hello", Mode: QueryMode.Code);
        var plan = CodeQueryPlanner.Build(request);
        var result = await new CodeQueryExecutor(index).ExecuteAsync(request, plan, default);

        var match = Assert.Single(result.Matches);
        Assert.Equal(2, match.Line);
        Assert.Equal("hello", match.Text);
    }

    [Fact]
    public async Task ContextBeforeAndAfter_AttachedToMatch()
    {
        await using var index = await SeedAsync(new[]
        {
            ("a.cs", "one\ntwo\nTARGET\nfour\nfive\n"),
        });

        var request = new SearchRequest("TARGET", Mode: QueryMode.Code, CaseSensitive: true,
            ContextBefore: 2, ContextAfter: 2);
        var plan = CodeQueryPlanner.Build(request);
        var result = await new CodeQueryExecutor(index).ExecuteAsync(request, plan, default);

        var match = Assert.Single(result.Matches);
        Assert.Equal(new[] { "one", "two" }, match.ContextBefore);
        Assert.Equal(new[] { "four", "five" }, match.ContextAfter);
    }

    [Fact]
    public async Task PathGlobFilter_NarrowsCandidates()
    {
        await using var index = await SeedAsync(new[]
        {
            ("src/foo.cs", "hello"),
            ("tests/bar.cs", "hello"),
        });

        var request = new SearchRequest("hello", Mode: QueryMode.Code, PathGlob: "src/**");
        var plan = CodeQueryPlanner.Build(request);
        var result = await new CodeQueryExecutor(index).ExecuteAsync(request, plan, default);

        var match = Assert.Single(result.Matches);
        Assert.Equal("src/foo.cs", match.Path);
    }

    [Fact]
    public async Task ExcludeGlob_RemovesCandidates()
    {
        await using var index = await SeedAsync(new[]
        {
            ("src/foo.cs", "hello"),
            ("src/bar.generated.cs", "hello"),
        });

        var request = new SearchRequest(
            "hello", Mode: QueryMode.Code,
            ExcludeGlob: new[] { "**/*.generated.cs" });
        var plan = CodeQueryPlanner.Build(request);
        var result = await new CodeQueryExecutor(index).ExecuteAsync(request, plan, default);

        var match = Assert.Single(result.Matches);
        Assert.Equal("src/foo.cs", match.Path);
    }

    [Fact]
    public async Task MaxMatchesPerFile_TruncatesAndSetsFlag()
    {
        // 10 lines each containing "hit", per-file cap=3.
        var content = string.Join("\n", Enumerable.Range(0, 10).Select(i => $"line {i} hit"));
        await using var index = await SeedAsync(new[] { ("a.cs", content) });

        var request = new SearchRequest("hit", Mode: QueryMode.Code, MaxMatchesPerFile: 3);
        var plan = CodeQueryPlanner.Build(request);
        var result = await new CodeQueryExecutor(index).ExecuteAsync(request, plan, default);

        Assert.Equal(3, result.Matches.Count);
        Assert.True(result.Truncated);
        Assert.Equal(10, result.ReportedTotal);
    }

    [Fact]
    public async Task MaxMatchesGlobal_TruncatesAcrossFiles()
    {
        await using var index = await SeedAsync(new[]
        {
            ("a.cs", "hit\nhit\nhit\n"),
            ("b.cs", "hit\nhit\n"),
        });

        var request = new SearchRequest("hit", Mode: QueryMode.Code, MaxMatches: 2, MaxMatchesPerFile: 10);
        var plan = CodeQueryPlanner.Build(request);
        var result = await new CodeQueryExecutor(index).ExecuteAsync(request, plan, default);

        Assert.Equal(2, result.Matches.Count);
        Assert.True(result.Truncated);
    }
}
