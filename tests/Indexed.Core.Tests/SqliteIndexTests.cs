using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Indexed.Core;
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
            SqliteIndex.UpsertFile(
                scope: scope,
                path: "src/foo.cs",
                mtimeUtc: 1,
                sizeBytes: 10,
                sha256: sha,
                language: "csharp",
                indexedAt: 2,
                content: "public class Alpha { }");
        }

        var candidates = await index.QueryCodeCandidatesAsync("\"alp\"", default);
        Assert.Single(candidates);

        var rows = await index.GetFilesAsync(candidates, default);
        Assert.Single(rows);
        Assert.Equal("src/foo.cs", rows[0].Path);
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
            SqliteIndex.UpsertFile(scope, "old.cs", 1, 1, new byte[32], "csharp", 1, "stale content");
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
            fileId = SqliteIndex.UpsertFile(
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
    public async Task GetAllPathsWithSha_ReturnsEveryRow()
    {
        await using var index = SqliteIndex.OpenOrCreate(DbPath);
        var sha1 = new byte[32]; sha1[0] = 1;
        var sha2 = new byte[32]; sha2[0] = 2;

        await using (var scope = await index.BeginWriteAsync())
        {
            SqliteIndex.UpsertFile(scope, "a.cs", 1, 10, sha1, "csharp", 1, "alpha");
            SqliteIndex.UpsertFile(scope, "b.cs", 1, 10, sha2, "csharp", 1, "beta");
        }

        var all = await index.GetAllPathsWithShaAsync(default);

        Assert.Equal(2, all.Count);
        Assert.True(all.ContainsKey("a.cs"));
        Assert.True(all.ContainsKey("b.cs"));
        Assert.Equal(sha1, all["a.cs"]);
        Assert.Equal(sha2, all["b.cs"]);
    }

    [Fact]
    public async Task LookupFileIdByPath_ReturnsIdOrNull()
    {
        await using var index = SqliteIndex.OpenOrCreate(DbPath);

        await using (var scope = await index.BeginWriteAsync())
        {
            SqliteIndex.UpsertFile(scope, "found.cs", 1, 1, new byte[32], "csharp", 1, "x");
        }

        var found = index.LookupFileIdByPath("found.cs");
        Assert.NotNull(found);

        var missing = index.LookupFileIdByPath("missing.cs");
        Assert.Null(missing);
    }

    [Fact]
    public async Task BulkDeleteFiles_RemovesAll()
    {
        await using var index = SqliteIndex.OpenOrCreate(DbPath);

        long id1, id2;
        await using (var scope = await index.BeginWriteAsync())
        {
            id1 = SqliteIndex.UpsertFile(scope, "x.cs", 1, 1, new byte[32], "csharp", 1, "aaa");
            var sha2 = new byte[32]; sha2[0] = 1;
            id2 = SqliteIndex.UpsertFile(scope, "y.cs", 1, 1, sha2, "csharp", 1, "bbb");
        }

        Assert.Equal(2L, index.GetFileCount());

        await using (var scope = await index.BeginWriteAsync())
        {
            SqliteIndex.BulkDeleteFiles(scope, new[] { id1, id2 });
        }

        Assert.Equal(0L, index.GetFileCount());
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
