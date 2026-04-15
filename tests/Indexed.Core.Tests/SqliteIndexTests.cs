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
    public async Task UpsertAndQuery_RoundTripsContent()
    {
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
        Assert.Contains("Alpha", rows[0].Content);
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
