using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Abstractions;
using Indexed.Core;
using Indexed.Git;
using Indexed.Service;
using Xunit;

namespace Indexed.Service.Tests;

/// <summary>
/// Tests for <see cref="SqliteSearchBackend"/> input validation. These exercise
/// the guard clauses before a query plan is built or the executor runs.
/// </summary>
public sealed class SqliteSearchBackendTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly SqliteIndex _index;
    private readonly SqliteSearchBackend _backend;

    public SqliteSearchBackendTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "IdxBackend_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        // Create a real repo so FullScanIndexer can seed the index with at
        // least one file — needed for tests that exercise actual search execution.
        var repoPath = Path.Combine(_tempRoot, "repo");
        Directory.CreateDirectory(repoPath);
        GitProcess.RunText(repoPath, new[] { "init", "-q", "-b", "main" });
        GitProcess.RunText(repoPath, new[] { "config", "user.name", "t" });
        GitProcess.RunText(repoPath, new[] { "config", "user.email", "t@t" });
        GitProcess.RunText(repoPath, new[] { "config", "commit.gpgsign", "false" });
        File.WriteAllText(
            Path.Combine(repoPath, "a.cs"),
            """
            /// greeting prose
            class A
            {
                int needle = 1; // auto marker
            }
            """);
        File.WriteAllText(Path.Combine(repoPath, "notes.md"), "# Handbook\n\nprose-only docs.\n");
        GitProcess.RunText(repoPath, new[] { "add", "a.cs", "notes.md" });
        GitProcess.RunText(repoPath, new[] { "commit", "-q", "-m", "init" });

        var dbPath = Path.Combine(_tempRoot, "index.db");
        _index = SqliteIndex.OpenOrCreate(dbPath);

        var target = GitIndexTarget.Open(repoPath);
        var indexer = new FullScanIndexer(target, _index);
        indexer.RunAsync(progress: null, CancellationToken.None).GetAwaiter().GetResult();

        _backend = new SqliteSearchBackend(
            _index,
            () => new Freshness(null, "abc", 0, null, false),
            new FileContentProvider(repoPath));
    }

    public void Dispose()
    {
        _index.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_tempRoot, true); } catch { }
    }

    // ----- empty / null pattern -----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task EmptyPattern_ReturnsBadRequest(string? pattern)
    {
        var req = new SearchRequest(pattern!);
        var result = await _backend.SearchAsync(req, CancellationToken.None);
        Assert.NotNull(result.Error);
        Assert.Equal(IndexedErrorCode.BadRequest, result.Error!.Code);
        Assert.Contains("pattern", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ----- mode validation -----

    [Fact]
    public async Task ProseMode_FindsExtractedSpans()
    {
        var req = new SearchRequest("greeting", Mode: QueryMode.Prose);
        var result = await _backend.SearchAsync(req, CancellationToken.None);

        Assert.Null(result.Error);
        var response = Assert.IsType<SearchResponse>(result.Response);
        var match = Assert.Single(response.Matches);
        Assert.Equal("a.cs", match.Path);
        Assert.Equal(SpanKind.XmlDoc, match.Kind);
        Assert.Equal(new MatchSpan(1, 1), match.Span);
    }

    [Fact]
    public async Task AutoMode_MergesResults_AndPrefersProseOnSameLine()
    {
        var req = new SearchRequest("marker", Mode: QueryMode.Auto);
        var result = await _backend.SearchAsync(req, CancellationToken.None);

        Assert.Null(result.Error);
        var response = Assert.IsType<SearchResponse>(result.Response);
        var match = Assert.Single(response.Matches);
        Assert.Equal("a.cs", match.Path);
        Assert.Equal(4, match.Line);
        Assert.Equal(SpanKind.LineCommentBlock, match.Kind);
        Assert.Equal(new MatchSpan(4, 4), match.Span);
    }

    [Fact]
    public async Task AutoMode_WithRegex_SkipsProseAndReturnsCodeMatches()
    {
        var req = new SearchRequest("marker", Mode: QueryMode.Auto, IsRegex: true);
        var result = await _backend.SearchAsync(req, CancellationToken.None);

        Assert.Null(result.Error);
        var response = Assert.IsType<SearchResponse>(result.Response);
        var match = Assert.Single(response.Matches);
        Assert.Equal(SpanKind.Code, match.Kind);
        Assert.Null(match.Span);
    }

    [Fact]
    public async Task RelevanceSort_IsAccepted()
    {
        var req = new SearchRequest("needle", Mode: QueryMode.Code, SortBy: SortBy.Relevance);
        var result = await _backend.SearchAsync(req, CancellationToken.None);

        Assert.True(
            result.Error is null || result.Error.Code != IndexedErrorCode.NotImplemented,
            $"unexpected NotImplemented: {result.Error?.Message}");
    }

    // ----- MaxMatches -----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_001)]
    public async Task MaxMatches_OutOfRange_ReturnsBadRequest(int maxMatches)
    {
        var req = new SearchRequest("test", Mode: QueryMode.Code, MaxMatches: maxMatches);
        var result = await _backend.SearchAsync(req, CancellationToken.None);
        Assert.NotNull(result.Error);
        Assert.Equal(IndexedErrorCode.BadRequest, result.Error!.Code);
        Assert.Contains("maxMatches", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10_000)]
    public async Task MaxMatches_BoundaryValues_Accepted(int maxMatches)
    {
        var req = new SearchRequest(
            "needle",
            Mode: QueryMode.Code,
            MaxMatches: maxMatches,
            MaxMatchesPerFile: maxMatches);
        var result = await _backend.SearchAsync(req, CancellationToken.None);
        // Should not return a BadRequest error for boundary values
        Assert.True(
            result.Error is null || result.Error.Code != IndexedErrorCode.BadRequest,
            $"unexpected BadRequest: {result.Error?.Message}");
    }

    [Fact]
    public async Task InvalidProsePattern_ReturnsPatternInvalid()
    {
        var req = new SearchRequest("\"", Mode: QueryMode.Prose);
        var result = await _backend.SearchAsync(req, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal(IndexedErrorCode.PatternInvalid, result.Error!.Code);
    }

    // ----- MaxMatchesPerFile -----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task MaxMatchesPerFile_NonPositive_ReturnsBadRequest(int value)
    {
        var req = new SearchRequest("test", Mode: QueryMode.Code, MaxMatchesPerFile: value);
        var result = await _backend.SearchAsync(req, CancellationToken.None);
        Assert.NotNull(result.Error);
        Assert.Equal(IndexedErrorCode.BadRequest, result.Error!.Code);
        Assert.Contains("maxMatchesPerFile", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ----- ContextBefore / ContextAfter -----

    [Theory]
    [InlineData(-1)]
    [InlineData(51)]
    public async Task ContextBefore_OutOfRange_ReturnsBadRequest(int value)
    {
        var req = new SearchRequest("test", Mode: QueryMode.Code, ContextBefore: value);
        var result = await _backend.SearchAsync(req, CancellationToken.None);
        Assert.NotNull(result.Error);
        Assert.Equal(IndexedErrorCode.BadRequest, result.Error!.Code);
        Assert.Contains("contextBefore", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(51)]
    public async Task ContextAfter_OutOfRange_ReturnsBadRequest(int value)
    {
        var req = new SearchRequest("test", Mode: QueryMode.Code, ContextAfter: value);
        var result = await _backend.SearchAsync(req, CancellationToken.None);
        Assert.NotNull(result.Error);
        Assert.Equal(IndexedErrorCode.BadRequest, result.Error!.Code);
        Assert.Contains("contextAfter", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    public async Task Context_BoundaryValues_Accepted(int value)
    {
        var req = new SearchRequest("needle", Mode: QueryMode.Code, ContextBefore: value, ContextAfter: value);
        var result = await _backend.SearchAsync(req, CancellationToken.None);
        Assert.True(
            result.Error is null || result.Error.Code != IndexedErrorCode.BadRequest,
            $"unexpected BadRequest: {result.Error?.Message}");
    }

    // ----- TimeoutMs -----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(30_001)]
    public async Task TimeoutMs_OutOfRange_ReturnsBadRequest(int value)
    {
        var req = new SearchRequest("test", Mode: QueryMode.Code, TimeoutMs: value);
        var result = await _backend.SearchAsync(req, CancellationToken.None);
        Assert.NotNull(result.Error);
        Assert.Equal(IndexedErrorCode.BadRequest, result.Error!.Code);
        Assert.Contains("timeoutMs", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30_000)]
    public async Task TimeoutMs_BoundaryValues_Accepted(int value)
    {
        var req = new SearchRequest("needle", Mode: QueryMode.Code, TimeoutMs: value);
        var result = await _backend.SearchAsync(req, CancellationToken.None);
        Assert.True(
            result.Error is null || result.Error.Code != IndexedErrorCode.BadRequest,
            $"unexpected BadRequest: {result.Error?.Message}");
    }

    // ----- successful code search -----

    [Fact]
    public async Task CodeSearch_FindsIndexedContent()
    {
        var req = new SearchRequest("needle", Mode: QueryMode.Code);
        var result = await _backend.SearchAsync(req, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.NotNull(result.Response);
        Assert.True(result.Response!.TotalMatches > 0);

        var match = Assert.Single(result.Response.Matches);
        Assert.Equal("a.cs", match.Path);
        Assert.Contains("needle", match.Text);
    }

    // ----- cancellation -----

    [Fact]
    public async Task AlreadyCancelledToken_ThrowsOrReturnsTimeout()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var req = new SearchRequest("needle", Mode: QueryMode.Code);

        // The backend may throw OCE or return a timeout error — either is acceptable.
        try
        {
            var result = await _backend.SearchAsync(req, cts.Token);
            // If it doesn't throw, it should report a timeout/error
            Assert.NotNull(result.Error);
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }
}
