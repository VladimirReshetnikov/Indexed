using System;
using System.IO;
using System.Threading.Tasks;
using Indexed.Core;
using Indexed.Targets;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Indexed.Core.Tests;

/// <summary>
/// Schema-v2 invariants for the contentless <c>code_fts</c> table: confirm the
/// FTS5 shadow <c>_content</c> table is absent (the whole point of the
/// size-reduction work — see <c>Indexed-Size-Reduction-SafeNearTerm-Plan.md
/// §Workstream C</c>) while <c>MATCH</c> still produces candidates.
/// </summary>
public sealed class SqliteIndexContentlessTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public SqliteIndexContentlessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "IndexedContentless_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "index.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private long UpsertPrimaryRoot(WriterScope scope)
    {
        var absoluteRoot = Path.GetFullPath(_tempDir);
        var bindings = SqliteIndex.UpsertRoots(
            scope,
            new[] { new TargetRoot(Name: null, AbsolutePath: absoluteRoot, IsPrimary: true) });
        return bindings[TargetPathUtilities.NormalizeForComparison(absoluteRoot)];
    }

    [Fact]
    public async Task CodeFts_IsContentless_NoContentShadowTable()
    {
        // Create a fresh DB at the current schema version and seed one row.
        await using (var index = SqliteIndex.OpenOrCreate(_dbPath))
        {
            await using var scope = await index.BeginWriteAsync();
            var rootId = UpsertPrimaryRoot(scope);
            SqliteIndex.UpsertFile(
                scope, rootId, "foo.cs", "foo.cs", 1, 5, new byte[32], "csharp", 1, "alpha");
        }

        // Inspect the catalog through a plain reader connection. FTS5's
        // external-content ("content = ''") mode skips creating the
        // `<fts>_content` shadow table — which is the big storage win we're
        // asserting here.
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using var conn = new SqliteConnection(csb.ConnectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='code_fts_content';";
        var match = cmd.ExecuteScalar();

        Assert.Null(match);
    }

    [Fact]
    public async Task Match_StillReturnsCandidates_OnContentlessFts()
    {
        // The shadow content table is gone, but the trigram index still has
        // posting lists — FTS5 MATCH must produce the same candidate rowid.
        await using var index = SqliteIndex.OpenOrCreate(_dbPath);
        long id;
        await using (var scope = await index.BeginWriteAsync())
        {
            var rootId = UpsertPrimaryRoot(scope);
            id = SqliteIndex.UpsertFile(
                scope, rootId, "foo.cs", "foo.cs", 1, 22, new byte[32], "csharp", 1, "public class Alpha { }");
        }

        var candidates = await index.QueryCodeCandidatesAsync("\"alp\"", default);
        Assert.Single(candidates);
        Assert.Equal(id, candidates[0]);

        var rows = await index.GetFilesAsync(candidates, default);
        var row = Assert.Single(rows);
        Assert.Equal("foo.cs", row.LogicalPath);
        // Contentless: no Content column on the projection.
        Assert.Equal(32, row.Sha256.Length);
    }

    [Fact]
    public async Task Delete_RemovesCandidate_OnContentlessFts()
    {
        // contentless_delete = 1 is required for DELETE to work against a
        // contentless FTS5 table — this test pins that option in the DDL.
        await using var index = SqliteIndex.OpenOrCreate(_dbPath);
        long id;
        await using (var scope = await index.BeginWriteAsync())
        {
            var rootId = UpsertPrimaryRoot(scope);
            id = SqliteIndex.UpsertFile(
                scope, rootId, "foo.cs", "foo.cs", 1, 5, new byte[32], "csharp", 1, "zetazeta");
        }

        // Sanity: MATCH hits before delete.
        var before = await index.QueryCodeCandidatesAsync("\"zet\"", default);
        Assert.Single(before);

        await using (var scope = await index.BeginWriteAsync())
        {
            SqliteIndex.DeleteFile(scope, id);
        }

        // Post-delete: no candidate.
        var after = await index.QueryCodeCandidatesAsync("\"zet\"", default);
        Assert.Empty(after);
    }
}
