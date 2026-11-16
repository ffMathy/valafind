using System.Text;
using Valafind.Core.Extraction;
using Xunit;

namespace Valafind.Core.Tests.Extraction;

public sealed class TextSnifferTests
{
    [Fact]
    public void Empty_IsNotText() => Assert.False(TextSniffer.LooksLikeText([]));

    [Theory]
    [InlineData("plain ascii text\r\nwith lines\tand tabs")]
    [InlineData("Ünïcödé text in UTF-8 — with dashes ✓ and emoji 😀")]
    [InlineData("\u001b[32mcolored log\u001b[0m")]
    public void Utf8Text_IsText(string text) => Assert.True(TextSniffer.LooksLikeText(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Utf8_TruncatedMidSequence_IsText()
    {
        var bytes = Encoding.UTF8.GetBytes("abc 😀");
        Assert.True(TextSniffer.LooksLikeText(bytes.AsSpan(0, bytes.Length - 2)));
    }

    [Fact]
    public void Boms_AreText()
    {
        Assert.True(TextSniffer.LooksLikeText(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("hi")).ToArray()));
        Assert.True(TextSniffer.LooksLikeText(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("hello world")).ToArray()));
        Assert.True(TextSniffer.LooksLikeText(Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes("hello")).ToArray()));
        Assert.True(TextSniffer.LooksLikeText(Encoding.UTF32.GetPreamble().Concat(Encoding.UTF32.GetBytes("hello")).ToArray()));
    }

    [Fact]
    public void BomlessUtf16_IsText()
    {
        Assert.True(TextSniffer.LooksLikeText(Encoding.Unicode.GetBytes("Windows tool output, UTF-16 without BOM")));
        Assert.True(TextSniffer.LooksLikeText(Encoding.BigEndianUnicode.GetBytes("Big endian UTF-16 without BOM")));
    }

    [Fact]
    public void Latin1Text_IsText()
    {
        Assert.True(TextSniffer.LooksLikeText(Encoding.Latin1.GetBytes("Café crème brûlée, naïve façade")));
    }

    [Fact]
    public void BinaryData_IsNotText()
    {
        var random = new byte[4096];
        new Random(5).NextBytes(random);
        Assert.False(TextSniffer.LooksLikeText(random));

        // ELF header, PNG header: NULs / control bytes.
        Assert.False(TextSniffer.LooksLikeText([0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, 0, 0, 0, 0]));
        Assert.False(TextSniffer.LooksLikeText([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D]));
    }

    [Fact]
    public void ManyControlCharacters_IsNotText()
    {
        var bytes = Enumerable.Range(0, 1000).Select(i => (byte)(i % 10 == 0 ? 0x01 : 'a')).ToArray();
        Assert.False(TextSniffer.LooksLikeText(bytes));
    }

    [Fact]
    public void HighByteNoise_WithoutNuls_IsNotText()
    {
        var random = new Random(9);
        var bytes = Enumerable.Range(0, 4096).Select(_ => (byte)random.Next(0x80, 0x100)).ToArray();
        Assert.False(TextSniffer.LooksLikeText(bytes));
    }
}
