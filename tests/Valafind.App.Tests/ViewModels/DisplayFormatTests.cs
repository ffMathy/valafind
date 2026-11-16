using Valafind.App.ViewModels;
using Valafind.Core.Abstractions;
using Xunit;

namespace Valafind.App.Tests.ViewModels;

public sealed class DisplayFormatTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 14, 32, 0, TimeSpan.Zero);

    [Fact]
    public void RelativeDateTodayYesterdayAndOlder()
    {
        Assert.Equal("Today 09:12", DisplayFormat.RelativeDate(new DateTimeOffset(2026, 11, 16, 9, 12, 0, TimeSpan.Zero), Now, TimeZoneInfo.Utc));
        Assert.Equal("Yesterday", DisplayFormat.RelativeDate(new DateTimeOffset(2026, 11, 15, 23, 59, 0, TimeSpan.Zero), Now, TimeZoneInfo.Utc));
        Assert.Equal("12 Sep 2025", DisplayFormat.RelativeDate(new DateTimeOffset(2025, 9, 12, 8, 0, 0, TimeSpan.Zero), Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void RelativeDateUsesTheGivenTimeZone()
    {
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
        // 23:30 UTC on the 15th is 01:30 on the 16th at UTC+2 -> today.
        Assert.Equal("Today 01:30", DisplayFormat.RelativeDate(new DateTimeOffset(2026, 11, 15, 23, 30, 0, TimeSpan.Zero), Now, plusTwo));
    }

    [Theory]
    [InlineData("/home/mathias/Documents/Bolig", "/home/mathias", "~/Documents/Bolig")]
    [InlineData("/home/mathias", "/home/mathias/", "~")]
    [InlineData("/home/mathiasx/Documents", "/home/mathias", "/home/mathiasx/Documents")]
    [InlineData("/srv/data", "/home/mathias", "/srv/data")]
    [InlineData("/home/mathias/a", null, "/home/mathias/a")]
    public void ShortenHome(string dir, string? home, string expected) =>
        Assert.Equal(expected, DisplayFormat.ShortenHome(dir, home, StringComparison.Ordinal));

    [Fact]
    public void ShortenHomeIgnoresCaseWhenAsked() =>
        Assert.Equal("~/Documents", DisplayFormat.ShortenHome("/Users/Mathias/Documents", "/users/mathias", StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData("/Users/mathias/Documents/Bolig", "/Users/mathias", "Documents › Bolig")]
    [InlineData("/Users/mathias/Pictures/2024/03", "/Users/mathias", "2024 › 03")]
    [InlineData("/Users/mathias/Downloads", "/Users/mathias", "Downloads")]
    [InlineData(@"C:\Users\mathi\Documents\Bolig", @"C:\Users\mathi", "Documents › Bolig")]
    public void Breadcrumb(string dir, string home, string expected) =>
        Assert.Equal(expected, DisplayFormat.Breadcrumb(dir, home, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void CollapseWhitespaceRemapsHighlights()
    {
        const string text = "  one\n\n two   three  ";
        var i = text.IndexOf("two   three", StringComparison.Ordinal);
        var (collapsed, spans) = DisplayFormat.CollapseWhitespace(text, [new TextSpan(i, "two   three".Length)]);
        Assert.Equal("one two three", collapsed);
        var span = Assert.Single(spans);
        Assert.Equal("two three", collapsed.Substring(span.Start, span.Length));
    }

    [Fact]
    public void CollapseWhitespaceDropsWhitespaceOnlySpansAndClamps()
    {
        var (collapsed, spans) = DisplayFormat.CollapseWhitespace("a   b", [new TextSpan(1, 3), new TextSpan(3, 100), new TextSpan(-5, 2)]);
        Assert.Equal("a b", collapsed);
        Assert.Equal([new TextSpan(2, 1)], spans);
    }

    [Theory]
    [InlineData("Ctrl+Alt+Space", UiFlavor.Windows, new[] { "Ctrl", "Alt", "Space" })]
    [InlineData("Super+Space", UiFlavor.Windows, new[] { "Win", "Space" })]
    [InlineData("Alt+Space", UiFlavor.Mac, new[] { "⌥", "Space" })]
    [InlineData("Cmd+Shift+k", UiFlavor.Mac, new[] { "⌘", "⇧", "K" })]
    [InlineData("Meta+space", UiFlavor.Linux, new[] { "Super", "Space" })]
    public void HotkeyKeys(string chord, UiFlavor flavor, string[] expected) =>
        Assert.Equal(expected, DisplayFormat.HotkeyKeys(chord, flavor));

    [Fact]
    public void HotkeyLabel()
    {
        Assert.Equal("⌥ Space", DisplayFormat.HotkeyLabel("Alt+Space", UiFlavor.Mac));
        Assert.Equal("Super+Space", DisplayFormat.HotkeyLabel("Super+Space", UiFlavor.Linux));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(421_888, "412 KB")]
    [InlineData(2_811_004, "2.7 MB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5 GB")]
    public void FileSize(long bytes, string expected) => Assert.Equal(expected, DisplayFormat.FileSize(bytes));

    [Fact]
    public void NumbersUseInvariantFormatting()
    {
        Assert.Equal("182,930", DisplayFormat.Count(182_930));
        Assert.Equal("92 MB/s", DisplayFormat.Throughput(92d * 1024 * 1024));
        Assert.Equal("0.4 MB/s", DisplayFormat.Throughput(0.4 * 1024 * 1024));
        Assert.Equal("41%", DisplayFormat.Percent(182_930d / 446_120));
        Assert.Equal("0.87", DisplayFormat.Score(0.8749f));
    }

    [Theory]
    [InlineData("/a/b/report.PDF", "PDF")]
    [InlineData("/a/b/Makefile", "FILE")]
    public void ExtensionLabel(string path, string expected) => Assert.Equal(expected, DisplayFormat.ExtensionLabel(path));
}
