using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Indexed.Service;
using Indexed.Targets;
using Xunit;

namespace Indexed.Service.Tests;

public sealed class DaemonInfoTests : IDisposable
{
    private readonly string _tempDir;

    public DaemonInfoTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "IndexedDaemonInfo_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void WriteAtomic_CreatesFileAndRoundTrips()
    {
        var root = new TargetRoot(null, @"C:\repos\foo", true);
        var info = new DaemonInfo(
            Port: 5123,
            Pid: 42,
            TargetKind: TargetKind.GitRepository,
            TargetId: "abc123def456",
            Roots: new[] { root },
            PrimaryRoot: root,
            RepoRoot: @"C:\repos\foo",
            RepoId: "abc123def456",
            StartedAt: new DateTimeOffset(2026, 4, 15, 0, 0, 0, TimeSpan.Zero),
            DaemonVersion: "0.1.0-s1",
            ShutdownToken: "token==");

        var path = Path.Combine(_tempDir, "daemon.json");
        info.WriteAtomic(path);

        Assert.True(File.Exists(path));
        var round = DaemonInfo.TryRead(path);
        Assert.NotNull(round);
        Assert.Equal(info.Port, round!.Port);
        Assert.Equal(info.Pid, round.Pid);
        Assert.Equal(info.TargetKind, round.TargetKind);
        Assert.Equal(info.TargetId, round.TargetId);
        Assert.Equal(info.PrimaryRoot, round.PrimaryRoot);
        Assert.Equal(info.RepoRoot, round.RepoRoot);
        Assert.Equal(info.RepoId, round.RepoId);
        Assert.Equal(info.StartedAt, round.StartedAt);
        Assert.Equal(info.DaemonVersion, round.DaemonVersion);
        Assert.Equal(info.ShutdownToken, round.ShutdownToken);
        Assert.True(info.Roots.SequenceEqual(round.Roots));
    }

    [Fact]
    public void WriteAtomic_OverwritesExistingFile()
    {
        // Atomic rename must replace a pre-existing daemon.json — mirror the
        // real-world case where an unclean shutdown left a stale port-file.
        var path = Path.Combine(_tempDir, "daemon.json");
        File.WriteAllText(path, "old contents");

        var root = new TargetRoot(null, ".", true);
        var info = new DaemonInfo(
            Port: 1, Pid: 1,
            TargetKind: TargetKind.GitRepository,
            TargetId: "a",
            Roots: new[] { root },
            PrimaryRoot: root,
            RepoRoot: ".",
            RepoId: "a",
            StartedAt: DateTimeOffset.UtcNow, DaemonVersion: "v", ShutdownToken: "t");
        info.WriteAtomic(path);

        var round = DaemonInfo.TryRead(path);
        Assert.Equal(1, round?.Port);
    }

    [Fact]
    public void WriteAtomic_DoesNotLeaveTempFiles()
    {
        var path = Path.Combine(_tempDir, "daemon.json");
        var root = new TargetRoot(null, ".", true);
        var info = new DaemonInfo(
            1,
            1,
            TargetKind.GitRepository,
            "a",
            new[] { root },
            root,
            ".",
            "a",
            DateTimeOffset.UtcNow,
            "v",
            "t");
        info.WriteAtomic(path);

        var siblings = Directory.EnumerateFiles(_tempDir);
        foreach (var s in siblings)
            Assert.False(Path.GetFileName(s).Contains(".tmp-"), $"temp file leaked: {s}");
    }

    [Fact]
    public void TryRead_ReturnsNullForMissing()
    {
        Assert.Null(DaemonInfo.TryRead(Path.Combine(_tempDir, "nope.json")));
    }

    [Fact]
    public void TryRead_ReturnsNullForMalformed()
    {
        var path = Path.Combine(_tempDir, "bad.json");
        File.WriteAllText(path, "{not json");
        Assert.Null(DaemonInfo.TryRead(path));
    }

    [Fact]
    public void TryDelete_SucceedsForMissing()
    {
        DaemonInfo.TryDelete(Path.Combine(_tempDir, "not-present"));
        // no exception = pass
    }

    [Fact]
    public void NewShutdownToken_ReturnsBase64()
    {
        var token = DaemonInfo.NewShutdownToken();
        // 32 bytes → base64 "==" pad; 44 chars.
        Assert.Equal(44, token.Length);
        Assert.NotEqual(token, DaemonInfo.NewShutdownToken());
    }
}
