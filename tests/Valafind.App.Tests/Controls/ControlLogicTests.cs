using Avalonia;
using Avalonia.Input;
using Valafind.App.Controls;
using Valafind.App.ViewModels;
using Valafind.App.Views;
using Valafind.Core.Abstractions;
using Xunit;

namespace Valafind.App.Tests.Controls;

public sealed class ControlLogicTests
{
    [Fact]
    public void SegmentSplitsTextIntoHighlightedRuns()
    {
        var segments = HighlightedTextBlock.Segment("the deposit is reduced", [new TextSpan(4, 7), new TextSpan(15, 100)]);
        Assert.Equal(
            [("the ", false), ("deposit", true), (" is ", false), ("reduced", true)],
            segments);
    }

    [Fact]
    public void SegmentMergesOverlapsAndIgnoresEmpty()
    {
        Assert.Equal([("abcdef", false)], HighlightedTextBlock.Segment("abcdef", [new TextSpan(2, 0), new TextSpan(-3, 2)]));
        Assert.Equal([("a", false), ("bcde", true), ("f", false)], HighlightedTextBlock.Segment("abcdef", [new TextSpan(1, 2), new TextSpan(2, 3)]));
        Assert.Empty(HighlightedTextBlock.Segment(string.Empty, null));
    }

    [Theory]
    [InlineData(KeyModifiers.Control | KeyModifiers.Alt, Key.Space, false, "Ctrl+Alt+Space")]
    [InlineData(KeyModifiers.Meta, Key.Space, false, "Super+Space")]
    [InlineData(KeyModifiers.Meta | KeyModifiers.Shift, Key.K, true, "Shift+Cmd+K")]
    [InlineData(KeyModifiers.Alt, Key.D5, false, "Alt+5")]
    [InlineData(KeyModifiers.Control, Key.F12, false, "Ctrl+F12")]
    public void HotkeyBoxFormatsChords(KeyModifiers mods, Key key, bool mac, string expected) =>
        Assert.Equal(expected, HotkeyBox.FormatChord(mods, key, mac));

    [Theory]
    [InlineData(KeyModifiers.None, Key.A)]
    [InlineData(KeyModifiers.Control, Key.LeftCtrl)]
    public void HotkeyBoxRejectsIncompleteChords(KeyModifiers mods, Key key) =>
        Assert.Null(HotkeyBox.FormatChord(mods, key, false));

    [Fact]
    public void LauncherIsCentredAt22PercentFromTop()
    {
        var area = new PixelRect(0, 40, 1920, 1000);
        var p = LauncherWindow.ComputePosition(area, 1.0, new Size(800, 600), cardTopMargin: 20);
        Assert.Equal(560, p.X);
        Assert.Equal(40 + 220 - 20, p.Y);
    }

    [Fact]
    public void LauncherPlacementHonoursScalingAndStaysOnScreen()
    {
        var area = new PixelRect(1920, 0, 2560, 1400);
        var p = LauncherWindow.ComputePosition(area, 2.0, new Size(800, 706), cardTopMargin: 20);
        Assert.Equal(1920 + (2560 - 1600) / 2, p.X);
        // 1412 px tall window on a 1400 px area: pinned to the top.
        Assert.Equal(0, p.Y);
    }

    [Theory]
    [InlineData(new[] { "--toggle" }, "toggle")]
    [InlineData(new[] { "-s" }, "show")]
    [InlineData(new[] { "/quit" }, "quit")]
    [InlineData(new[] { "--verbose" }, null)]
    [InlineData(new string[0], null)]
    public void ProgramParsesCommands(string[] args, string? expected) => Assert.Equal(expected, Program.ParseCommand(args));

    [Theory]
    [InlineData("windows", UiFlavor.Windows)]
    [InlineData(" MacOS ", UiFlavor.Mac)]
    [InlineData("gnome", UiFlavor.Linux)]
    [InlineData("amiga", null)]
    [InlineData(null, null)]
    public void FlavorOverrideParsing(string? value, UiFlavor? expected) => Assert.Equal(expected, UiFlavors.Parse(value));
}
