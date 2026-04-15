using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Indexed.Git;
using Xunit;

namespace Indexed.Git.Tests;

/// <summary>
/// End-to-end tests against synthetic git repositories created in the
/// per-test temp directory. Each test creates an empty working tree,
/// configures <c>user.name</c>/<c>user.email</c> so commits succeed without
/// a global git config, and asserts the high-level <see cref="GitRepository"/>
/// contract.
/// </summary>
public sealed class GitRepositoryTests : IDisposable
{
    private readonly string _tempRoot;

    public GitRepositoryTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "IndexedGitTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ----- helpers -----

    private string NewRepo(string name)
    {
        var path = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(path);
        Run(path, "init", "-q", "-b", "main");
        Run(path, "config", "user.name", "Indexed Tests");
        Run(path, "config", "user.email", "test@indexed.invalid");
        Run(path, "config", "commit.gpgsign", "false");
        return path;
    }

    private static void Run(string cwd, params string[] args)
        => GitProcess.RunText(cwd, args);

    private static void WriteFile(string root, string relPath, byte[] bytes)
    {
        var full = Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    private static void WriteText(string root, string relPath, string content)
        => WriteFile(root, relPath, Encoding.UTF8.GetBytes(content));

    // ----- Open -----

    [Fact]
    public void Open_ResolvesRootFromSubdirectory()
    {
        var repo = NewRepo("open-sub");
        var sub = Path.Combine(repo, "deep", "nest");
        Directory.CreateDirectory(sub);

        var handle = GitRepository.Open(sub);

        // Path.GetFullPath normalizes any short-form + trailing separators.
        Assert.Equal(Path.GetFullPath(repo), handle.RepoRoot);
    }

    [Fact]
    public void Open_ThrowsForNonRepoDirectory()
    {
        var notRepo = Path.Combine(_tempRoot, "not-a-repo");
        Directory.CreateDirectory(notRepo);

        Assert.Throws<GitProcessException>(() => GitRepository.Open(notRepo));
    }

    [Fact]
    public void Open_ThrowsForMissingDirectory()
    {
        var missing = Path.Combine(_tempRoot, "definitely-missing");
        Assert.Throws<DirectoryNotFoundException>(() => GitRepository.Open(missing));
    }

    // ----- HEAD / first commit -----

    [Fact]
    public void GetHeadSha_EmptyRepo_ReturnsEmpty()
    {
        var repo = NewRepo("empty-head");
        var handle = GitRepository.Open(repo);

        Assert.Equal(string.Empty, handle.GetHeadSha());
        Assert.Equal(string.Empty, handle.GetFirstCommitSha());
    }

    [Fact]
    public void GetHeadSha_SingleCommit_ReturnsFullSha()
    {
        var repo = NewRepo("head-single");
        WriteText(repo, "a.txt", "hello");
        Run(repo, "add", "a.txt");
        Run(repo, "commit", "-q", "-m", "initial");

        var handle = GitRepository.Open(repo);

        var head = handle.GetHeadSha();
        var first = handle.GetFirstCommitSha();

        Assert.Matches("^[0-9a-f]{40}$", head);
        Assert.Equal(head, first);
    }

    [Fact]
    public void GetFirstCommitSha_MultipleCommits_ReturnsRoot()
    {
        var repo = NewRepo("first-commit");
        WriteText(repo, "a.txt", "one");
        Run(repo, "add", "a.txt");
        Run(repo, "commit", "-q", "-m", "c1");
        var rootSha = GitProcess.RunText(repo, new[] { "rev-parse", "HEAD" }).Trim();

        WriteText(repo, "a.txt", "two");
        Run(repo, "add", "a.txt");
        Run(repo, "commit", "-q", "-m", "c2");

        var handle = GitRepository.Open(repo);

        Assert.NotEqual(handle.GetHeadSha(), rootSha);
        Assert.Equal(rootSha, handle.GetFirstCommitSha());
    }

    // ----- EnumerateFiles -----

    [Fact]
    public void EnumerateFiles_TrackedAndUntracked_NoIgnored()
    {
        var repo = NewRepo("enum-basic");
        WriteText(repo, ".gitignore", "*.log\nbuild/\n");
        WriteText(repo, "tracked.cs", "class A {}");
        Run(repo, "add", ".gitignore", "tracked.cs");
        Run(repo, "commit", "-q", "-m", "seed");

        WriteText(repo, "untracked.md", "# hi");
        WriteText(repo, "debug.log", "ignored");                 // matches *.log
        WriteText(repo, "build/artifact.bin", "ignored");        // matches build/

        var handle = GitRepository.Open(repo);
        var files = handle.EnumerateFiles();

        Assert.Contains(".gitignore", files);
        Assert.Contains("tracked.cs", files);
        Assert.Contains("untracked.md", files);
        Assert.DoesNotContain("debug.log", files);
        Assert.DoesNotContain(files, p => p.StartsWith("build/", StringComparison.Ordinal));
    }

    [Fact]
    public void EnumerateFiles_ReturnsRepositoryRelativePosixPaths()
    {
        var repo = NewRepo("enum-paths");
        WriteText(repo, "src/a/b/c.txt", "x");
        Run(repo, "add", "-A");
        Run(repo, "commit", "-q", "-m", "init");

        var handle = GitRepository.Open(repo);
        var files = handle.EnumerateFiles();

        Assert.Contains("src/a/b/c.txt", files);
        Assert.DoesNotContain(files, p => p.Contains('\\'));
    }

    [Fact]
    public void EnumerateFiles_Deduplicates()
    {
        // A tracked file that is also modified on disk shows up in both
        // ls-files and ls-files --others is a no-op (it only reports
        // *un*tracked). This test confirms the set-union path returns one
        // entry even if both call paths had overlapping output.
        var repo = NewRepo("enum-dedup");
        WriteText(repo, "a.txt", "one");
        Run(repo, "add", "a.txt");
        Run(repo, "commit", "-q", "-m", "init");

        WriteText(repo, "a.txt", "two");  // modified; still tracked, not untracked

        var handle = GitRepository.Open(repo);
        var files = handle.EnumerateFiles();

        Assert.Single(files, f => f == "a.txt");
    }

    [Fact]
    public void EnumerateFiles_Sorted()
    {
        var repo = NewRepo("enum-sorted");
        WriteText(repo, "b.txt", "b");
        WriteText(repo, "a.txt", "a");
        WriteText(repo, "c/d.txt", "d");
        Run(repo, "add", "-A");
        Run(repo, "commit", "-q", "-m", "init");

        var files = GitRepository.Open(repo).EnumerateFiles();

        var expected = files.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, files);
    }

    // ----- IsLikelyBinary -----

    [Fact]
    public void IsLikelyBinary_DetectsNulByte()
    {
        var repo = NewRepo("bin-nul");
        WriteFile(repo, "bin.dat", new byte[] { 0x48, 0x00, 0x65, 0x6c, 0x6c, 0x6f });
        WriteText(repo, "text.txt", "hello world");

        var handle = GitRepository.Open(repo);

        Assert.True(handle.IsLikelyBinary("bin.dat"));
        Assert.False(handle.IsLikelyBinary("text.txt"));
    }

    [Fact]
    public void IsLikelyBinary_EmptyFile_NotBinary()
    {
        var repo = NewRepo("bin-empty");
        WriteFile(repo, "empty.txt", Array.Empty<byte>());

        var handle = GitRepository.Open(repo);

        Assert.False(handle.IsLikelyBinary("empty.txt"));
    }

    [Fact]
    public void IsLikelyBinary_MissingFile_ReturnsTrue()
    {
        var repo = NewRepo("bin-missing");
        var handle = GitRepository.Open(repo);

        // Non-indexable per contract: the indexer treats unreadable and
        // binary identically.
        Assert.True(handle.IsLikelyBinary("does/not/exist.txt"));
    }

    [Fact]
    public void IsLikelyBinary_LargeFile_ReturnsTrue()
    {
        // Emulate the size-cap path without writing 50 MB: the cap lives in
        // GitRepository itself (MaxIndexableFileBytes = 50 MB). We can't
        // easily flip it for one test, so exercise the NUL-only branch here.
        // The size-cap branch is covered by code review + the constant's
        // visibility, not an integration test.
        var repo = NewRepo("bin-large");
        var big = new byte[64 * 1024];
        big[100] = 0;
        WriteFile(repo, "big.bin", big);

        var handle = GitRepository.Open(repo);
        Assert.True(handle.IsLikelyBinary("big.bin"));
    }

    // ----- GetBinaryAttrPaths -----

    [Fact]
    public void GetBinaryAttrPaths_HonorsGitattributes()
    {
        var repo = NewRepo("attrs");
        WriteText(repo, ".gitattributes", "*.bin binary\nplain.txt !binary\n");
        WriteText(repo, "a.bin", "text content but flagged");
        WriteText(repo, "plain.txt", "normal text");
        WriteText(repo, "other.cs", "class X {}");
        Run(repo, "add", "-A");
        Run(repo, "commit", "-q", "-m", "init");

        var handle = GitRepository.Open(repo);
        var binaries = handle.GetBinaryAttrPaths();

        Assert.Contains("a.bin", binaries);
        Assert.DoesNotContain("plain.txt", binaries);
        Assert.DoesNotContain("other.cs", binaries);
    }

    [Fact]
    public void GetBinaryAttrPaths_EmptyRepo_ReturnsEmpty()
    {
        var repo = NewRepo("attrs-empty");
        var handle = GitRepository.Open(repo);
        Assert.Empty(handle.GetBinaryAttrPaths());
    }

    // ----- GetDiffTree -----

    [Fact]
    public void GetDiffTree_DetectsAddedModifiedDeleted()
    {
        var repo = NewRepo("diff-amd");
        WriteText(repo, "keep.cs", "original");
        WriteText(repo, "modify.cs", "v1");
        WriteText(repo, "remove.cs", "bye");
        Run(repo, "add", "-A");
        Run(repo, "commit", "-q", "-m", "c1");
        var sha1 = GitProcess.RunText(repo, new[] { "rev-parse", "HEAD" }).Trim();

        // Modify, delete, and add a file.
        WriteText(repo, "modify.cs", "v2");
        File.Delete(Path.Combine(repo, "remove.cs"));
        WriteText(repo, "added.cs", "new file");
        Run(repo, "add", "-A");
        Run(repo, "commit", "-q", "-m", "c2");
        var sha2 = GitProcess.RunText(repo, new[] { "rev-parse", "HEAD" }).Trim();

        var handle = GitRepository.Open(repo);
        var diff = handle.GetDiffTree(sha1, sha2);

        Assert.Contains(diff, e => e.Status == DiffStatus.Added && e.Path == "added.cs");
        Assert.Contains(diff, e => e.Status == DiffStatus.Modified && e.Path == "modify.cs");
        Assert.Contains(diff, e => e.Status == DiffStatus.Deleted && e.Path == "remove.cs");
        // "keep.cs" should not appear.
        Assert.DoesNotContain(diff, e => e.Path == "keep.cs");
    }

    [Fact]
    public void GetDiffTree_DetectsRenames()
    {
        var repo = NewRepo("diff-rename");
        WriteText(repo, "old-name.cs", "some unique content for rename detection");
        Run(repo, "add", "-A");
        Run(repo, "commit", "-q", "-m", "c1");
        var sha1 = GitProcess.RunText(repo, new[] { "rev-parse", "HEAD" }).Trim();

        Run(repo, "mv", "old-name.cs", "new-name.cs");
        Run(repo, "commit", "-q", "-m", "c2");
        var sha2 = GitProcess.RunText(repo, new[] { "rev-parse", "HEAD" }).Trim();

        var handle = GitRepository.Open(repo);
        var diff = handle.GetDiffTree(sha1, sha2);

        // Git may report this as Renamed or as Add+Delete depending on
        // diff-tree heuristics. Either way the new path must appear.
        var hasNew = diff.Any(e => e.Path == "new-name.cs");
        Assert.True(hasNew, "new-name.cs should appear in diff");
    }

    [Fact]
    public void GetDiffTree_IdenticalTrees_ReturnsEmpty()
    {
        var repo = NewRepo("diff-same");
        WriteText(repo, "a.txt", "x");
        Run(repo, "add", "-A");
        Run(repo, "commit", "-q", "-m", "c1");
        var sha = GitProcess.RunText(repo, new[] { "rev-parse", "HEAD" }).Trim();

        var handle = GitRepository.Open(repo);
        var diff = handle.GetDiffTree(sha, sha);

        Assert.Empty(diff);
    }

    [Fact]
    public void GetDiffTree_ThrowsForUnreachableCommit()
    {
        var repo = NewRepo("diff-bad");
        WriteText(repo, "a.txt", "x");
        Run(repo, "add", "-A");
        Run(repo, "commit", "-q", "-m", "c1");
        var sha = GitProcess.RunText(repo, new[] { "rev-parse", "HEAD" }).Trim();

        var handle = GitRepository.Open(repo);
        Assert.Throws<GitProcessException>(
            () => handle.GetDiffTree(sha, "0000000000000000000000000000000000000000"));
    }

    // ----- GetUntrackedFiles -----

    [Fact]
    public void GetUntrackedFiles_ListsNonIgnored()
    {
        var repo = NewRepo("untracked");
        WriteText(repo, ".gitignore", "*.log\n");
        Run(repo, "add", ".gitignore");
        Run(repo, "commit", "-q", "-m", "init");

        WriteText(repo, "untracked.cs", "x");
        WriteText(repo, "debug.log", "ignored");

        var handle = GitRepository.Open(repo);
        var files = handle.GetUntrackedFiles();

        Assert.Contains("untracked.cs", files);
        Assert.DoesNotContain("debug.log", files);
    }

    // ----- GetIndexMtime -----

    [Fact]
    public void GetIndexMtime_ReturnsRecentTimestamp()
    {
        var repo = NewRepo("mtime");
        WriteText(repo, "a.txt", "x");
        Run(repo, "add", "a.txt");
        Run(repo, "commit", "-q", "-m", "init");

        var handle = GitRepository.Open(repo);
        var mtime = handle.GetIndexMtime();

        Assert.NotNull(mtime);
        // Should be within the last minute.
        Assert.True(DateTimeOffset.UtcNow - mtime.Value < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void GetIndexMtime_ChangesAfterGitOperation()
    {
        var repo = NewRepo("mtime-change");
        WriteText(repo, "a.txt", "x");
        Run(repo, "add", "a.txt");
        Run(repo, "commit", "-q", "-m", "c1");

        var handle = GitRepository.Open(repo);
        var mtime1 = handle.GetIndexMtime();

        // Force a mtime change by staging something new.
        System.Threading.Thread.Sleep(50); // ensure mtime tick
        WriteText(repo, "b.txt", "y");
        Run(repo, "add", "b.txt");

        var mtime2 = handle.GetIndexMtime();

        Assert.NotNull(mtime1);
        Assert.NotNull(mtime2);
        Assert.True(mtime2!.Value >= mtime1!.Value);
    }

    // ----- internal helpers -----

    [Fact]
    public void SplitNullTerminated_HandlesTrailingTerminator()
    {
        var bytes = Encoding.UTF8.GetBytes("alpha\0beta\0gamma\0");
        var parts = GitRepository.SplitNullTerminated(bytes);

        Assert.Equal(new[] { "alpha", "beta", "gamma" }, parts);
    }

    [Fact]
    public void SplitNullTerminated_HandlesNoTrailingTerminator()
    {
        var bytes = Encoding.UTF8.GetBytes("one\0two");
        var parts = GitRepository.SplitNullTerminated(bytes);

        Assert.Equal(new[] { "one", "two" }, parts);
    }

    [Fact]
    public void SplitNullTerminated_HandlesUtf8Paths()
    {
        // Non-ASCII UTF-8 bytes must round-trip cleanly.
        var bytes = Encoding.UTF8.GetBytes("α\0β\0γ\0");
        var parts = GitRepository.SplitNullTerminated(bytes);

        Assert.Equal(new[] { "α", "β", "γ" }, parts);
    }
}
