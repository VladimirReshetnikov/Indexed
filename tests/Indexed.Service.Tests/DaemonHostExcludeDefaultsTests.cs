using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Core;
using Indexed.Git;
using Indexed.Service;
using Xunit;

namespace Indexed.Service.Tests;

/// <summary>
/// Integration tests for the default-exclude-globs feature:
/// <see cref="DaemonOptions.UseDefaultIndexExcludes"/> and
/// <see cref="ExcludeFilter.DefaultBinaryAdjacentGlobs"/>.
/// </summary>
public sealed class DaemonHostExcludeDefaultsTests : IDisposable
{
    private readonly string _tempRoot;

    public DaemonHostExcludeDefaultsTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(), "IdxDefExcl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, true); } catch { }
    }

    // ----- helpers -----

    private string MakeRepo(params (string rel, string content)[] files)
    {
        var path = Path.Combine(_tempRoot, "repo_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        GitProcess.RunText(path, new[] { "init", "-q", "-b", "main" });
        GitProcess.RunText(path, new[] { "config", "user.name", "t" });
        GitProcess.RunText(path, new[] { "config", "user.email", "t@t" });
        GitProcess.RunText(path, new[] { "config", "commit.gpgsign", "false" });

        foreach (var (rel, content) in files)
        {
            var full = Path.Combine(path, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            GitProcess.RunText(path, new[] { "add", rel });
        }

        GitProcess.RunText(path, new[] { "commit", "-q", "-m", "init" });
        return path;
    }

    private static async Task<SqliteIndex> RunFullScanAsync(
        string repoPath,
        bool useDefaultExcludes,
        string[]? extraExcludes = null)
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "idx_test_" + Guid.NewGuid().ToString("N") + ".db");

        var index = SqliteIndex.OpenOrCreate(dbPath);
        try
        {
            var opts = new DaemonOptions
            {
                RepoRoot = repoPath,
                UseDefaultIndexExcludes = useDefaultExcludes,
                IndexExcludeGlobs = extraExcludes,
            };

            // Replicate DaemonHost's effective-excludes merge.
            var effectiveExcludes = opts.UseDefaultIndexExcludes
                ? ExcludeFilter.Combine(opts.IndexExcludeGlobs, ExcludeFilter.DefaultBinaryAdjacentGlobs)
                : opts.IndexExcludeGlobs;

            var repo = GitRepository.Open(repoPath);
            var indexer = new FullScanIndexer(repo, index, effectiveExcludes);
            await indexer.RunAsync(progress: null, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            await index.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return index;
    }

    // ----- tests -----

    [Fact]
    public async Task DefaultExcludes_PackageLockJson_NotIndexed()
    {
        var repo = MakeRepo(
            ("src/app.cs", "class A {}"),
            ("package-lock.json", "{ \"lockfileVersion\": 3, \"requires\": true }"));

        await using var index = await RunFullScanAsync(repo, useDefaultExcludes: true);

        // Normal source file is indexed.
        var srcId = index.LookupFileIdByPath("src/app.cs");
        Assert.NotNull(srcId);

        // lockfile is skipped.
        var lockId = index.LookupFileIdByPath("package-lock.json");
        Assert.Null(lockId);
    }

    [Fact]
    public async Task DefaultExcludes_MinifiedJs_NotIndexed()
    {
        var repo = MakeRepo(
            ("src/main.js", "function greet() { return 'hello'; }"),
            ("dist/app.min.js", "function greet(){return'hello';}"));

        await using var index = await RunFullScanAsync(repo, useDefaultExcludes: true);

        Assert.NotNull(index.LookupFileIdByPath("src/main.js"));
        Assert.Null(index.LookupFileIdByPath("dist/app.min.js"));
    }

    [Fact]
    public async Task DefaultExcludes_OptOut_PackageLockJsonIndexed()
    {
        var repo = MakeRepo(
            ("src/app.cs", "class A {}"),
            ("package-lock.json", "{ \"lockfileVersion\": 3, \"requires\": true }"));

        await using var index = await RunFullScanAsync(repo, useDefaultExcludes: false);

        // With opt-out, the lockfile should be indexed.
        var lockId = index.LookupFileIdByPath("package-lock.json");
        Assert.NotNull(lockId);
    }

    [Fact]
    public async Task DefaultExcludes_ComposesWithUserGlobs_BothRespected()
    {
        var repo = MakeRepo(
            ("src/main.cs", "class Main {}"),
            ("package-lock.json", "{}"),
            ("vendor/lib.cs", "class Lib {}"));

        // User glob skips vendor/; defaults skip package-lock.json.
        await using var index = await RunFullScanAsync(
            repo,
            useDefaultExcludes: true,
            extraExcludes: new[] { "vendor/**" });

        Assert.NotNull(index.LookupFileIdByPath("src/main.cs"));
        Assert.Null(index.LookupFileIdByPath("package-lock.json"));   // default excluded
        Assert.Null(index.LookupFileIdByPath("vendor/lib.cs"));        // user excluded
    }

    [Fact]
    public async Task DefaultExcludes_GeneratedCSharp_NotIndexed()
    {
        var repo = MakeRepo(
            ("src/Program.cs", "Console.WriteLine();"),
            ("src/Generated/Proto.g.cs", "// <auto-generated />"));

        await using var index = await RunFullScanAsync(repo, useDefaultExcludes: true);

        Assert.NotNull(index.LookupFileIdByPath("src/Program.cs"));
        Assert.Null(index.LookupFileIdByPath("src/Generated/Proto.g.cs"));
    }
}
