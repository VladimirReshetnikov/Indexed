using System;
using System.IO;
using Indexed.Service;
using Xunit;

namespace Indexed.Service.Tests;

public sealed class DaemonCommandLineTests : IDisposable
{
    private readonly string _originalCurrentDirectory;
    private readonly string _tempRoot;

    public DaemonCommandLineTests()
    {
        _originalCurrentDirectory = Directory.GetCurrentDirectory();
        _tempRoot = Path.Combine(Path.GetTempPath(), "Indexed.Service.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        Directory.SetCurrentDirectory(_tempRoot);
    }

    [Fact]
    public void NoArgs_DefaultsToCurrentDirectoryGitMode()
    {
        var options = DaemonCommandLine.ParseOptions(Array.Empty<string>());

        Assert.Equal(_tempRoot, options.RepoRoot);
        Assert.Null(options.TargetSelection);
        Assert.True(options.UseDefaultIndexExcludes);
        Assert.False(options.UseDefaultDirectoryExcludes);
    }

    [Fact]
    public void RepoRootFlag_ParsesGitMode()
    {
        var options = DaemonCommandLine.ParseOptions(new[] { "--repo-root", @"C:\repo" });

        Assert.Equal(@"C:\repo", options.RepoRoot);
        Assert.Null(options.TargetSelection);
    }

    [Fact]
    public void SingleRoot_ParsesDirectoryTreeMode()
    {
        var options = DaemonCommandLine.ParseOptions(new[]
        {
            "--root", @"C:\tree",
            "--no-default-directory-excludes",
        });

        Assert.Null(options.RepoRoot);
        Assert.NotNull(options.TargetSelection);
        var root = Assert.Single(options.TargetSelection!.Roots!);
        Assert.Null(root.Name);
        Assert.Equal(Path.GetFullPath(@"C:\tree"), root.Path);
        Assert.False(options.TargetSelection.UseDefaultDirectoryExcludes);
    }

    [Fact]
    public void SingleRoot_PathContainingEquals_ParsesAsBarePath()
    {
        var options = DaemonCommandLine.ParseOptions(new[]
        {
            "--root", @"C:\tree=with-equals",
        });

        var root = Assert.Single(options.TargetSelection!.Roots!);
        Assert.Null(root.Name);
        Assert.Equal(Path.GetFullPath(@"C:\tree=with-equals"), root.Path);
    }

    [Fact]
    public void MultiRoot_ParsesDirectorySetMode()
    {
        var options = DaemonCommandLine.ParseOptions(new[]
        {
            "--root", @"sdk=C:\src\sdk",
            "--root", @"docs=C:\src\docs",
        });

        Assert.NotNull(options.TargetSelection);
        Assert.Collection(
            options.TargetSelection!.Roots!,
            root =>
            {
                Assert.Equal("docs", root.Name);
                Assert.Equal(Path.GetFullPath(@"C:\src\docs"), root.Path);
            },
            root =>
            {
                Assert.Equal("sdk", root.Name);
                Assert.Equal(Path.GetFullPath(@"C:\src\sdk"), root.Path);
            });
    }

    [Fact]
    public void RepoRoot_And_Root_AreRejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => DaemonCommandLine.ParseOptions(new[]
        {
            "--repo-root", @"C:\repo",
            "--root", @"C:\tree",
        }));

        Assert.Contains("mutually exclusive", ex.Message);
    }

    [Fact]
    public void NoDefaultDirectoryExcludes_WithoutRoot_IsRejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => DaemonCommandLine.ParseOptions(new[]
        {
            "--repo-root", @"C:\repo",
            "--no-default-directory-excludes",
        }));

        Assert.Contains("--root", ex.Message);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalCurrentDirectory);
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }
}
