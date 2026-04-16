using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Core;
using Indexed.Git;
using Xunit;

namespace Indexed.Core.Tests;

/// <summary>
/// Integration tests for <see cref="HeadPoller"/>. Each test spins up a real
/// git repo in a temp directory with a SQLite index so the poller's mtime
/// canary and <c>git rev-parse HEAD</c> checks run against actual git state.
/// </summary>
public sealed class HeadPollerTests : IDisposable
{
    private readonly string _tempRoot;

    public HeadPollerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "HP_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, true); } catch { }
    }

    private (GitRepository Repo, SqliteIndex Index) SetupRepoAndIndex()
    {
        var repoPath = Path.Combine(_tempRoot, "repo");
        Directory.CreateDirectory(repoPath);
        RunGit(repoPath, "init", "-q", "-b", "main");
        RunGit(repoPath, "config", "user.name", "t");
        RunGit(repoPath, "config", "user.email", "t@t");
        RunGit(repoPath, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(repoPath, "a.txt"), "hello");
        RunGit(repoPath, "add", "a.txt");
        RunGit(repoPath, "commit", "-q", "-m", "first");

        var repo = GitRepository.Open(repoPath);
        var dbPath = Path.Combine(_tempRoot, "index.db");
        var index = SqliteIndex.OpenOrCreate(dbPath);

        return (repo, index);
    }

    private static void RunGit(string repo, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed: {p.StandardError.ReadToEnd()}");
    }

    [Fact]
    public async Task HeadMoved_DetectedAndEnqueued()
    {
        var (repo, index) = SetupRepoAndIndex();
        try
        {
            using var queue = new DebouncingEventQueue(
                perPathDebounce: TimeSpan.FromMilliseconds(10),
                globalBatchWindow: TimeSpan.FromMilliseconds(100));

            // Record the current HEAD as the indexed head so the poller
            // has a baseline to detect movement from.
            var initialHead = repo.GetHeadSha();
            index.SetMeta(SqliteSchema.MetaKey_IndexedHead, initialHead);

            using var poller = new HeadPoller(repo, index, queue,
                interval: TimeSpan.FromMilliseconds(50));
            poller.Start();

            Assert.Equal(initialHead, poller.LastKnownHead);

            // Create a new commit to move HEAD.
            var repoPath = repo.RepoRoot;
            File.WriteAllText(Path.Combine(repoPath, "b.txt"), "world");
            RunGit(repoPath, "add", "b.txt");
            RunGit(repoPath, "commit", "-q", "-m", "second");

            var newHead = repo.GetHeadSha();
            Assert.NotEqual(initialHead, newHead);

            // Wait for the poller to detect the change.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var batch = await queue.DequeueAsync(cts.Token);

            Assert.Contains(batch, e => e is HeadMoved hm && hm.NewHead == newHead);
            Assert.Equal(newHead, poller.LastKnownHead);
        }
        finally
        {
            await index.DisposeAsync();
        }
    }

    [Fact]
    public async Task NoChange_NoEventEnqueued()
    {
        var (repo, index) = SetupRepoAndIndex();
        try
        {
            using var queue = new DebouncingEventQueue(
                perPathDebounce: TimeSpan.FromMilliseconds(10),
                globalBatchWindow: TimeSpan.FromMilliseconds(100));

            var head = repo.GetHeadSha();
            index.SetMeta(SqliteSchema.MetaKey_IndexedHead, head);

            using var poller = new HeadPoller(repo, index, queue,
                interval: TimeSpan.FromMilliseconds(50));
            poller.Start();

            // Wait a few poll cycles -- no commit, so no event expected.
            await Task.Delay(300);

            // Complete the queue and drain -- should be empty.
            queue.Complete();
            var batch = await queue.DequeueAsync();
            Assert.Empty(batch);
        }
        finally
        {
            await index.DisposeAsync();
        }
    }

    [Fact]
    public async Task LastKnownHead_ReflectsInitialSeed()
    {
        var (repo, index) = SetupRepoAndIndex();
        try
        {
            using var queue = new DebouncingEventQueue();
            var head = repo.GetHeadSha();
            index.SetMeta(SqliteSchema.MetaKey_IndexedHead, head);

            using var poller = new HeadPoller(repo, index, queue,
                interval: TimeSpan.FromSeconds(60)); // long interval -- won't tick
            poller.Start();

            Assert.Equal(head, poller.LastKnownHead);
        }
        finally
        {
            await index.DisposeAsync();
        }
    }
}
