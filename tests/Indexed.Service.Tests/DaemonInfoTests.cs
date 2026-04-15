using System;
using System.IO;
using System.Threading.Tasks;
using Indexed.Service;
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
        var info = new DaemonInfo(
            Port: 5123,
            Pid: 42,
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
        Assert.Equal(info, round);
    }

    [Fact]
    public void WriteAtomic_OverwritesExistingFile()
    {
        // Atomic rename must replace a pre-existing daemon.json — mirror the
        // real-world case where an unclean shutdown left a stale port-file.
        var path = Path.Combine(_tempDir, "daemon.json");
        File.WriteAllText(path, "old contents");

        var info = new DaemonInfo(
            Port: 1, Pid: 1, RepoRoot: ".", RepoId: "a",
            StartedAt: DateTimeOffset.UtcNow, DaemonVersion: "v", ShutdownToken: "t");
        info.WriteAtomic(path);

        var round = DaemonInfo.TryRead(path);
        Assert.Equal(1, round?.Port);
    }

    [Fact]
    public void WriteAtomic_DoesNotLeaveTempFiles()
    {
        var path = Path.Combine(_tempDir, "daemon.json");
        var info = new DaemonInfo(1, 1, ".", "a", DateTimeOffset.UtcNow, "v", "t");
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
