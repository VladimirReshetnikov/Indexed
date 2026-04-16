using System.Collections.Generic;
using Indexed.Core;
using Xunit;

namespace Indexed.Core.Tests;

/// <summary>
/// Tests for <see cref="ExcludeFilter.DefaultBinaryAdjacentGlobs"/> and
/// <see cref="ExcludeFilter.Combine"/>.
/// </summary>
public sealed class ExcludeFilterDefaultGlobsTests
{
    // ----- DefaultBinaryAdjacentGlobs contents -----

    [Theory]
    [InlineData("frontend/package-lock.json")]
    [InlineData("package-lock.json")]
    [InlineData("some/deep/nested/package-lock.json")]
    public void Defaults_ExcludePackageLockJson(string path)
    {
        var filter = new ExcludeFilter(ExcludeFilter.DefaultBinaryAdjacentGlobs);
        Assert.True(filter.IsExcluded(path), $"expected '{path}' to be excluded");
    }

    [Theory]
    [InlineData("dist/bundle.min.js")]
    [InlineData("public/app.min.css")]
    [InlineData("assets/vendor.min.js")]
    public void Defaults_ExcludeMinifiedBundles(string path)
    {
        var filter = new ExcludeFilter(ExcludeFilter.DefaultBinaryAdjacentGlobs);
        Assert.True(filter.IsExcluded(path), $"expected '{path}' to be excluded");
    }

    [Theory]
    [InlineData("src/app.js.map")]
    [InlineData("dist/bundle.css.map")]
    public void Defaults_ExcludeSourceMaps(string path)
    {
        var filter = new ExcludeFilter(ExcludeFilter.DefaultBinaryAdjacentGlobs);
        Assert.True(filter.IsExcluded(path), $"expected '{path}' to be excluded");
    }

    [Theory]
    [InlineData("yarn.lock")]
    [InlineData("src/yarn.lock")]
    [InlineData("pnpm-lock.yaml")]
    [InlineData("npm-shrinkwrap.json")]
    [InlineData("Cargo.lock")]
    [InlineData("composer.lock")]
    [InlineData("Gemfile.lock")]
    [InlineData("Pipfile.lock")]
    [InlineData("poetry.lock")]
    [InlineData("go.sum")]
    [InlineData("packages.lock.json")]
    public void Defaults_ExcludeEcosystemLockfiles(string path)
    {
        var filter = new ExcludeFilter(ExcludeFilter.DefaultBinaryAdjacentGlobs);
        Assert.True(filter.IsExcluded(path), $"expected '{path}' to be excluded");
    }

    [Theory]
    [InlineData("src/Generated/Foo.generated.cs")]
    [InlineData("src/Grpc/Service.g.cs")]
    [InlineData("src/Grpc/Service.g.i.cs")]
    [InlineData("src/WinForms/MainForm.Designer.cs")]
    public void Defaults_ExcludeGeneratedCSharp(string path)
    {
        var filter = new ExcludeFilter(ExcludeFilter.DefaultBinaryAdjacentGlobs);
        Assert.True(filter.IsExcluded(path), $"expected '{path}' to be excluded");
    }

    [Theory]
    [InlineData("src/Program.cs")]
    [InlineData("src/Services/SearchService.cs")]
    [InlineData("README.md")]
    [InlineData("Cargo.toml")]          // not Cargo.lock
    [InlineData("src/app.js")]          // not app.min.js
    [InlineData("src/package.json")]    // not package-lock.json
    public void Defaults_DoNotExcludeRegularSourceFiles(string path)
    {
        var filter = new ExcludeFilter(ExcludeFilter.DefaultBinaryAdjacentGlobs);
        Assert.False(filter.IsExcluded(path), $"expected '{path}' NOT to be excluded");
    }

    // ----- Combine -----

    [Fact]
    public void Combine_BothNull_ReturnsNull()
    {
        Assert.Null(ExcludeFilter.Combine(null, null));
    }

    [Fact]
    public void Combine_BothEmpty_ReturnsNull()
    {
        Assert.Null(ExcludeFilter.Combine(new List<string>(), new List<string>()));
    }

    [Fact]
    public void Combine_NullAndList_ReturnsSecond()
    {
        var b = new[] { "a/**" };
        var result = ExcludeFilter.Combine(null, b);
        Assert.Same(b, result);
    }

    [Fact]
    public void Combine_ListAndNull_ReturnsFirst()
    {
        var a = new[] { "a/**" };
        var result = ExcludeFilter.Combine(a, null);
        Assert.Same(a, result);
    }

    [Fact]
    public void Combine_BothNonEmpty_ConcatenatesAll()
    {
        var a = new[] { "lib/**", "vendor/**" };
        var b = new[] { "**/package-lock.json", "**/*.min.js" };

        var result = ExcludeFilter.Combine(a, b);

        Assert.NotNull(result);
        Assert.Equal(4, result!.Count);
        Assert.Contains("lib/**", result);
        Assert.Contains("vendor/**", result);
        Assert.Contains("**/package-lock.json", result);
        Assert.Contains("**/*.min.js", result);
    }

    [Fact]
    public void Combine_UserGlobsComposeWithDefaults_AllEntriesActive()
    {
        var userGlobs = new[] { "lib/**" };
        var combined = ExcludeFilter.Combine(userGlobs, ExcludeFilter.DefaultBinaryAdjacentGlobs);
        var filter = new ExcludeFilter(combined);

        // User glob matches
        Assert.True(filter.IsExcluded("lib/some/file.cs"));
        // Default glob matches
        Assert.True(filter.IsExcluded("package-lock.json"));
        // Neither matches
        Assert.False(filter.IsExcluded("src/Main.cs"));
    }

    [Fact]
    public void NoDefaults_UserGlobsOnly_DefaultsNotApplied()
    {
        // When defaults are not combined, default-excluded paths should pass.
        var filter = new ExcludeFilter(new[] { "lib/**" });

        Assert.True(filter.IsExcluded("lib/foo.cs"));
        Assert.False(filter.IsExcluded("package-lock.json")); // default, not active
    }
}
