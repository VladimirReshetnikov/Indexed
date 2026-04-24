using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Indexed.Abstractions;
using Indexed.Core;
using Indexed.Extractors;
using Indexed.Targets;
using Xunit;

namespace Indexed.Core.Tests;

/// <summary>
/// Round-trip and lifecycle tests for <see cref="SqliteIndex"/>.
/// </summary>
public sealed class SqliteIndexTests : IDisposable
{
    private readonly string _tempDir;

    public SqliteIndexTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "IndexedSqlite_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private string DbPath => Path.Combine(_tempDir, "index.db");

    private long UpsertPrimaryRoot(WriterScope scope, string? rootPath = null)
    {
        var absoluteRoot = Path.GetFullPath(rootPath ?? _tempDir);
        var bindings = SqliteIndex.UpsertRoots(
            scope,
            new[] { new TargetRoot(Name: null, AbsolutePath: absoluteRoot, IsPrimary: true) });
        return bindings[TargetPathUtilities.NormalizeForComparison(absoluteRoot)];
    }

    private long UpsertFileForTest(
        WriterScope scope,
        string logicalPath,
        long mtimeUtc,
        long sizeBytes,
        byte[] sha256,
        string? language,
        long indexedAt,
        string textForTokenization)
    {
        var rootId = UpsertPrimaryRoot(scope);
        return SqliteIndex.UpsertFile(
            scope: scope,
            rootId: rootId,
            relativePath: logicalPath,
            logicalPath: logicalPath,
            mtimeUtc: mtimeUtc,
            sizeBytes: sizeBytes,
            sha256: sha256,
            language: language,
            indexedAt: indexedAt,
            textForTokenization: textForTokenization);
    }

    [Fact]
    public void OpenOrCreate_CreatesSchemaAtCurrentVersion()
    {
        using var index = SqliteIndex.OpenOrCreate(DbPath).AsSync();
        Assert.Equal(SqliteSchema.Version, index.SchemaVersion);
        Assert.True(File.Exists(DbPath));
    }

    [Fact]
    public async Task UpsertAndQuery_RoundTripsMetadata()
    {
        // Schema v2: code_fts is contentless. The trigram index still produces
        // candidates via MATCH, but the content itself is not round-trippable
        // from the index — callers rehydrate from the working tree. Here we
        // assert the candidate row surfaces and carries the sha we wrote.
        await using var index = SqliteIndex.OpenOrCreate(DbPath);
        var sha = new byte[32];
        Random.Shared.NextBytes(sha);

        await using (var scope = await index.BeginWriteAsync())
        {
            UpsertFileForTest(
                scope: scope,
                logicalPath: "src/foo.cs",
                mtimeUtc: 1,
                sizeBytes: 10,
                sha256: sha,
                language: "csharp",
                indexedAt: 2,
                textForTokenization: "public class Alpha { }");
        }

        var candidates = await index.QueryCodeCandidatesAsync("\"alp\"", default);
        Assert.Single(candidates);

        var rows = await index.GetFilesAsync(candidates, default);
        Assert.Single(rows);
        Assert.Equal("src/foo.cs", rows[0].LogicalPath);
        Assert.Equal(sha, rows[0].Sha256);
    }

    [Fact]
    public async Task SchemaMismatch_RebuildsDbOnReopen()
    {
        // Create a stale DB with a fake older schema version by directly
        // overwriting meta after the first open.
        await using (var index = SqliteIndex.OpenOrCreate(DbPath))
        {
            index.SetMeta(SqliteSchema.MetaKey_SchemaVersion, "0");

            await using var scope = await index.BeginWriteAsync();
            UpsertFileForTest(scope, "old.cs", 1, 1, new byte[32], "csharp", 1, "stale content");
        }

        // Reopening must notice the mismatch, wipe the DB, and start fresh.
        await using (var index = SqliteIndex.OpenOrCreate(DbPath))
        {
            Assert.Equal(SqliteSchema.Version, index.SchemaVersion);
            Assert.Equal(0L, index.GetFileCount());
        }
    }

    [Fact]
    public async Task MetaRoundTripsStrings()
    {
        await using var index = SqliteIndex.OpenOrCreate(DbPath);
        index.SetMeta("x", "hello");
        Assert.Equal("hello", index.GetMeta("x"));
        index.SetMeta("x", null);
        Assert.Null(index.GetMeta("x"));
    }

    [Fact]
    public async Task DeleteFile_RemovesFromFilesAndFts()
    {
        await using var index = SqliteIndex.OpenOrCreate(DbPath);

        long fileId;
        await using (var scope = await index.BeginWriteAsync())
        {
            fileId = UpsertFileForTest(
                scope, "a.cs", 1, 1, new byte[32], "csharp", 1, "zeta");
        }

        await using (var scope = await index.BeginWriteAsync())
        {
            SqliteIndex.DeleteFile(scope, fileId);
        }

        Assert.Equal(0L, index.GetFileCount());
        var candidates = await index.QueryCodeCandidatesAsync("\"zet\"", default);
        Assert.Empty(candidates);
    }

    [Fact]
    public async Task ReplaceProseSpans_ReplacesExistingRowsAndQueriesThem()
    {
        await using var index = SqliteIndex.OpenOrCreate(DbPath);
        long fileId;

        await using (var scope = await index.BeginWriteAsync())
        {
            fileId = UpsertFileForTest(
                scope,
                "src/foo.cs",
                mtimeUtc: 1,
                sizeBytes: 10,
                sha256: new byte[32],
                language: "csharp",
                indexedAt: 2,
                textForTokenization: "class Foo { }");

            SqliteIndex.ReplaceProseSpans(
                scope,
                fileId,
                new[]
                {
                    new ExtractedProseSpan(3, 4, SpanKind.XmlDoc, "needle docs\nother line"),
                });
        }

        var firstRows = await index.QueryProseCandidatesAsync("needle", "\uE000", "\uE001", default);
        var first = Assert.Single(firstRows);
        Assert.Equal("src/foo.cs", first.LogicalPath);
        Assert.Equal(SpanKind.XmlDoc, first.Kind);
        Assert.Equal(3, first.StartLine);
        Assert.Equal(4, first.EndLine);
        Assert.Contains("\uE000needle\uE001", first.Highlighted);

        await using (var scope = await index.BeginWriteAsync())
        {
            SqliteIndex.ReplaceProseSpans(
                scope,
                fileId,
                new[]
                {
                    new ExtractedProseSpan(8, 8, SpanKind.LineCommentBlock, "replacement"),
                });
        }

        var oldRows = await index.QueryProseCandidatesAsync("needle", "\uE000", "\uE001", default);
        Assert.Empty(oldRows);

        var replacementRows = await index.QueryProseCandidatesAsync("replacement", "\uE000", "\uE001", default);
        var replacement = Assert.Single(replacementRows);
        Assert.Equal(SpanKind.LineCommentBlock, replacement.Kind);
        Assert.Equal(8, replacement.StartLine);
        Assert.Equal(8, replacement.EndLine);
    }
    [Fact]
    public async Task GetAllPathsWithSha_ReturnsEveryRow()
    {
        await using var index = SqliteIndex.OpenOrCreate(DbPath);
        var sha1 = new byte[32]; sha1[0] = 1;
        var sha2 = new byte[32]; sha2[0] = 2;

        await using (var scope = await index.BeginWriteAsync())
        {
            UpsertFileForTest(scope, "a.cs", 1, 10, sha1, "csharp", 1, "alpha");
            UpsertFileForTest(scope, "b.cs", 1, 10, sha2, "csharp", 1, "beta");
        }

        var all = await index.GetAllLogicalPathsWithShaAsync(default);

        Assert.Equal(2, all.Count);
        Assert.True(all.ContainsKey("a.cs"));
        Assert.True(all.ContainsKey("b.cs"));
        Assert.Equal(sha1, all["a.cs"]);
        Assert.Equal(sha2, all["b.cs"]);
    }

    [Fact]
    public async Task LookupFileIdByLogicalPath_ReturnsIdOrNull()
    {
        await using var index = SqliteIndex.OpenOrCreate(DbPath);

        await using (var scope = await index.BeginWriteAsync())
        {
            UpsertFileForTest(scope, "found.cs", 1, 1, new byte[32], "csharp", 1, "x");
        }

        var found = index.LookupFileIdByLogicalPath("found.cs");
        Assert.NotNull(found);

        var missing = index.LookupFileIdByLogicalPath("missing.cs");
        Assert.Null(missing);
    }

    [Fact]
    public async Task BulkDeleteFiles_RemovesAll()
    {
        await using var index = SqliteIndex.OpenOrCreate(DbPath);

        long id1, id2;
        await using (var scope = await index.BeginWriteAsync())
        {
            id1 = UpsertFileForTest(scope, "x.cs", 1, 1, new byte[32], "csharp", 1, "aaa");
            var sha2 = new byte[32]; sha2[0] = 1;
            id2 = UpsertFileForTest(scope, "y.cs", 1, 1, sha2, "csharp", 1, "bbb");
        }

        Assert.Equal(2L, index.GetFileCount());

        await using (var scope = await index.BeginWriteAsync())
        {
            SqliteIndex.BulkDeleteFiles(scope, new[] { id1, id2 });
        }

        Assert.Equal(0L, index.GetFileCount());
    }

    [Fact]
    public async Task SetMeta_WithScope_CommitsAtomically()
    {
        // Scope-bound overload binds the INSERT to the scope's transaction
        // so the row only becomes visible once the scope commits.
        await using var index = SqliteIndex.OpenOrCreate(DbPath);

        await using (var scope = await index.BeginWriteAsync())
        {
            SqliteIndex.SetMeta(scope, "scope-bound", "v1");
            // A reader inside the scope should NOT see the row yet because
            // the sync reader uses a separate connection (committed-state
            // only). This is the defining property of the scope-bound
            // overload.
            Assert.Null(index.GetMeta("scope-bound"));
        }

        Assert.Equal("v1", index.GetMeta("scope-bound"));
    }

    [Fact]
    public async Task SetMeta_WithScope_RollsBackOnFail()
    {
        // Regression: the scope-bound overload must participate in the
        // scope's rollback path. Before the overload existed, callers used
        // the plain overload which committed immediately on the writer
        // connection — if a later statement in the batch failed and the
        // scope rolled back, the meta row was still present (half-applied).
        await using var index = SqliteIndex.OpenOrCreate(DbPath);

        await using (var scope = await index.BeginWriteAsync())
        {
            SqliteIndex.SetMeta(scope, "rollback-test", "v1");
            scope.Fail();
        }

        Assert.Null(index.GetMeta("rollback-test"));
    }

    [Fact]
    public async Task WriterScope_DoubleDispose_IsIdempotent()
    {
        // Regression test: before the Interlocked-flag guard, a second
        // DisposeAsync would call ReleaseWriterLock() again, permitting a
        // concurrent writer to slip in while another scope was still
        // legitimately holding the semaphore. Verify that two successive
        // disposes still leave the writer available exactly once.
        await using var index = SqliteIndex.OpenOrCreate(DbPath);

        var scope = await index.BeginWriteAsync();
        UpsertFileForTest(scope, "x.cs", 1, 1, new byte[32], "csharp", 1, "aaa");
        await scope.DisposeAsync();
        // Second dispose must be a no-op; in particular it must NOT release
        // the semaphore again.
        await scope.DisposeAsync();

        // If the double-dispose had double-released, the next BeginWriteAsync
        // would complete immediately even while another writer holds the
        // lock. Simulate the hazard: acquire one scope and verify the next
        // BeginWriteAsync does NOT complete instantly (it should wait).
        await using var held = await index.BeginWriteAsync();
        var second = index.BeginWriteAsync().AsTask();
        // Give the pending acquire a moment; if the semaphore were over-
        // released by the double-dispose it would already be completed.
        await Task.Delay(50);
        Assert.False(second.IsCompleted, "BeginWriteAsync returned while another scope was live — writer semaphore leaked");
        await held.DisposeAsync();
        await using var s = await second;
    }
}

/// <summary>Helper to keep the sync test readable despite the async-disposable API.</summary>
internal static class SqliteIndexSyncHelper
{
    public static SyncIndexHandle AsSync(this SqliteIndex index) => new(index);
}

internal sealed class SyncIndexHandle : IDisposable
{
    private readonly SqliteIndex _index;
    public SyncIndexHandle(SqliteIndex index) { _index = index; }
    public int SchemaVersion => _index.SchemaVersion;
    public void Dispose() => _index.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
