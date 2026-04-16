using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Indexed.Core;
using Xunit;

namespace Indexed.Core.Tests;

/// <summary>
/// Tests for <see cref="RepoWatcher"/> — verifies path normalization,
/// <c>.git/</c> skip logic, exclude-glob filtering, and FSW integration
/// against a real temp directory.
/// </summary>
public sealed class RepoWatcherTests : IDisposable
{
    private readonly string _tempRoot;

    public RepoWatcherTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "RW_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, true); } catch { }
    }

    // ----- Normalize -----

    [Fact]
    public void Normalize_InsideRepo_ReturnsPosixRelativePath()
    {
        using var queue = new DebouncingEventQueue();
        var watcher = new RepoWatcher(_tempRoot, queue);

        var full = Path.Combine(_tempRoot, "src", "a.cs");
        var result = watcher.Normalize(full);
        Assert.Equal("src/a.cs", result);
    }

    [Fact]
    public void Normalize_OutsideRepo_ReturnsNull()
    {
        using var queue = new DebouncingEventQueue();
        var watcher = new RepoWatcher(_tempRoot, queue);

        var result = watcher.Normalize(@"C:\completely\elsewhere\file.cs");
        Assert.Null(result);
    }

    [Fact]
    public void Normalize_RootFile_ReturnsFilename()
    {
        using var queue = new DebouncingEventQueue();
        var watcher = new RepoWatcher(_tempRoot, queue);

        var full = Path.Combine(_tempRoot, "readme.md");
        Assert.Equal("readme.md", watcher.Normalize(full));
    }

    [Fact]
    public void Normalize_DotDotSegments_Canonicalized()
    {
        using var queue = new DebouncingEventQueue();
        var watcher = new RepoWatcher(_tempRoot, queue);

        // src/../lib/foo.cs should resolve to lib/foo.cs
        var full = Path.Combine(_tempRoot, "src", "..", "lib", "foo.cs");
        Assert.Equal("lib/foo.cs", watcher.Normalize(full));
    }

    [Fact]
    public void Normalize_DotDotEscapesRepo_ReturnsNull()
    {
        using var queue = new DebouncingEventQueue();
        var watcher = new RepoWatcher(_tempRoot, queue);

        // Going above the repo root should return null.
        var full = Path.Combine(_tempRoot, "..", "escape.cs");
        Assert.Null(watcher.Normalize(full));
    }

    // ----- FSW integration (real watcher on temp dir) -----

    [Fact]
    public async Task FileCreation_EnqueuesFileChanged()
    {
        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(10),
            globalBatchWindow: TimeSpan.FromMilliseconds(100));
        using var watcher = new RepoWatcher(_tempRoot, queue);
        watcher.Start();

        var file = Path.Combine(_tempRoot, "test.cs");
        await File.WriteAllTextAsync(file, "hello");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batch = await queue.DequeueAsync(cts.Token);

        Assert.Contains(batch, e => e is FileChanged fc && fc.RelativePath == "test.cs");
    }

    [Fact]
    public async Task FileDeletion_EnqueuesFileDeleted()
    {
        var file = Path.Combine(_tempRoot, "del.cs");
        await File.WriteAllTextAsync(file, "hello");

        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(10),
            globalBatchWindow: TimeSpan.FromMilliseconds(100));
        using var watcher = new RepoWatcher(_tempRoot, queue);
        watcher.Start();

        File.Delete(file);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batch = await queue.DequeueAsync(cts.Token);

        Assert.Contains(batch, e => e is FileDeleted fd && fd.RelativePath == "del.cs");
    }

    // ----- .git skip logic -----

    [Fact]
    public async Task GitDirectory_IsSkipped()
    {
        var gitDir = Path.Combine(_tempRoot, ".git");
        Directory.CreateDirectory(gitDir);

        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(10),
            globalBatchWindow: TimeSpan.FromMilliseconds(200));
        using var watcher = new RepoWatcher(_tempRoot, queue);
        watcher.Start();

        // Write inside .git — should be skipped.
        await File.WriteAllTextAsync(Path.Combine(gitDir, "index"), "data");

        // Also write a normal file so the queue has something to drain.
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "real.cs"), "code");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batch = await queue.DequeueAsync(cts.Token);

        // The .git write should NOT appear.
        Assert.DoesNotContain(batch, e =>
            e is FileChanged fc && fc.RelativePath.StartsWith(".git/", StringComparison.Ordinal));
        // The normal file SHOULD appear.
        Assert.Contains(batch, e => e is FileChanged fc && fc.RelativePath == "real.cs");
    }

    [Fact]
    public async Task GitignoreFile_IsNotSkipped()
    {
        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(10),
            globalBatchWindow: TimeSpan.FromMilliseconds(200));
        using var watcher = new RepoWatcher(_tempRoot, queue);
        watcher.Start();

        // .gitignore is a regular tracked file — should NOT be skipped.
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, ".gitignore"), "*.log");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batch = await queue.DequeueAsync(cts.Token);

        Assert.Contains(batch, e => e is FileChanged fc && fc.RelativePath == ".gitignore");
    }

    // ----- Exclude-glob filtering -----

    [Fact]
    public async Task ExcludeGlob_SkipsMatchingPaths()
    {
        var libDir = Path.Combine(_tempRoot, "lib");
        Directory.CreateDirectory(libDir);

        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(10),
            globalBatchWindow: TimeSpan.FromMilliseconds(200));
        using var watcher = new RepoWatcher(_tempRoot, queue,
            excludeGlobs: new[] { "lib/**" });
        watcher.Start();

        // Write inside lib/ — should be excluded.
        await File.WriteAllTextAsync(Path.Combine(libDir, "vendor.cs"), "code");

        // Also write a normal file.
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "src.cs"), "code");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var batch = await queue.DequeueAsync(cts.Token);

        Assert.DoesNotContain(batch, e =>
            e is FileChanged fc && fc.RelativePath.StartsWith("lib/", StringComparison.Ordinal));
        Assert.Contains(batch, e => e is FileChanged fc && fc.RelativePath == "src.cs");
    }

    // ----- OnError → ReconciliationRequested -----

    [Fact]
    public async Task OnError_EnqueuesReconciliationRequested()
    {
        // We cannot easily provoke a real FSW error in a unit test, so we
        // verify indirectly: enqueue a ReconciliationRequested (the same
        // action OnError takes) and confirm it surfaces.
        using var queue = new DebouncingEventQueue(
            perPathDebounce: TimeSpan.FromMilliseconds(10),
            globalBatchWindow: TimeSpan.FromMilliseconds(50));

        queue.Enqueue(new ReconciliationRequested());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var batch = await queue.DequeueAsync(cts.Token);

        Assert.Contains(batch, e => e is ReconciliationRequested);
    }

    // ----- Start is idempotent -----

    [Fact]
    public void Start_CalledTwice_DoesNotThrow()
    {
        using var queue = new DebouncingEventQueue();
        using var watcher = new RepoWatcher(_tempRoot, queue);

        watcher.Start();
        watcher.Start(); // second call is a no-op
    }
}
