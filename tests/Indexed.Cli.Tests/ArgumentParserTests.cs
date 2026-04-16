using Indexed.Abstractions;
using Indexed.Cli;
using Xunit;

namespace Indexed.Cli.Tests;

public sealed class ArgumentParserTests
{
    // ----- verb dispatch -----

    [Fact]
    public void NoArgs_RendersHelp()
    {
        var r = ArgumentParser.Parse(System.Array.Empty<string>());
        Assert.Equal(CliCommand.Help, r.Command);
        Assert.Null(r.Diagnostic);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public void HelpFlag_RendersHelp(string flag)
    {
        var r = ArgumentParser.Parse(new[] { flag });
        Assert.Equal(CliCommand.Help, r.Command);
    }

    [Theory]
    [InlineData("status", CliCommand.Status)]
    [InlineData("rescan", CliCommand.Rescan)]
    [InlineData("stop", CliCommand.Stop)]
    public void BareVerb_Parses(string arg, CliCommand expected)
    {
        var r = ArgumentParser.Parse(new[] { arg });
        Assert.Equal(expected, r.Command);
    }

    [Fact]
    public void UnknownVerb_RendersHelpWithDiagnostic()
    {
        var r = ArgumentParser.Parse(new[] { "bogus" });
        Assert.Equal(CliCommand.Help, r.Command);
        Assert.Contains("bogus", r.Diagnostic);
    }

    // ----- find -----

    [Fact]
    public void Find_WithoutPattern_RendersDiagnostic()
    {
        var r = ArgumentParser.Parse(new[] { "find" });
        Assert.Equal(CliCommand.Help, r.Command);
        Assert.Contains("pattern", r.Diagnostic);
    }

    [Fact]
    public void Find_PatternOnly_DefaultsApplied()
    {
        var r = ArgumentParser.Parse(new[] { "find", "foo" });

        Assert.Equal(CliCommand.Find, r.Command);
        Assert.Equal("foo", r.Pattern);
        Assert.Equal(QueryMode.Auto, r.Mode);
        Assert.False(r.IsRegex);
        Assert.False(r.CaseSensitive);
        Assert.False(r.EmitJson);
        Assert.Equal(200, r.MaxMatches);
        Assert.Equal(20, r.MaxMatchesPerFile);
    }

    [Theory]
    [InlineData("auto", QueryMode.Auto)]
    [InlineData("code", QueryMode.Code)]
    [InlineData("prose", QueryMode.Prose)]
    public void Find_Mode_Parsed(string spec, QueryMode expected)
    {
        var r = ArgumentParser.Parse(new[] { "find", "p", "--mode", spec });
        Assert.Equal(expected, r.Mode);
    }

    [Fact]
    public void Find_InvalidMode_RendersDiagnostic()
    {
        var r = ArgumentParser.Parse(new[] { "find", "p", "--mode", "lexer" });
        Assert.Equal(CliCommand.Help, r.Command);
        Assert.Contains("lexer", r.Diagnostic);
    }

    [Fact]
    public void Find_RegexFlag()
    {
        var r = ArgumentParser.Parse(new[] { "find", "p", "--regex" });
        Assert.True(r.IsRegex);

        r = ArgumentParser.Parse(new[] { "find", "p", "-e" });
        Assert.True(r.IsRegex);
    }

    [Fact]
    public void Find_MultipleExcludes_Accumulate()
    {
        var r = ArgumentParser.Parse(new[]
        {
            "find", "p", "--exclude", "**/bin/**", "--exclude", "**/obj/**",
        });
        Assert.Equal(new[] { "**/bin/**", "**/obj/**" }, r.ExcludeGlob);
    }

    [Fact]
    public void Find_MultipleKinds_Accumulate()
    {
        var r = ArgumentParser.Parse(new[]
        {
            "find", "p", "--kind", "code", "--kind", "xml-doc",
        });
        Assert.Equal(new[] { SpanKind.Code, SpanKind.XmlDoc }, r.KindFilter);
    }

    [Fact]
    public void Find_SymmetricContextFlag()
    {
        var r = ArgumentParser.Parse(new[] { "find", "p", "-C", "3" });
        Assert.Equal(3, r.ContextBefore);
        Assert.Equal(3, r.ContextAfter);
    }

    [Fact]
    public void Find_AsymmetricContextFlags()
    {
        var r = ArgumentParser.Parse(new[]
        {
            "find", "p", "--context-before", "2", "--context-after", "5",
        });
        Assert.Equal(2, r.ContextBefore);
        Assert.Equal(5, r.ContextAfter);
    }

    [Fact]
    public void Find_GlobFlag()
    {
        var r = ArgumentParser.Parse(new[] { "find", "p", "-g", "src/**/*.cs" });
        Assert.Equal("src/**/*.cs", r.PathGlob);
    }

    [Fact]
    public void Find_JsonFlag()
    {
        var r = ArgumentParser.Parse(new[] { "find", "p", "--json" });
        Assert.True(r.EmitJson);
    }

    [Fact]
    public void Find_RepoRootFlag()
    {
        var r = ArgumentParser.Parse(new[] { "find", "p", "--repo-root", @"C:\x" });
        Assert.Equal(@"C:\x", r.RepoRoot);
    }

    [Fact]
    public void Find_UnknownOption_RendersDiagnostic()
    {
        var r = ArgumentParser.Parse(new[] { "find", "p", "--wat" });
        Assert.Equal(CliCommand.Help, r.Command);
        Assert.Contains("--wat", r.Diagnostic);
    }

    [Fact]
    public void Find_MissingFlagValue_RendersDiagnostic()
    {
        var r = ArgumentParser.Parse(new[] { "find", "p", "--glob" });
        Assert.Equal(CliCommand.Help, r.Command);
        Assert.Contains("--glob", r.Diagnostic);
    }

    [Fact]
    public void Find_MaxMatches_Parsed()
    {
        var r = ArgumentParser.Parse(new[]
        {
            "find", "p", "--max-matches", "50", "--max-matches-per-file", "5",
        });
        Assert.Equal(50, r.MaxMatches);
        Assert.Equal(5, r.MaxMatchesPerFile);
    }

    [Fact]
    public void Find_NonIntegerValue_RendersDiagnostic()
    {
        var r = ArgumentParser.Parse(new[] { "find", "p", "--max-matches", "x" });
        Assert.Equal(CliCommand.Help, r.Command);
        Assert.Contains("--max-matches", r.Diagnostic);
    }

    [Fact]
    public void Find_ExtraPositional_RendersDiagnostic()
    {
        var r = ArgumentParser.Parse(new[] { "find", "one", "two" });
        Assert.Equal(CliCommand.Help, r.Command);
    }

    // ----- --no-default-excludes -----

    [Fact]
    public void Find_NoDefaultExcludes_DefaultIsFalse()
    {
        var r = ArgumentParser.Parse(new[] { "find", "foo" });
        Assert.False(r.NoDefaultExcludes);
    }

    [Fact]
    public void Find_NoDefaultExcludes_FlagSetsTrue()
    {
        var r = ArgumentParser.Parse(new[] { "find", "foo", "--no-default-excludes" });
        Assert.Equal(CliCommand.Find, r.Command);
        Assert.True(r.NoDefaultExcludes);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("rescan")]
    [InlineData("stop")]
    public void NonFind_NoDefaultExcludes_FlagAccepted(string verb)
    {
        // --no-default-excludes is forwarded to the daemon regardless of verb.
        var r = ArgumentParser.Parse(new[] { verb, "--no-default-excludes" });
        Assert.Equal(verb switch
        {
            "status" => CliCommand.Status,
            "rescan" => CliCommand.Rescan,
            "stop"   => CliCommand.Stop,
            _        => CliCommand.Help,
        }, r.Command);
        Assert.True(r.NoDefaultExcludes);
    }

    [Fact]
    public void Find_NoDefaultExcludes_ComposesWithExcludeIndex()
    {
        var r = ArgumentParser.Parse(new[]
        {
            "find", "foo", "--no-default-excludes", "--exclude-index", "lib/**",
        });
        Assert.True(r.NoDefaultExcludes);
        Assert.Equal(new[] { "lib/**" }, r.IndexExcludeGlob);
    }
}
