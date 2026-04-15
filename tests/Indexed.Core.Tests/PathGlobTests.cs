using Indexed.Core;
using Xunit;

namespace Indexed.Core.Tests;

/// <summary>
/// Translation/matching tests for <see cref="PathGlob"/>. Pins gitignore-style
/// glob semantics the public API promises.
/// </summary>
public sealed class PathGlobTests
{
    [Theory]
    [InlineData("*.cs", "foo.cs", true)]
    [InlineData("*.cs", "src/foo.cs", false)]
    [InlineData("**/*.cs", "foo.cs", true)]
    [InlineData("**/*.cs", "src/foo.cs", true)]
    [InlineData("**/*.cs", "a/b/c/foo.cs", true)]
    [InlineData("src/**", "src/foo.cs", true)]
    [InlineData("src/**", "src/a/b/c.cs", true)]
    [InlineData("src/**", "tests/foo.cs", false)]
    [InlineData("foo?bar.txt", "fooXbar.txt", true)]
    [InlineData("foo?bar.txt", "foobar.txt", false)]
    [InlineData("foo?bar.txt", "fooXYbar.txt", false)]
    [InlineData("a/**/b", "a/b", true)]
    [InlineData("a/**/b", "a/x/b", true)]
    [InlineData("a/**/b", "a/x/y/b", true)]
    public void Matches_FollowsGitIgnoreSemantics(string glob, string path, bool expected)
    {
        Assert.Equal(expected, PathGlob.Matches(glob, path));
    }

    [Fact]
    public void Backslashes_NormalizedToForwardSlash()
    {
        Assert.True(PathGlob.Matches("src/**", @"src\a\b.cs"));
    }

    [Fact]
    public void SpecialRegexCharsAreLiteral()
    {
        Assert.True(PathGlob.Matches("a+b.txt", "a+b.txt"));
        Assert.False(PathGlob.Matches("a+b.txt", "aab.txt"));
    }
}
