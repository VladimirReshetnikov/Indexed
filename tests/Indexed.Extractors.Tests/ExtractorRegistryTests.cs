using System.Linq;
using Indexed.Abstractions;
using Indexed.Extractors;
using Xunit;

namespace Indexed.Extractors.Tests;

/// <summary>
/// Focused behavioral tests for the built-in extractor registry. These pin the
/// current normalization rules without forcing test code to depend on the
/// individual internal extractor classes directly.
/// </summary>
public sealed class ExtractorRegistryTests
{
    private readonly ExtractorRegistry _registry = ExtractorRegistry.BuildDefault();

    [Fact]
    public void MarkdownFiles_AreIndexedAsSingleWholeFileSpan()
    {
        var spans = _registry.Extract(
            "docs/readme.md",
            "# Title\r\n\r\nHello markdown world.\r\n");

        var span = Assert.Single(spans);
        Assert.Equal(1, span.StartLine);
        Assert.Equal(3, span.EndLine);
        Assert.Equal(SpanKind.Markdown, span.Kind);
        Assert.Equal("# Title\n\nHello markdown world.", span.Content);
    }

    [Fact]
    public void CSharpFiles_ExtractXmlDocs_LineComments_AndBlockComments()
    {
        var content = """
            using System;

            /// <summary>
            /// Hello docs.
            /// <para>Call <see cref="System.Console"/>.</para>
            /// </summary>
            class C
            {
                // first line
                // second line
                /*
                 * block prose
                 */
                void M() { }
            }
            """;

        var spans = _registry.Extract("src/C.cs", content);

        Assert.Equal(3, spans.Count);

        var xmlDoc = spans[0];
        Assert.Equal(SpanKind.XmlDoc, xmlDoc.Kind);
        Assert.Equal(3, xmlDoc.StartLine);
        Assert.Equal(6, xmlDoc.EndLine);
        Assert.Contains("Hello docs.", xmlDoc.Content);
        Assert.Contains("Call System.Console.", xmlDoc.Content);

        var lineComments = spans[1];
        Assert.Equal(SpanKind.LineCommentBlock, lineComments.Kind);
        Assert.Equal(9, lineComments.StartLine);
        Assert.Equal(10, lineComments.EndLine);
        Assert.Equal("first line\nsecond line", lineComments.Content);

        var blockComment = spans[2];
        Assert.Equal(SpanKind.BlockComment, blockComment.Kind);
        Assert.Equal(11, blockComment.StartLine);
        Assert.Equal(13, blockComment.EndLine);
        Assert.Contains("block prose", blockComment.Content);
    }

    [Fact]
    public void PowerShellFiles_IgnoreHashBangAndCaptureLineAndBlockComments()
    {
        var content = """
            #!/usr/bin/env pwsh
            # first
            # second
            Write-Host "hello"
            <#
            block line 1
            block line 2
            #>
            """;

        var spans = _registry.Extract("scripts/tool.ps1", content);

        Assert.Equal(2, spans.Count);

        var lineComments = spans[0];
        Assert.Equal(SpanKind.LineCommentBlock, lineComments.Kind);
        Assert.Equal(2, lineComments.StartLine);
        Assert.Equal(3, lineComments.EndLine);
        Assert.Equal("first\nsecond", lineComments.Content);

        var blockComment = spans[1];
        Assert.Equal(SpanKind.BlockComment, blockComment.Kind);
        Assert.Equal(5, blockComment.StartLine);
        Assert.Equal(8, blockComment.EndLine);
        Assert.Contains("block line 1", blockComment.Content);
        Assert.Contains("block line 2", blockComment.Content);
    }

    [Fact]
    public void UnknownExtensions_ReturnNoSpans()
    {
        var spans = _registry.Extract("image.bin", "plain text that should be ignored");
        Assert.Empty(spans);
    }

    [Fact]
    public void PlainTextExtensions_UsePlainTextKind()
    {
        var spans = _registry.Extract("notes.txt", "alpha\nbeta\n");

        var span = Assert.Single(spans);
        Assert.Equal(SpanKind.PlainText, span.Kind);
        Assert.Equal(1, span.StartLine);
        Assert.Equal(2, span.EndLine);
        Assert.Equal("alpha\nbeta", span.Content);
    }
}
