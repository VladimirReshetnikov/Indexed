using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Abstractions;
using Indexed.Core;
using Xunit;

namespace Indexed.Core.Tests;

/// <summary>
/// Disk-read snippet-rehydration behavior for <see cref="CodeQueryExecutor"/>
/// under schema v2. The index is a candidate oracle; the actual match text
/// comes from the live working tree. These tests pin the three staleness
/// classes the executor must handle correctly:
/// <list type="bullet">
///   <item><description>Live edit — the disk text diverges from the indexed text but the
///   trigram index still produces a candidate. The returned snippet must be
///   the <em>new</em> on-disk text.</description></item>
///   <item><description>Missing file — the indexer wrote a row, then the file was
///   deleted/unreadable. The executor must drop the candidate (no phantom
///   match) and enqueue a <see cref="FileChanged"/> repair.</description></item>
///   <item><description>Oversize file — the on-disk file grew past
///   <see cref="FullScanIndexer.MaxIndexableFileBytes"/>. The executor must
///   skip it rather than pretending to scan it (preserves parity with
///   at-index-time behavior).</description></item>
/// </list>
/// </summary>
public sealed class CodeQueryExecutorDiskReadTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _repoRoot;
    private readonly string _dbPath;

    public CodeQueryExecutorDiskReadTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "IndexedExecDisk_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _repoRoot = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(_repoRoot);
        _dbPath = Path.Combine(_tempDir, "index.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<SqliteIndex> SeedAsync(string relPath, string content)
    {
        var index = SqliteIndex.OpenOrCreate(_dbPath);
        await using var scope = await index.BeginWriteAsync();
        SqliteIndex.UpsertFile(
            scope,
            relPath,
            mtimeUtc: 1,
            sizeBytes: content.Length,
            sha256: new byte[32],
            language: null,
            indexedAt: 1,
            content: content);

        var full = Path.Combine(_repoRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return index;
    }

    [Fact]
    public async Task ReturnsLiveSnippetAfterEdit()
    {
        // Index with one set of words, then rewrite the file on disk so the
        // trigrams still match (overlap between old and new text) but the
        // line content is different. The executor should report the new text.
        // Overlap: "hello" on both sides is what keeps the candidate alive
        // for the trigram "hel"; the "BRAND_NEW" marker is only on disk.
        await using var index = await SeedAsync(
            "a.cs",
            "hello old_marker\n");

        var diskFull = Path.Combine(_repoRoot, "a.cs");
        File.WriteAllText(diskFull, "hello BRAND_NEW\n");

        var request = new SearchRequest("hello", Mode: QueryMode.Code);
        var plan = CodeQueryPlanner.Build(request);
        var executor = new CodeQueryExecutor(index, new FileContentProvider(_repoRoot));
        var result = await executor.ExecuteAsync(request, plan, default);

        var match = Assert.Single(result.Matches);
        Assert.Equal("a.cs", match.Path);
        Assert.Equal("hello BRAND_NEW", match.Text);
        Assert.DoesNotContain("old_marker", match.Text);
    }

    [Fact]
    public async Task MissingFile_IsSkippedAndRepaired()
    {
        // Index, delete from disk, then search. The trigram posting list
        // still knows about the file, but the disk read returns null — the
        // executor must NOT produce a phantom match and must enqueue a
        // repair so the incremental indexer converges.
        await using var index = await SeedAsync("gone.cs", "alpha beta");

        var diskFull = Path.Combine(_repoRoot, "gone.cs");
        File.Delete(diskFull);

        using var repairQueue = new DebouncingEventQueue();
        var request = new SearchRequest("alpha", Mode: QueryMode.Code);
        var plan = CodeQueryPlanner.Build(request);
        var executor = new CodeQueryExecutor(
            index, new FileContentProvider(_repoRoot), repairQueue);
        var result = await executor.ExecuteAsync(request, plan, default);

        Assert.Empty(result.Matches);
        Assert.Equal(0, result.ReportedTotal);

        // One FileChanged("gone.cs") should now be queued for the incremental
        // indexer. Pull the debounced batch back out — the queue emits after
        // the batch window even with just one pending event.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var batch = await repairQueue.DequeueAsync(cts.Token);
        var repair = Assert.IsType<FileChanged>(batch.Single());
        Assert.Equal("gone.cs", repair.RelativePath);
    }

    [Fact]
    public async Task OversizeFile_IsSkipped()
    {
        // Write a small file into the index, then grow it on disk past the
        // MaxIndexableFileBytes ceiling. The executor must refuse to scan
        // it (matching at-index-time behavior), so no match is produced.
        // Use a marker the indexer saw to guarantee the candidate ID is in
        // the trigram posting list.
        const string marker = "UNIQUE_OVERSIZE_MARKER";
        await using var index = await SeedAsync("big.cs", marker);

        var diskFull = Path.Combine(_repoRoot, "big.cs");

        // Build a payload past the limit. Use a byte array (1 MiB blocks of
        // the marker text) so growing to >50 MiB is quick. The marker is
        // still present at offset 0, so if the executor scanned it anyway
        // we'd see a false match.
        var blockSize = 1024 * 1024;
        var block = new string('X', blockSize);
        using (var fs = File.Create(diskFull))
        using (var writer = new StreamWriter(fs))
        {
            writer.Write(marker);
            writer.Write('\n');
            for (var i = 0; i < FullScanIndexer.MaxIndexableFileBytes / blockSize + 1; i++)
            {
                writer.Write(block);
            }
        }
        Assert.True(new FileInfo(diskFull).Length > FullScanIndexer.MaxIndexableFileBytes);

        var request = new SearchRequest(marker, Mode: QueryMode.Code);
        var plan = CodeQueryPlanner.Build(request);
        var executor = new CodeQueryExecutor(index, new FileContentProvider(_repoRoot));
        var result = await executor.ExecuteAsync(request, plan, default);

        Assert.Empty(result.Matches);
    }
}
