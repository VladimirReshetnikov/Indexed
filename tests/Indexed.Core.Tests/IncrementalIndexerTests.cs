using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Indexed.Core;
using Indexed.Git;
using Xunit;

namespace Indexed.Core.Tests;

/// <summary>
/// Integration tests for <see cref="IncrementalIndexer"/>. Spins up a real
/// tiny git repo, indexes via full scan, then exercises incremental
/// single-file, branch switch, and reconciliation scenarios.
/// </summary>
public sealed class IncrementalIndexerTests : IDisposable
{
    private readonly string _tempRoot;

    public IncrementalIndexerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "IndexedIncr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, true); } catch { }
    }

    private string InitRepo((string path, string content)[] files)
    {
        var repo = Path.Combine(_tempRoot, "repo_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static string GetHead(string repo)
        => GitRepository.Open(repo).GetHeadSha();

    [Fact]
    public async Task FileChanged_UpsertsNewFile()
    {
        var repoPath = InitRepo(new[] { ("a.cs", "alpha") });
        var gitRepo = GitRepository.Open(repoPath);
        var dbPath = Path.Combine(_tempRoot, "fc.db");
        await using var index = SqliteIndex.OpenOrCreate(dbPath);

        // Full scan to seed the index.
        await new FullScanIndexer(gitRepo, index).RunAsync();
        Assert.Equal(1L, index.GetFileCount());

        // Write a new file on disk (but don't git-add).
        var newFile = Path.Combine(repoPath, "b.cs");
        await File.WriteAllTextAsync(newFile, "beta content");

        // Enqueue a FileChanged event and run the indexer.
        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(5),
            globalBatchWindow: TimeSpan.FromMilliseconds(30));

        await using var indexer = new IncrementalIndexer(gitRepo, index, queue);
        indexer.Start();

        queue.Enqueue(new FileChanged("b.cs"));
        await Task.Delay(200); // let the worker process the batch

        await indexer.DisposeAsync();

        Assert.Equal(2L, index.GetFileCount());
        var candidates = await index.QueryCodeCandidatesAsync("\"bet\"", default);
        Assert.NotEmpty(candidates);
    }

    [Fact]
    public async Task FileDeleted_RemovesFromIndex()
    {
        var repoPath = InitRepo(new[] { ("a.cs", "alpha"), ("b.cs", "beta") });
        var gitRepo = GitRepository.Open(repoPath);
        var dbPath = Path.Combine(_tempRoot, "fd.db");
        await using var index = SqliteIndex.OpenOrCreate(dbPath);

        await new FullScanIndexer(gitRepo, index).RunAsync();
        Assert.Equal(2L, index.GetFileCount());

        // Delete b.cs from disk.
        File.Delete(Path.Combine(repoPath, "b.cs"));

        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(5),
            globalBatchWindow: TimeSpan.FromMilliseconds(30));

        await using var indexer = new IncrementalIndexer(gitRepo, index, queue);
        indexer.Start();

        queue.Enqueue(new FileDeleted("b.cs"));
        await Task.Delay(200);

        await indexer.DisposeAsync();

        Assert.Equal(1L, index.GetFileCount());
    }

    [Fact]
    public async Task HeadMoved_AppliesDiffTree()
    {
        var repoPath = InitRepo(new[]
        {
            ("a.cs", "alpha"),
            ("b.cs", "beta"),
            ("c.cs", "gamma"),
        });
        var gitRepo = GitRepository.Open(repoPath);
        var dbPath = Path.Combine(_tempRoot, "hm.db");
        await using var index = SqliteIndex.OpenOrCreate(dbPath);

        await new FullScanIndexer(gitRepo, index).RunAsync();
        var sha1 = GetHead(repoPath);
        Assert.Equal(3L, index.GetFileCount());

        // Make a second commit: modify a.cs, delete b.cs, add d.cs.
        File.WriteAllText(Path.Combine(repoPath, "a.cs"), "alpha_v2");
        File.Delete(Path.Combine(repoPath, "b.cs"));
        File.WriteAllText(Path.Combine(repoPath, "d.cs"), "delta");
        RunGit(repoPath, "add", "-A");
        RunGit(repoPath, "commit", "-q", "-m", "c2");
        var sha2 = GetHead(repoPath);

        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(5),
            globalBatchWindow: TimeSpan.FromMilliseconds(30));

        await using var indexer = new IncrementalIndexer(gitRepo, index, queue);
        indexer.Start();

        queue.Enqueue(new HeadMoved(sha1, sha2));

        // HeadMoved processing shells out to git diff-tree, then reads and
        // upserts multiple files + writes indexed_head meta — allow time.
        await Task.Delay(2000);

        await indexer.DisposeAsync();

        // b.cs deleted, d.cs added → net change: still 3 files.
        Assert.Equal(3L, index.GetFileCount());

        // a.cs should have updated content.
        var aCandidates = await index.QueryCodeCandidatesAsync("\"alpha_v2\"", default);
        Assert.NotEmpty(aCandidates);

        // b.cs should be gone.
        var bId = index.LookupFileIdByPath("b.cs");
        Assert.Null(bId);

        // d.cs should be present.
        var dId = index.LookupFileIdByPath("d.cs");
        Assert.NotNull(dId);

        // indexed_head should be updated.
        Assert.Equal(sha2, index.GetMeta(SqliteSchema.MetaKey_IndexedHead));
    }

    [Fact]
    public async Task ReconciliationRequested_FixesMissingFiles()
    {
        var repoPath = InitRepo(new[] { ("a.cs", "alpha") });
        var gitRepo = GitRepository.Open(repoPath);
        var dbPath = Path.Combine(_tempRoot, "recon.db");
        await using var index = SqliteIndex.OpenOrCreate(dbPath);

        await new FullScanIndexer(gitRepo, index).RunAsync();
        Assert.Equal(1L, index.GetFileCount());

        // Write a new untracked file that the index doesn't know about.
        File.WriteAllText(Path.Combine(repoPath, "orphan.cs"), "orphan content");

        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(5),
            globalBatchWindow: TimeSpan.FromMilliseconds(30));

        await using var indexer = new IncrementalIndexer(gitRepo, index, queue);
        indexer.Start();

        queue.Enqueue(new ReconciliationRequested());

        // Reconciliation shells out to git ls-files + enumerates the index,
        // which can take longer than a simple file upsert.
        await Task.Delay(2000);

        await indexer.DisposeAsync();

        // orphan.cs should now be in the index.
        Assert.Equal(2L, index.GetFileCount());
        var orphanId = index.LookupFileIdByPath("orphan.cs");
        Assert.NotNull(orphanId);
    }

    [Fact]
    public async Task BatchCommitted_EventFires()
    {
        var repoPath = InitRepo(new[] { ("a.cs", "alpha") });
        var gitRepo = GitRepository.Open(repoPath);
        var dbPath = Path.Combine(_tempRoot, "evt.db");
        await using var index = SqliteIndex.OpenOrCreate(dbPath);
        await new FullScanIndexer(gitRepo, index).RunAsync();

        File.WriteAllText(Path.Combine(repoPath, "b.cs"), "beta");

        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(5),
            globalBatchWindow: TimeSpan.FromMilliseconds(30));

        var committed = false;
        await using var indexer = new IncrementalIndexer(gitRepo, index, queue);
        indexer.BatchCommitted += () => committed = true;
        indexer.Start();

        queue.Enqueue(new FileChanged("b.cs"));
        await Task.Delay(200);

        await indexer.DisposeAsync();

        Assert.True(committed);
    }
}
