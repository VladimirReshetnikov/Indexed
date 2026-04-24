using System;
using System.IO;
using System.Threading.Tasks;
using Indexed.Abstractions;
using Indexed.Core;
using Indexed.Extractors;
using Indexed.Targets;
using Xunit;

namespace Indexed.Core.Tests;

/// <summary>
/// End-to-end tests for <see cref="ProseQueryExecutor"/> using real SQLite
/// prose rows. These pin line mapping, filters, and span metadata.
/// </summary>
public sealed class ProseQueryExecutorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public ProseQueryExecutorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "IndexedProseExec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "index.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<SqliteIndex> SeedAsync(params SeededSpan[] spans)
    {
        var index = SqliteIndex.OpenOrCreate(_dbPath);
        await using var scope = await index.BeginWriteAsync();
        var absoluteRoot = Path.GetFullPath(_tempDir);
        var bindings = SqliteIndex.UpsertRoots(
            scope,
            new[] { new TargetRoot(Name: null, AbsolutePath: absoluteRoot, IsPrimary: true) });
        var rootId = bindings[TargetPathUtilities.NormalizeForComparison(absoluteRoot)];

        foreach (var seeded in spans)
        {
            var fileId = SqliteIndex.UpsertFile(
                scope: scope,
                rootId: rootId,
                relativePath: seeded.Path,
                logicalPath: seeded.Path,
                mtimeUtc: 1,
                sizeBytes: seeded.Content.Length,
                sha256: new byte[32],
                language: null,
                indexedAt: 1,
                textForTokenization: seeded.Content);

            SqliteIndex.ReplaceProseSpans(
                scope,
                fileId,
                new[]
                {
                    new ExtractedProseSpan(seeded.StartLine, seeded.EndLine, seeded.Kind, seeded.Content),
                });
        }

        return index;
    }

    [Fact]
    public async Task ExecuteAsync_MapsSpanLineColumnAndContext()
    {
        await using var index = await SeedAsync(
            new SeededSpan(
                Path: "src/service.cs",
                StartLine: 10,
                EndLine: 12,
                Kind: SpanKind.XmlDoc,
                Content: "first context\nneedle line\nafter context"));

        var request = new SearchRequest(
            "needle",
            Mode: QueryMode.Prose,
            ContextBefore: 1,
            ContextAfter: 1);

        var result = await new ProseQueryExecutor(index).ExecuteAsync(request, default);

        var ranked = Assert.Single(result.Matches);
        var match = ranked.Match;
        Assert.Equal("src/service.cs", match.Path);
        Assert.Equal(11, match.Line);
        Assert.Equal(1, match.Column);
        Assert.Equal("needle line", match.Text);
        Assert.Equal(SpanKind.XmlDoc, match.Kind);
        Assert.Equal(new MatchSpan(10, 12), match.Span);
        Assert.Equal(new[] { "first context" }, match.ContextBefore);
        Assert.Equal(new[] { "after context" }, match.ContextAfter);
    }

    [Fact]
    public async Task ExecuteAsync_HonorsPathGlobAndKindFilter()
    {
        await using var index = await SeedAsync(
            new SeededSpan(
                Path: "docs/readme.md",
                StartLine: 1,
                EndLine: 2,
                Kind: SpanKind.Markdown,
                Content: "needle docs\nmore"),
            new SeededSpan(
                Path: "src/app.cs",
                StartLine: 5,
                EndLine: 5,
                Kind: SpanKind.LineCommentBlock,
                Content: "needle comment"));

        var request = new SearchRequest(
            "needle",
            Mode: QueryMode.Prose,
            PathGlob: "docs/**",
            KindFilter: new[] { SpanKind.Markdown });

        var result = await new ProseQueryExecutor(index).ExecuteAsync(request, default);

        var ranked = Assert.Single(result.Matches);
        Assert.Equal("docs/readme.md", ranked.Match.Path);
        Assert.Equal(SpanKind.Markdown, ranked.Match.Kind);
    }

    [Fact]
    public async Task ExecuteAsync_ExcludeGlob_RemovesMatchingPaths()
    {
        await using var index = await SeedAsync(
            new SeededSpan(
                Path: "docs/readme.md",
                StartLine: 1,
                EndLine: 1,
                Kind: SpanKind.Markdown,
                Content: "needle docs"));

        var request = new SearchRequest(
            "needle",
            Mode: QueryMode.Prose,
            ExcludeGlob: new[] { "docs/**" });

        var result = await new ProseQueryExecutor(index).ExecuteAsync(request, default);
        Assert.Empty(result.Matches);
    }

    private sealed record SeededSpan(
        string Path,
        int StartLine,
        int EndLine,
        SpanKind Kind,
        string Content);
}
