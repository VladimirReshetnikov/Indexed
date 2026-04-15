using System;
using System.Text;
using Indexed.Core;
using Xunit;

namespace Indexed.Core.Tests;

/// <summary>
/// Tests for <see cref="TextDecoder"/> — BOM detection, UTF-16/32 handling,
/// and UTF-8 fallback.
/// </summary>
public sealed class TextDecoderTests
{
    [Fact]
    public void Utf8NoBom_DecodesCorrectly()
    {
        var bytes = Encoding.UTF8.GetBytes("hello world");
        Assert.Equal("hello world", TextDecoder.Decode(bytes));
    }

    [Fact]
    public void Utf8Bom_StrippedCorrectly()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var content = Encoding.UTF8.GetBytes("hello");
        var bytes = new byte[bom.Length + content.Length];
        bom.CopyTo(bytes, 0);
        content.CopyTo(bytes, bom.Length);

        Assert.Equal("hello", TextDecoder.Decode(bytes));
    }

    [Fact]
    public void Utf16LE_Bom_DecodesCorrectly()
    {
        var bom = new byte[] { 0xFF, 0xFE };
        var content = Encoding.Unicode.GetBytes("hello");
        var bytes = new byte[bom.Length + content.Length];
        bom.CopyTo(bytes, 0);
        content.CopyTo(bytes, bom.Length);

        Assert.Equal("hello", TextDecoder.Decode(bytes));
    }

    [Fact]
    public void Utf16BE_Bom_DecodesCorrectly()
    {
        var bom = new byte[] { 0xFE, 0xFF };
        var content = Encoding.BigEndianUnicode.GetBytes("hello");
        var bytes = new byte[bom.Length + content.Length];
        bom.CopyTo(bytes, 0);
        content.CopyTo(bytes, bom.Length);

        Assert.Equal("hello", TextDecoder.Decode(bytes));
    }

    [Fact]
    public void Utf32LE_Bom_DecodesCorrectly()
    {
        var bom = new byte[] { 0xFF, 0xFE, 0x00, 0x00 };
        var content = Encoding.UTF32.GetBytes("hi");
        var bytes = new byte[bom.Length + content.Length];
        bom.CopyTo(bytes, 0);
        content.CopyTo(bytes, bom.Length);

        Assert.Equal("hi", TextDecoder.Decode(bytes));
    }

    [Fact]
    public void Utf32BE_Bom_DecodesCorrectly()
    {
        var bom = new byte[] { 0x00, 0x00, 0xFE, 0xFF };
        var enc = new UTF32Encoding(bigEndian: true, byteOrderMark: false);
        var content = enc.GetBytes("hi");
        var bytes = new byte[bom.Length + content.Length];
        bom.CopyTo(bytes, 0);
        content.CopyTo(bytes, bom.Length);

        Assert.Equal("hi", TextDecoder.Decode(bytes));
    }

    [Fact]
    public void InvalidUtf8_ReplacedWithFffd()
    {
        // 0xFF is not valid in any UTF-8 sequence.
        var bytes = new byte[] { 0x68, 0x65, 0xFF, 0x6C, 0x6F };
        var text = TextDecoder.Decode(bytes);

        Assert.Contains("\uFFFD", text);
        Assert.StartsWith("he", text);
        Assert.EndsWith("lo", text);
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TextDecoder.Decode(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void NonAsciiUtf8_RoundTrips()
    {
        var original = "日本語テスト — тест — ñ";
        var bytes = Encoding.UTF8.GetBytes(original);
        Assert.Equal(original, TextDecoder.Decode(bytes));
    }
}
