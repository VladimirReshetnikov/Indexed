using Indexed.Service;
using Xunit;

namespace Indexed.Service.Tests;

public sealed class RepoIdTests
{
    [Fact]
    public void Compute_Returns12HexChars()
    {
        var id = RepoId.Compute(@"C:\repos\foo", "9d348b9fa1b2c3d4e5f60718293a4b5c6d7e8f90");

        Assert.Equal(12, id.Length);
        Assert.Matches("^[0-9a-f]{12}$", id);
    }

    [Fact]
    public void Compute_DiffersForDifferentPaths()
    {
        var a = RepoId.Compute(@"C:\repos\foo", "abc");
        var b = RepoId.Compute(@"C:\repos\bar", "abc");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_DiffersForDifferentFirstCommits()
    {
        var a = RepoId.Compute(@"C:\repos\foo", "aaa");
        var b = RepoId.Compute(@"C:\repos\foo", "bbb");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_StableAcrossCalls()
    {
        // RepoId is a directory-name component; stability across process
        // invocations is a hard requirement.
        var a = RepoId.Compute(@"C:\repos\foo", "abc");
        var b = RepoId.Compute(@"C:\repos\foo", "abc");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_HandlesEmptyFirstCommit()
    {
        // Empty repo (no commits) — id should still compute without throwing.
        var id = RepoId.Compute(@"C:\repos\foo", string.Empty);

        Assert.Matches("^[0-9a-f]{12}$", id);
    }
}
