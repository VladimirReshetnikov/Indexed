using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Indexed.Abstractions;
using Indexed.Core;
using Indexed.Git;
using Xunit;

namespace Indexed.Core.Tests;

/// <summary>
/// Full-scan indexer integration tests. Spins up a real tiny git repo in a
/// temp dir, runs the indexer, and asserts the DB shape + re-run skip behavior.
/// </summary>
public sealed class FullScanIndexerTests : IDisposable
{
    private readonly string _tempRoot;

    public FullScanIndexerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "IndexedFullScan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, true); } catch { }
    }

    private string InitRepo((string path, string content)[] files)
    {
        var repo = Path.Combine(_tempRoot, "repo");
        Directory.CreateDirectory(repo);
        RunGit(repo, "init", "-q", "-b", "main");
        RunGit(repo, "config", "user.name", "t");
        RunGit(repo, "config", "user.email", "t@t");
        RunGit(repo, "config", "commit.gpgsign", "false");
        foreach (var (relPath, content) in files)
        {
            var full = Path.Combine(repo, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "-q", "-m", "init");
        return repo;
    }

    private static void RunGit(string repo, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {p.StandardError.ReadToEnd()}");
    }

    [Fact]
    public async Task RunAsync_IndexesAllTextFiles()
    {
        var repo = InitRepo(new[]
        {
            ("a.cs", "alpha beta"),
            ("b.cs", "gamma delta"),
            ("sub/c.cs", "epsilon zeta"),
        });

        var gitRepo = GitRepository.Open(repo);
        var dbPath = Path.Combine(_tempRoot, "index.db");
        await using var index = SqliteIndex.OpenOrCreate(dbPath);

        var stats = await new FullScanIndexer(gitRepo, index).RunAsync();

        Assert.Equal(3, stats.Total);
        Assert.Equal(3, stats.Indexed);
        Assert.Equal(0, stats.Skipped);
        Assert.Equal(3L, index.GetFileCount());

        var candidates = await index.QueryCodeCandidatesAsync("\"eps\"", default);
        var rows = await index.GetFilesAsync(candidates, default);
        Assert.Contains(rows, r => r.Path == "sub/c.cs");
    }

    [Fact]
    public async Task RunAsync_SecondRun_AllUnchanged()
    {
        var repo = InitRepo(new[] { ("a.cs", "hello world") });
        var gitRepo = GitRepository.Open(repo);
        var dbPath = Path.Combine(_tempRoot, "index.db");

        await using var index = SqliteIndex.OpenOrCreate(dbPath);
        var first = await new FullScanIndexer(gitRepo, index).RunAsync();
        Assert.Equal(1, first.Indexed);

        var second = await new FullScanIndexer(gitRepo, index).RunAsync();
        Assert.Equal(1, second.Total);
        Assert.Equal(0, second.Indexed); // SHA matches → unchanged
        Assert.Equal(1, second.Unchanged);
    }

    [Fact]
    public async Task RunAsync_WritesIndexedHeadAndLastScanAt()
    {
        var repo = InitRepo(new[] { ("a.cs", "x") });
        var gitRepo = GitRepository.Open(repo);
        var dbPath = Path.Combine(_tempRoot, "index.db");

        await using var index = SqliteIndex.OpenOrCreate(dbPath);
        await new FullScanIndexer(gitRepo, index).RunAsync();

        var head = index.GetMeta(SqliteSchema.MetaKey_IndexedHead);
        Assert.False(string.IsNullOrEmpty(head));
        Assert.Equal(gitRepo.GetHeadSha(), head);

        var at = index.GetMeta(SqliteSchema.MetaKey_LastFullScanAt);
        Assert.False(string.IsNullOrEmpty(at));
    }
}
