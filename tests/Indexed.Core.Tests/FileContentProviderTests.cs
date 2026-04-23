using System;
using System.IO;
using System.Threading.Tasks;
using Indexed.Core;
using Xunit;

namespace Indexed.Core.Tests;

/// <summary>
/// Direct unit coverage for <see cref="FileContentProvider"/> that pins the
/// classification contract the query-time snippet rehydrator depends on:
/// missing vs. oversize vs. unreadable vs. out-of-root. The broader
/// <see cref="CodeQueryExecutorDiskReadTests"/> exercises the integration
/// path (executor + repair queue); these tests poke the provider directly
/// so regressions in the classification surface here first.
/// </summary>
public sealed class FileContentProviderTests : IDisposable
{
    private readonly string _tempRoot;

    public FileContentProviderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "FcpTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, true); } catch { }
    }

    [Fact]
    public async Task Ok_ForExistingFile_WithPosixSlashes()
    {
        // POSIX-slash relative paths are what SqliteIndex.files.path stores;
        // the provider must normalize them back to the native separator.
        var sub = Path.Combine(_tempRoot, "src");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "foo.cs"), "public class Alpha { }\n");

        var provider = new FileContentProvider(_tempRoot);
        var outcome = await provider.ReadAsync("src/foo.cs", default);

        Assert.Equal(FileReadStatus.Ok, outcome.Status);
        Assert.True(outcome.IsOk);
        Assert.Equal("public class Alpha { }\n", outcome.Content);
    }

    [Fact]
    public async Task Ok_ForExistingFile_WithBackslashes()
    {
        // Defensive: on Windows, a row stored with \\ separators (legacy or
        // misconfigured index) should still resolve cleanly.
        Directory.CreateDirectory(Path.Combine(_tempRoot, "a"));
        File.WriteAllText(Path.Combine(_tempRoot, "a", "b.cs"), "hi");

        var provider = new FileContentProvider(_tempRoot);
        var outcome = await provider.ReadAsync(@"a\b.cs", default);

        Assert.Equal(FileReadStatus.Ok, outcome.Status);
        Assert.Equal("hi", outcome.Content);
    }

    [Fact]
    public async Task Missing_ForAbsentFile()
    {
        var provider = new FileContentProvider(_tempRoot);
        var outcome = await provider.ReadAsync("nope.cs", default);

        Assert.Equal(FileReadStatus.Missing, outcome.Status);
        Assert.Null(outcome.Content);
        Assert.False(outcome.IsOk);
    }

    [Fact]
    public async Task Missing_ForAbsentDirectory()
    {
        // FileInfo.Exists is false when the parent directory is absent too,
        // so the missing classification still wins without leaking an
        // IOException from File.ReadAllBytesAsync.
        var provider = new FileContentProvider(_tempRoot);
        var outcome = await provider.ReadAsync("does/not/exist/foo.cs", default);

        Assert.Equal(FileReadStatus.Missing, outcome.Status);
    }

    [Fact]
    public async Task Oversize_WhenStatExceedsLimit()
    {
        // Build a 50 MiB + 1 byte file; stat sees it before any read is issued.
        var path = Path.Combine(_tempRoot, "huge.bin");
        using (var fs = File.Create(path))
        {
            fs.SetLength(IndexLimits.MaxIndexableFileBytes + 1);
        }

        var provider = new FileContentProvider(_tempRoot);
        var outcome = await provider.ReadAsync("huge.bin", default);

        Assert.Equal(FileReadStatus.Oversize, outcome.Status);
        Assert.Null(outcome.Content);
    }

    [Fact]
    public async Task Unreadable_ForEmptyRelPath()
    {
        var provider = new FileContentProvider(_tempRoot);
        var outcome = await provider.ReadAsync(string.Empty, default);

        Assert.Equal(FileReadStatus.Unreadable, outcome.Status);
    }

    [Fact]
    public async Task Unreadable_ForInvalidPathCharacters()
    {
        // Path.Combine throws ArgumentException on certain invalid chars;
        // the provider must swallow and classify, not throw.
        if (!OperatingSystem.IsWindows()) return; // invalid-char set differs by OS
        var provider = new FileContentProvider(_tempRoot);
        var outcome = await provider.ReadAsync("bad\0path.cs", default);

        Assert.Equal(FileReadStatus.Unreadable, outcome.Status);
    }

    [Fact]
    public async Task OutOfRoot_WhenRelPathEscapesWithDotDot()
    {
        // Defense-in-depth: a pathological `files.path` row with `..`
        // segments must not read outside the repo root. Create the sibling
        // target so the test fails with a match outcome (rather than
        // Missing) if the prefix check regresses.
        var parent = Path.GetDirectoryName(_tempRoot)!;
        var siblingName = "Fcp_OutOfRoot_" + Guid.NewGuid().ToString("N") + ".txt";
        var sibling = Path.Combine(parent, siblingName);
        try
        {
            File.WriteAllText(sibling, "SHOULD_NOT_BE_READ");

            var provider = new FileContentProvider(_tempRoot);
            var outcome = await provider.ReadAsync($"../{siblingName}", default);

            Assert.Equal(FileReadStatus.OutOfRoot, outcome.Status);
            Assert.Null(outcome.Content);
        }
        finally
        {
            try { File.Delete(sibling); } catch { }
        }
    }

    [Fact]
    public async Task OutOfRoot_WhenRelPathIsAbsolute()
    {
        // Absolute paths in `files.path` would be a bug upstream, but the
        // provider must still refuse rather than read an arbitrary disk
        // location. Path.Combine with an absolute second argument discards
        // the first; GetFullPath then normalizes. The root-prefix check
        // catches it.
        var foreign = Path.Combine(Path.GetTempPath(), "ShouldNotRead_" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(foreign, "NOPE");
        try
        {
            var provider = new FileContentProvider(_tempRoot);
            var outcome = await provider.ReadAsync(foreign, default);

            Assert.Equal(FileReadStatus.OutOfRoot, outcome.Status);
        }
        finally
        {
            try { File.Delete(foreign); } catch { }
        }
    }

    [Fact]
    public void Constructor_CanonicalizesRoot()
    {
        // A root with a trailing separator should resolve to a canonical
        // form that the provider's prefix check can rely on. Path.GetFullPath
        // preserves the trailing separator when one is supplied on input,
        // so compare after stripping trailing separators on both sides.
        var withSep = _tempRoot + Path.DirectorySeparatorChar;
        var provider = new FileContentProvider(withSep);
        Assert.Equal(
            Path.GetFullPath(_tempRoot).TrimEnd(Path.DirectorySeparatorChar),
            provider.PrimaryRootPath.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyRoot()
    {
        Assert.Throws<ArgumentException>(() => new FileContentProvider(string.Empty));
    }
}
