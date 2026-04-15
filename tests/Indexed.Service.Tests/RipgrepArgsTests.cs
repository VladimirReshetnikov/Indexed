using Indexed.Abstractions;
using Indexed.Service;
using Xunit;

namespace Indexed.Service.Tests;

/// <summary>
/// The actual ripgrep invocation is covered by the end-to-end smoke test
/// (which requires rg on PATH). These unit tests pin the argument-building
/// logic so it can be refactored with confidence.
/// </summary>
public sealed class RipgrepArgsTests
{
    [Fact]
    public void Defaults_UseSmartCaseAndFixedStrings()
    {
        var args = RipgrepSearchBackend.BuildArgs(new SearchRequest("foo"));

        Assert.Contains("--smart-case", args);
        Assert.Contains("--fixed-strings", args);
        Assert.Contains("foo", args);
        Assert.Contains(".", args);
    }

    [Fact]
    public void CaseSensitive_EmitsFlag()
    {
        var args = RipgrepSearchBackend.BuildArgs(
            new SearchRequest("foo", CaseSensitive: true));

        Assert.Contains("--case-sensitive", args);
        Assert.DoesNotContain("--smart-case", args);
    }

    [Fact]
    public void Regex_OmitsFixedStringsFlag()
    {
        var args = RipgrepSearchBackend.BuildArgs(
            new SearchRequest("foo.*", IsRegex: true));

        Assert.DoesNotContain("--fixed-strings", args);
    }

    [Fact]
    public void PathGlob_PassedToRipgrep()
    {
        var args = RipgrepSearchBackend.BuildArgs(
            new SearchRequest("foo", PathGlob: "src/**/*.cs"));

        var idx = args.IndexOf("--glob");
        Assert.True(idx >= 0);
        Assert.Equal("src/**/*.cs", args[idx + 1]);
    }

    [Fact]
    public void ExcludeGlobs_EmittedAsNegatedGlobs()
    {
        var args = RipgrepSearchBackend.BuildArgs(new SearchRequest(
            "foo", ExcludeGlob: new[] { "**/bin/**", "**/obj/**" }));

        Assert.Contains("!**/bin/**", args);
        Assert.Contains("!**/obj/**", args);
    }

    [Fact]
    public void ContextArgs_EmittedWhenNonzero()
    {
        var args = RipgrepSearchBackend.BuildArgs(new SearchRequest(
            "foo", ContextBefore: 3, ContextAfter: 2));

        var bIdx = args.IndexOf("-B");
        var aIdx = args.IndexOf("-A");
        Assert.True(bIdx >= 0 && args[bIdx + 1] == "3");
        Assert.True(aIdx >= 0 && args[aIdx + 1] == "2");
    }

    [Fact]
    public void PatternSeparatedByDoubleDash()
    {
        // Must pass `-- <pattern>` so patterns starting with `-` are not
        // interpreted as flags.
        var args = RipgrepSearchBackend.BuildArgs(new SearchRequest("--weird"));

        var sep = args.IndexOf("--");
        Assert.True(sep >= 0);
        Assert.Equal("--weird", args[sep + 1]);
    }
}
