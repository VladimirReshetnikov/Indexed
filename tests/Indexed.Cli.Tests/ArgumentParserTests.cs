using System.IO;
using Indexed.Abstractions;
using Indexed.Cli;
using Indexed.Targets;
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
    [InlineData("daemons", CliCommand.Daemons)]
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
        Assert.Null(r.IdleTimeoutSeconds);
        Assert.Equal(200, r.MaxMatches);
        Assert.Equal(20, r.MaxMatchesPerFile);
        Assert.Equal(10000, r.TimeoutMs);
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
    public void Find_SingleRoot_BarePath_Parses()
    {
        var r = ArgumentParser.Parse(new[] { "find", "p", "--root", @"C:\src\workspace" });

        Assert.Equal(CliCommand.Find, r.Command);
        var root = Assert.Single(r.Roots!);
        Assert.Null(root.Name);
        Assert.Equal(Path.GetFullPath(@"C:\src\workspace"), root.Path);
    }

    [Fact]
    public void Find_SingleRoot_PathContainingEquals_StillParsesAsBarePath()
    {
        var r = ArgumentParser.Parse(new[] { "find", "p", "--root", @"C:\src\dir=with-equals" });

        Assert.Equal(CliCommand.Find, r.Command);
        var root = Assert.Single(r.Roots!);
        Assert.Null(root.Name);
        Assert.Equal(Path.GetFullPath(@"C:\src\dir=with-equals"), root.Path);
    }

    [Fact]
    public void Find_SingleRoot_LabelSyntax_RendersDiagnostic()
    {
        var r = ArgumentParser.Parse(new[] { "find", "p", "--root", @"sdk=C:\src\sdk" });

        Assert.Equal(CliCommand.Help, r.Command);
        Assert.Contains("bare path", r.Diagnostic);
    }

    [Fact]
    public void Find_MultiRoot_LabeledSyntax_Parses()
    {
        var r = ArgumentParser.Parse(new[]
        {
            "find", "p",
            "--root", @"docs=C:\src\docs",
            "--root", @"sdk=C:\src\sdk",
        });

        Assert.Equal(CliCommand.Find, r.Command);
        Assert.Collection(
            r.Roots!,
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
    public void Find_MultiRoot_RequiresLabels()
    {
        var r = ArgumentParser.Parse(new[]
        {
            "find", "p",
            "--root", @"C:\src\docs",
            "--root", @"sdk=C:\src\sdk",
        });

        Assert.Equal(CliCommand.Help, r.Command);
        Assert.Contains("LABEL=PATH", r.Diagnostic);
    }

    [Fact]
    public void Find_RepoRoot_And_Root_AreMutuallyExclusive()
    {
        var r = ArgumentParser.Parse(new[]
        {
            "find", "p",
            "--repo-root", @"C:\repo",
            "--root", @"C:\tree",
        });

        Assert.Equal(CliCommand.Help, r.Command);
        Assert.Contains("mutually exclusive", r.Diagnostic);
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
    public void Find_TimeoutMs_Parsed()
    {
        var r = ArgumentParser.Parse(new[] { "find", "p", "--timeout-ms", "30000" });

        Assert.Equal(CliCommand.Find, r.Command);
        Assert.Equal(30000, r.TimeoutMs);
    }

    [Fact]
    public void Find_NonIntegerValue_RendersDiagnostic()
    {
        var r = ArgumentParser.Parse(new[] { "find", "p", "--max-matches", "x" });
        Assert.Equal(CliCommand.Help, r.Command);
        Assert.Contains("--max-matches", r.Diagnostic);
    }

    [Fact]
    public void Find_TimeoutMs_NonInteger_RendersDiagnostic()
    {
        var r = ArgumentParser.Parse(new[] { "find", "p", "--timeout-ms", "x" });
        Assert.Equal(CliCommand.Help, r.Command);
        Assert.Contains("--timeout-ms", r.Diagnostic);
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
        Assert.False(r.NoDefaultDirectoryExcludes);
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

    [Fact]
    public void Find_IndexIncludeGlobs_Accumulate()
    {
        var r = ArgumentParser.Parse(new[]
        {
            "find", "foo", "--include-index", "**/*.cs", "--include-index", "docs/**/*.md",
        });

        Assert.Equal(CliCommand.Find, r.Command);
        Assert.Equal(new[] { "**/*.cs", "docs/**/*.md" }, r.IndexIncludeGlob);
    }

    [Fact]
    public void Find_MaxIndexableFileBytes_Parsed()
    {
        var r = ArgumentParser.Parse(new[] { "find", "foo", "--max-indexable-file-bytes", "12345" });

        Assert.Equal(CliCommand.Find, r.Command);
        Assert.Equal(12345, r.MaxIndexableFileBytes);
    }

    [Fact]
    public void Find_MaxIndexableFileMb_ParsedAsBytes()
    {
        var r = ArgumentParser.Parse(new[] { "find", "foo", "--max-indexable-file-mb", "2" });

        Assert.Equal(CliCommand.Find, r.Command);
        Assert.Equal(2L * 1024 * 1024, r.MaxIndexableFileBytes);
    }

    [Fact]
    public void Find_MaxIndexableFileBytes_RejectsZero()
    {
        var r = ArgumentParser.Parse(new[] { "find", "foo", "--max-indexable-file-bytes", "0" });

        Assert.Equal(CliCommand.Help, r.Command);
        Assert.Contains("positive", r.Diagnostic);
    }

    [Fact]
    public void Find_IndexUpdatesManual_Parsed()
    {
        var r = ArgumentParser.Parse(new[] { "find", "foo", "--index-updates", "manual" });

        Assert.Equal(CliCommand.Find, r.Command);
        Assert.Equal(IndexUpdateMode.Manual, r.UpdateMode);
    }

    [Fact]
    public void Find_ManualIndexUpdatesAlias_Parsed()
    {
        var r = ArgumentParser.Parse(new[] { "find", "foo", "--manual-index-updates" });

        Assert.Equal(CliCommand.Find, r.Command);
        Assert.Equal(IndexUpdateMode.Manual, r.UpdateMode);
    }

    [Fact]
    public void Find_IndexUpdates_RejectsUnknownMode()
    {
        var r = ArgumentParser.Parse(new[] { "find", "foo", "--index-updates", "sometimes" });

        Assert.Equal(CliCommand.Help, r.Command);
        Assert.Contains("live or manual", r.Diagnostic);
    }

    [Fact]
    public void Find_NoDefaultDirectoryExcludes_WithRoot_SetsTrue()
    {
        var r = ArgumentParser.Parse(new[]
        {
            "find", "foo", "--root", @"C:\tree", "--no-default-directory-excludes",
        });

        Assert.Equal(CliCommand.Find, r.Command);
        Assert.True(r.NoDefaultDirectoryExcludes);
    }

    [Fact]
    public void Find_NoDefaultDirectoryExcludes_WithoutRoot_RendersDiagnostic()
    {
        var r = ArgumentParser.Parse(new[] { "find", "foo", "--no-default-directory-excludes" });

        Assert.Equal(CliCommand.Help, r.Command);
        Assert.Contains("--root", r.Diagnostic);
    }

    // ----- --idle-timeout-seconds -----

    [Theory]
    [InlineData("status")]
    [InlineData("find")]
    public void IdleTimeoutSeconds_Parsed(string verb)
    {
        var r = verb == "find"
            ? ArgumentParser.Parse(new[] { "find", "foo", "--idle-timeout-seconds", "10" })
            : ArgumentParser.Parse(new[] { "status", "--idle-timeout-seconds", "10" });

        Assert.Equal(10, r.IdleTimeoutSeconds);
    }

    [Fact]
    public void IdleTimeoutSeconds_NonInteger_RendersDiagnostic()
    {
        var r = ArgumentParser.Parse(new[] { "status", "--idle-timeout-seconds", "x" });
        Assert.Equal(CliCommand.Help, r.Command);
        Assert.Contains("--idle-timeout-seconds", r.Diagnostic);
    }

    [Theory]
    [InlineData("daemons")]
    [InlineData("daemons", "--json")]
    public void Daemons_Parses(params string[] args)
    {
        var r = ArgumentParser.Parse(args);
        Assert.Equal(CliCommand.Daemons, r.Command);
    }

    [Fact]
    public void Daemons_RejectsUnsupportedOptions()
    {
        var r = ArgumentParser.Parse(new[] { "daemons", "--root", @"C:\tree" });

        Assert.Equal(CliCommand.Help, r.Command);
        Assert.Contains("only supports --json", r.Diagnostic);
    }
}
