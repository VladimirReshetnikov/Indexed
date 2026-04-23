using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Indexed.Targets;
using Xunit;

namespace Indexed.Core.Tests;

public sealed class DirectoryTargetsTests : IDisposable
{
    private readonly string _tempRoot;

    public DirectoryTargetsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "IndexedDirTargets_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, true); } catch { }
    }

    [Fact]
    public async Task DirectoryTree_EnumeratesFilesWithoutGit()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "src"));
        File.WriteAllText(Path.Combine(_tempRoot, "src", "a.cs"), "class A {}");
        File.WriteAllText(Path.Combine(_tempRoot, "readme.md"), "# Hello");

        var target = DirectoryTreeIndexTarget.Open(
            _tempRoot,
            useDefaultIndexExcludes: false,
            useDefaultDirectoryExcludes: false);

        var files = new List<EnumeratedFile>();
        await foreach (var file in target.EnumerateFilesAsync())
            files.Add(file);

        Assert.Contains(files, f => f.LogicalPath.Value == "src/a.cs");
        Assert.Contains(files, f => f.LogicalPath.Value == "readme.md");
    }

    [Fact]
    public async Task DirectorySet_EnumeratesLabelPrefixedLogicalPaths()
    {
        var sdkRoot = Path.Combine(_tempRoot, "sdk");
        var docsRoot = Path.Combine(_tempRoot, "docs");
        Directory.CreateDirectory(Path.Combine(sdkRoot, "src"));
        Directory.CreateDirectory(docsRoot);
        File.WriteAllText(Path.Combine(sdkRoot, "src", "api.cs"), "class Api {}");
        File.WriteAllText(Path.Combine(docsRoot, "guide.md"), "# Guide");

        var target = DirectorySetIndexTarget.Open(
            new[]
            {
                new TargetRootSpec("sdk", sdkRoot),
                new TargetRootSpec("docs", docsRoot),
            },
            useDefaultIndexExcludes: false,
            useDefaultDirectoryExcludes: false);

        var logicalPaths = new List<string>();
        await foreach (var file in target.EnumerateFilesAsync())
            logicalPaths.Add(file.LogicalPath.Value);

        Assert.Contains("sdk/src/api.cs", logicalPaths);
        Assert.Contains("docs/guide.md", logicalPaths);
    }

    [Fact]
    public void DirectorySet_ResolveAndMap_RoundTrips()
    {
        var sdkRoot = Path.Combine(_tempRoot, "sdk");
        var docsRoot = Path.Combine(_tempRoot, "docs");
        Directory.CreateDirectory(Path.Combine(sdkRoot, "src"));
        Directory.CreateDirectory(docsRoot);

        var target = DirectorySetIndexTarget.Open(
            new[]
            {
                new TargetRootSpec("sdk", sdkRoot),
                new TargetRootSpec("docs", docsRoot),
            },
            useDefaultIndexExcludes: false,
            useDefaultDirectoryExcludes: false);

        var logicalPath = new LogicalPath("sdk/src/api.cs");
        var absolutePath = target.ResolveAbsolutePath(logicalPath);

        Assert.True(target.TryMapAbsolutePath(absolutePath, out var mapped));
        Assert.Equal(logicalPath, mapped);
        Assert.True(target.TryResolveLogicalPath(logicalPath, out var file));
        Assert.Equal("src/api.cs", file.RelativePath);
        Assert.Equal(absolutePath, file.AbsolutePath);
    }

    [Fact]
    public void DirectorySet_Open_RejectsOverlappingRoots()
    {
        var parent = Path.Combine(_tempRoot, "workspace");
        var child = Path.Combine(parent, "nested");
        Directory.CreateDirectory(child);

        Assert.Throws<ArgumentException>(() =>
            DirectorySetIndexTarget.Open(
                new[]
                {
                    new TargetRootSpec("parent", parent),
                    new TargetRootSpec("child", child),
                },
                useDefaultIndexExcludes: false,
                useDefaultDirectoryExcludes: false));
    }
}
