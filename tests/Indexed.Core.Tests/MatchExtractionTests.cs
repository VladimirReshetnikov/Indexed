using System;
using Indexed.Core;
using Xunit;

namespace Indexed.Core.Tests;

/// <summary>
/// Tests for <see cref="MatchExtraction"/> — pure helpers that translate between
/// character offsets and line/column coordinates.
/// </summary>
public sealed class MatchExtractionTests
{
    // ===== ComputeLineOffsets =====

    [Fact]
    public void ComputeLineOffsets_EmptyString_ReturnsSingleZeroEntry()
    {
        var offsets = MatchExtraction.ComputeLineOffsets("");
        Assert.Single(offsets);
        Assert.Equal(0, offsets[0]);
    }

    [Fact]
    public void ComputeLineOffsets_SingleLineNoNewline()
    {
        var offsets = MatchExtraction.ComputeLineOffsets("hello");
        Assert.Single(offsets);
        Assert.Equal(0, offsets[0]);
    }

    [Fact]
    public void ComputeLineOffsets_LF()
    {
        // "ab\ncd\ne"
        var offsets = MatchExtraction.ComputeLineOffsets("ab\ncd\ne");
        Assert.Equal(3, offsets.Count);
        Assert.Equal(0, offsets[0]);  // line 1 starts at 0
        Assert.Equal(3, offsets[1]);  // line 2 starts at 3
        Assert.Equal(6, offsets[2]);  // line 3 starts at 6
    }

    [Fact]
    public void ComputeLineOffsets_CRLF()
    {
        // "ab\r\ncd\r\n"
        var offsets = MatchExtraction.ComputeLineOffsets("ab\r\ncd\r\n");
        Assert.Equal(3, offsets.Count);
        Assert.Equal(0, offsets[0]);
        Assert.Equal(4, offsets[1]);  // after "ab\r\n"
        Assert.Equal(8, offsets[2]);  // after "cd\r\n"
    }

    [Fact]
    public void ComputeLineOffsets_BareCR()
    {
        // Classic Mac line endings: "ab\rcd\r"
        var offsets = MatchExtraction.ComputeLineOffsets("ab\rcd\r");
        Assert.Equal(3, offsets.Count);
        Assert.Equal(0, offsets[0]);
        Assert.Equal(3, offsets[1]);
        Assert.Equal(6, offsets[2]);
    }

    [Fact]
    public void ComputeLineOffsets_MixedLineEndings()
    {
        // "a\nb\r\nc\rd"
        var offsets = MatchExtraction.ComputeLineOffsets("a\nb\r\nc\rd");
        Assert.Equal(4, offsets.Count);
        Assert.Equal(0, offsets[0]);  // "a"
        Assert.Equal(2, offsets[1]);  // "b"
        Assert.Equal(5, offsets[2]);  // "c"
        Assert.Equal(7, offsets[3]);  // "d"
    }

    [Fact]
    public void ComputeLineOffsets_TrailingLF_CreatesEmptyLastLine()
    {
        // "x\n" → two lines: "x" and ""
        var offsets = MatchExtraction.ComputeLineOffsets("x\n");
        Assert.Equal(2, offsets.Count);
        Assert.Equal(0, offsets[0]);
        Assert.Equal(2, offsets[1]);
    }

    // ===== LineAndColumnOf =====

    [Fact]
    public void LineAndColumnOf_StartOfFile()
    {
        var offsets = MatchExtraction.ComputeLineOffsets("abc\ndef");
        var (line, col) = MatchExtraction.LineAndColumnOf(0, offsets);
        Assert.Equal(1, line);
        Assert.Equal(1, col);
    }

    [Fact]
    public void LineAndColumnOf_MiddleOfFirstLine()
    {
        var offsets = MatchExtraction.ComputeLineOffsets("abcdef\nghi");
        var (line, col) = MatchExtraction.LineAndColumnOf(3, offsets);
        Assert.Equal(1, line);
        Assert.Equal(4, col);  // 0-based offset 3 → column 4
    }

    [Fact]
    public void LineAndColumnOf_StartOfSecondLine()
    {
        var offsets = MatchExtraction.ComputeLineOffsets("abc\ndef");
        var (line, col) = MatchExtraction.LineAndColumnOf(4, offsets);
        Assert.Equal(2, line);
        Assert.Equal(1, col);
    }

    [Fact]
    public void LineAndColumnOf_EndOfContent()
    {
        var content = "ab\ncd";
        var offsets = MatchExtraction.ComputeLineOffsets(content);
        // Last char 'd' is at offset 4
        var (line, col) = MatchExtraction.LineAndColumnOf(4, offsets);
        Assert.Equal(2, line);
        Assert.Equal(2, col);
    }

    [Fact]
    public void LineAndColumnOf_WithCRLF()
    {
        var content = "ab\r\ncd";
        var offsets = MatchExtraction.ComputeLineOffsets(content);
        // 'c' at offset 4
        var (line, col) = MatchExtraction.LineAndColumnOf(4, offsets);
        Assert.Equal(2, line);
        Assert.Equal(1, col);
    }

    // ===== LineTextAt =====

    [Fact]
    public void LineTextAt_FirstLine()
    {
        var content = "hello\nworld";
        var offsets = MatchExtraction.ComputeLineOffsets(content);
        Assert.Equal("hello", MatchExtraction.LineTextAt(content, offsets, 1));
    }

    [Fact]
    public void LineTextAt_SecondLine()
    {
        var content = "hello\nworld";
        var offsets = MatchExtraction.ComputeLineOffsets(content);
        Assert.Equal("world", MatchExtraction.LineTextAt(content, offsets, 2));
    }

    [Fact]
    public void LineTextAt_StripsTrailingLF()
    {
        var content = "hello\n";
        var offsets = MatchExtraction.ComputeLineOffsets(content);
        Assert.Equal("hello", MatchExtraction.LineTextAt(content, offsets, 1));
    }

    [Fact]
    public void LineTextAt_StripsTrailingCRLF()
    {
        var content = "hello\r\n";
        var offsets = MatchExtraction.ComputeLineOffsets(content);
        Assert.Equal("hello", MatchExtraction.LineTextAt(content, offsets, 1));
    }

    [Fact]
    public void LineTextAt_StripsTrailingBareCR()
    {
        var content = "hello\rworld";
        var offsets = MatchExtraction.ComputeLineOffsets(content);
        Assert.Equal("hello", MatchExtraction.LineTextAt(content, offsets, 1));
    }

    [Fact]
    public void LineTextAt_PastEndReturnsEmpty()
    {
        var content = "one\ntwo";
        var offsets = MatchExtraction.ComputeLineOffsets(content);
        Assert.Equal(string.Empty, MatchExtraction.LineTextAt(content, offsets, 99));
    }

    [Fact]
    public void LineTextAt_ZeroReturnsEmpty()
    {
        var content = "hello";
        var offsets = MatchExtraction.ComputeLineOffsets(content);
        Assert.Equal(string.Empty, MatchExtraction.LineTextAt(content, offsets, 0));
    }

    [Fact]
    public void LineTextAt_EmptyLineInMiddle()
    {
        var content = "a\n\nb";
        var offsets = MatchExtraction.ComputeLineOffsets(content);
        Assert.Equal("", MatchExtraction.LineTextAt(content, offsets, 2));
        Assert.Equal("b", MatchExtraction.LineTextAt(content, offsets, 3));
    }

    // ===== ContextBefore =====

    [Fact]
    public void ContextBefore_ReturnsRequestedLines()
    {
        var content = "line1\nline2\nline3\nline4\nline5";
        var offsets = MatchExtraction.ComputeLineOffsets(content);

        var ctx = MatchExtraction.ContextBefore(content, offsets, matchLine: 4, count: 2);
        Assert.Equal(2, ctx.Length);
        Assert.Equal("line2", ctx[0]);
        Assert.Equal("line3", ctx[1]);
    }

    [Fact]
    public void ContextBefore_ClampedAtFileStart()
    {
        var content = "line1\nline2\nline3";
        var offsets = MatchExtraction.ComputeLineOffsets(content);

        // Request 5 lines before line 2 — only 1 available
        var ctx = MatchExtraction.ContextBefore(content, offsets, matchLine: 2, count: 5);
        Assert.Single(ctx);
        Assert.Equal("line1", ctx[0]);
    }

    [Fact]
    public void ContextBefore_FirstLine_ReturnsEmpty()
    {
        var content = "only\nlines";
        var offsets = MatchExtraction.ComputeLineOffsets(content);

        var ctx = MatchExtraction.ContextBefore(content, offsets, matchLine: 1, count: 3);
        Assert.Empty(ctx);
    }

    [Fact]
    public void ContextBefore_ZeroCount_ReturnsEmpty()
    {
        var content = "a\nb\nc";
        var offsets = MatchExtraction.ComputeLineOffsets(content);
        Assert.Empty(MatchExtraction.ContextBefore(content, offsets, matchLine: 3, count: 0));
    }

    // ===== ContextAfter =====

    [Fact]
    public void ContextAfter_ReturnsRequestedLines()
    {
        var content = "line1\nline2\nline3\nline4\nline5";
        var offsets = MatchExtraction.ComputeLineOffsets(content);

        var ctx = MatchExtraction.ContextAfter(content, offsets, matchLine: 2, count: 2);
        Assert.Equal(2, ctx.Length);
        Assert.Equal("line3", ctx[0]);
        Assert.Equal("line4", ctx[1]);
    }

    [Fact]
    public void ContextAfter_ClampedAtFileEnd()
    {
        var content = "line1\nline2\nline3";
        var offsets = MatchExtraction.ComputeLineOffsets(content);

        // Request 5 lines after line 2 — only 1 available
        var ctx = MatchExtraction.ContextAfter(content, offsets, matchLine: 2, count: 5);
        Assert.Single(ctx);
        Assert.Equal("line3", ctx[0]);
    }

    [Fact]
    public void ContextAfter_LastLine_ReturnsEmpty()
    {
        var content = "a\nb";
        var offsets = MatchExtraction.ComputeLineOffsets(content);

        var ctx = MatchExtraction.ContextAfter(content, offsets, matchLine: 2, count: 3);
        Assert.Empty(ctx);
    }

    [Fact]
    public void ContextAfter_ZeroCount_ReturnsEmpty()
    {
        var content = "a\nb\nc";
        var offsets = MatchExtraction.ComputeLineOffsets(content);
        Assert.Empty(MatchExtraction.ContextAfter(content, offsets, matchLine: 1, count: 0));
    }

    // ===== Unicode =====

    [Fact]
    public void LineAndColumnOf_Unicode_MeasuresInChars()
    {
        // "αβ\nγδ" — each Greek letter is one UTF-16 code unit
        var content = "αβ\nγδ";
        var offsets = MatchExtraction.ComputeLineOffsets(content);
        // 'γ' at char offset 3
        var (line, col) = MatchExtraction.LineAndColumnOf(3, offsets);
        Assert.Equal(2, line);
        Assert.Equal(1, col);
    }

    [Fact]
    public void LineTextAt_Unicode()
    {
        var content = "café\nlatte";
        var offsets = MatchExtraction.ComputeLineOffsets(content);
        Assert.Equal("café", MatchExtraction.LineTextAt(content, offsets, 1));
        Assert.Equal("latte", MatchExtraction.LineTextAt(content, offsets, 2));
    }

    // ===== Round-trip: offset → line/col → text =====

    [Fact]
    public void RoundTrip_OffsetToLineToText()
    {
        var content = "first\nsecond\nthird";
        var offsets = MatchExtraction.ComputeLineOffsets(content);

        // Offset 8 is 's' in "second" → line 2, col 3
        var (line, col) = MatchExtraction.LineAndColumnOf(8, offsets);
        Assert.Equal(2, line);
        Assert.Equal(3, col);

        var text = MatchExtraction.LineTextAt(content, offsets, line);
        Assert.Equal("second", text);
    }
}
