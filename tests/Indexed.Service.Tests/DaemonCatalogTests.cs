using System;
using System.IO;
using Indexed.Service;
using Indexed.Targets;
using Xunit;

namespace Indexed.Service.Tests;

public sealed class DaemonCatalogTests : IDisposable
{
    private readonly string _tempRoot;

    public DaemonCatalogTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "Indexed.Service.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void List_ReturnsParsedDaemons_AndSkipsGarbage()
    {
        var first = new DaemonInfo(
            Port: 41001,
            Pid: 1111,
            TargetKind: TargetKind.DirectoryTree,
            TargetId: "bbbbbbbbbbbb",
            Roots: new[] { new TargetRoot(null, @"C:\tree", true) },
            PrimaryRoot: new TargetRoot(null, @"C:\tree", true),
            RepoRoot: null,
            RepoId: null,
            StartedAt: DateTimeOffset.Parse("2026-04-23T19:00:00Z"),
            DaemonVersion: "1.0.0",
            ShutdownToken: "token-1");
        var second = new DaemonInfo(
            Port: 41002,
            Pid: 2222,
            TargetKind: TargetKind.GitRepository,
            TargetId: "aaaaaaaaaaaa",
            Roots: new[] { new TargetRoot(null, @"C:\repo", true) },
            PrimaryRoot: new TargetRoot(null, @"C:\repo", true),
            RepoRoot: @"C:\repo",
            RepoId: "aaaaaaaaaaaa",
            StartedAt: DateTimeOffset.Parse("2026-04-23T19:10:00Z"),
            DaemonVersion: "1.0.0",
            ShutdownToken: "token-2");

        WriteDaemon(first);
        WriteDaemon(second);

        var brokenDir = Path.Combine(_tempRoot, "broken");
        Directory.CreateDirectory(brokenDir);
        File.WriteAllText(Path.Combine(brokenDir, "daemon.json"), "{ not json");

        var daemons = DaemonCatalog.List(_tempRoot);

        Assert.Collection(
            daemons,
            daemon => Assert.Equal("aaaaaaaaaaaa", daemon.TargetId),
            daemon => Assert.Equal("bbbbbbbbbbbb", daemon.TargetId));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }

    private void WriteDaemon(DaemonInfo info)
    {
        var dir = Path.Combine(_tempRoot, info.TargetId);
        Directory.CreateDirectory(dir);
        info.WriteAtomic(Path.Combine(dir, "daemon.json"));
    }
}
