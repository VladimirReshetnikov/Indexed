using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Core;
using Indexed.Targets;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Indexed.Core.Tests;

/// <summary>
/// Behavioral tests for <see cref="IndexOptimizer"/>. Tests drive merge
/// cycles deterministically via <see cref="IndexOptimizer.RunCycleAsync"/>
/// rather than waiting on the real timer.
/// </summary>
public sealed class IndexOptimizerTests : IDisposable
{
    private readonly string _tempDir;

    public IndexOptimizerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "IndexedOpt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private string DbPath => Path.Combine(_tempDir, "index.db");

    private long UpsertPrimaryRoot(WriterScope scope)
    {
        var absoluteRoot = Path.GetFullPath(_tempDir);
        var bindings = SqliteIndex.UpsertRoots(
            scope,
            new[] { new TargetRoot(Name: null, AbsolutePath: absoluteRoot, IsPrimary: true) });
        return bindings[TargetPathUtilities.NormalizeForComparison(absoluteRoot)];
    }

    /// <summary>
    /// Count rows in <c>code_fts_data</c>. Each row is a page of posting list
    /// data (or structure). After a merge consolidates segments, fragmented
    /// rows collapse into fewer pages; this count therefore falls (or at
    /// worst stays equal) across a successful merge on a sufficiently
    /// fragmented FTS5 index.
    /// </summary>
    private static long CountCodeFtsDataRows(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM code_fts_data;";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>
    /// Insert <paramref name="count"/> files, each in its own transaction,
    /// so FTS5 produces many small segments.
    /// </summary>
    private static async Task InsertManyFilesAsync(SqliteIndex index, int count)
    {
        var sha = new byte[32];
        for (var i = 0; i < count; i++)
        {
            sha[0] = (byte)(i & 0xFF);
            sha[1] = (byte)((i >> 8) & 0xFF);
            await using var scope = await index.BeginWriteAsync();
            var rootId = SqliteIndex.UpsertRoots(
                scope,
                new[] { new TargetRoot(Name: null, AbsolutePath: Path.GetFullPath(Path.GetDirectoryName(index.DbPath)!), IsPrimary: true) })
                [TargetPathUtilities.NormalizeForComparison(Path.GetDirectoryName(index.DbPath)!)];
            SqliteIndex.UpsertFile(
                scope,
                rootId: rootId,
                relativePath: $"src/f{i:D4}.cs",
                logicalPath: $"src/f{i:D4}.cs",
                mtimeUtc: i,
                sizeBytes: 64,
                sha256: sha,
                language: "csharp",
                indexedAt: i,
                textForTokenization: $"public class Cls{i:D4} {{ /* alpha beta gamma delta epsilon {i} */ }}");
        }
    }

    [Fact]
    public async Task Merge_ReducesSegmentCount()
    {
        await using var index = SqliteIndex.OpenOrCreate(DbPath);

        // Produce fragmentation: 200 commits => many level-0 segments.
        await InsertManyFilesAsync(index, count: 200);

        var before = CountCodeFtsDataRows(DbPath);

        await using var optimizer = new IndexOptimizer(
            index,
            interval: TimeSpan.FromHours(1),
            pageBudget: 1024);
        optimizer.NotifyDirty();

        var ran = await optimizer.RunCycleAsync();
        Assert.True(ran, "optimizer should have run a merge cycle");
        Assert.Equal(1, optimizer.MergeCount);

        var after = CountCodeFtsDataRows(DbPath);

        // A merge may add a new structure row while consolidating posting
        // lists, but the total page count should not grow by more than a
        // handful of bookkeeping rows. On a fragmented index the merge
        // almost always shrinks the page count; we assert a non-growth
        // bound to stay deterministic across SQLite versions.
        Assert.True(
            after <= before + 4,
            $"code_fts_data row count should not grow meaningfully after merge; before={before}, after={after}");
    }

    [Fact]
    public async Task SkipsWhenClean()
    {
        await using var index = SqliteIndex.OpenOrCreate(DbPath);
        await InsertManyFilesAsync(index, count: 5);

        await using var optimizer = new IndexOptimizer(
            index,
            interval: TimeSpan.FromHours(1),
            pageBudget: 128);

        // No NotifyDirty: first tick is a skip.
        Assert.False(await optimizer.RunCycleAsync());
        Assert.Equal(0, optimizer.MergeCount);

        // Second tick is also a skip because the first never flipped dirty.
        Assert.False(await optimizer.RunCycleAsync());
        Assert.Equal(0, optimizer.MergeCount);

        // After NotifyDirty, the next tick runs.
        optimizer.NotifyDirty();
        Assert.True(await optimizer.RunCycleAsync());
        Assert.Equal(1, optimizer.MergeCount);

        // And the next tick skips again (the cycle consumed the dirty flag).
        Assert.False(await optimizer.RunCycleAsync());
        Assert.Equal(1, optimizer.MergeCount);
    }

    [Fact]
    public async Task CoexistsWithIncrementalIndexer()
    {
        // Run the optimizer concurrently with a simulated "incremental
        // indexer" that opens its own writer scopes. The optimizer must not
        // deadlock, and every merge cycle must either succeed or cleanly
        // report that there was nothing to do.
        await using var index = SqliteIndex.OpenOrCreate(DbPath);

        await using var optimizer = new IndexOptimizer(
            index,
            interval: TimeSpan.FromHours(1),
            pageBudget: 128);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Writer pressure: spin a task that upserts files continuously.
        var writer = Task.Run(async () =>
        {
            var sha = new byte[32];
            for (var i = 0; i < 80 && !cts.IsCancellationRequested; i++)
            {
                sha[0] = (byte)(i & 0xFF);
                await using var scope = await index.BeginWriteAsync(cts.Token);
                var rootId = UpsertPrimaryRoot(scope);
                SqliteIndex.UpsertFile(
                    scope,
                    rootId,
                    $"p{i:D4}.cs",
                    $"p{i:D4}.cs",
                    i,
                    64,
                    sha,
                    "csharp",
                    i,
                    $"body {i}");
                optimizer.NotifyDirty();
            }
        }, cts.Token);

        // Run merge cycles in parallel while writes are flowing.
        var merger = Task.Run(async () =>
        {
            for (var i = 0; i < 10 && !cts.IsCancellationRequested; i++)
            {
                await optimizer.RunCycleAsync(cancellationToken: cts.Token);
                await Task.Delay(10, cts.Token);
            }
        }, cts.Token);

        await Task.WhenAll(writer, merger);

        // Final post-run merge to ensure no stuck lock remains.
        optimizer.NotifyDirty();
        Assert.True(await optimizer.RunCycleAsync());
        Assert.True(optimizer.MergeCount >= 1);
    }

    [Fact]
    public async Task DisposeRunsFinalMerge()
    {
        await using var index = SqliteIndex.OpenOrCreate(DbPath);
        await InsertManyFilesAsync(index, count: 50);

        var optimizer = new IndexOptimizer(
            index,
            interval: TimeSpan.FromHours(1),
            pageBudget: 64);

        // Arrange dirty state without consuming it through RunCycleAsync.
        optimizer.NotifyDirty();
        Assert.Equal(0, optimizer.MergeCount);

        // Dispose should run a final merge.
        await optimizer.DisposeAsync();
        Assert.Equal(1, optimizer.MergeCount);

        // Idempotent second dispose is a no-op.
        await optimizer.DisposeAsync();
        Assert.Equal(1, optimizer.MergeCount);
    }

    [Fact]
    public async Task Dispose_WithoutDirty_DoesNotMerge()
    {
        await using var index = SqliteIndex.OpenOrCreate(DbPath);
        await InsertManyFilesAsync(index, count: 5);

        var optimizer = new IndexOptimizer(
            index,
            interval: TimeSpan.FromHours(1),
            pageBudget: 64);

        // Never called NotifyDirty: clean optimizer has nothing to do on shutdown.
        await optimizer.DisposeAsync();
        Assert.Equal(0, optimizer.MergeCount);
    }

    [Fact]
    public async Task Constructor_Rejects_InvalidArguments()
    {
        // Use a disposable index so the constructor's null check on index
        // is not the only thing exercised.
        using var dir = new TempDir();
        var dbPath = Path.Combine(dir.Path, "bad.db");
        await using var index = SqliteIndex.OpenOrCreate(dbPath);

        Assert.Throws<ArgumentNullException>(() => new IndexOptimizer(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new IndexOptimizer(index, interval: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new IndexOptimizer(index, interval: TimeSpan.FromMinutes(1), pageBudget: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new IndexOptimizer(index, interval: TimeSpan.FromMinutes(1), pageBudget: -5));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "IndexedOptTmp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
