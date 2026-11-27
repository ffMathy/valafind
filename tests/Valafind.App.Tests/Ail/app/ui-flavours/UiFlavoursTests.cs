// ail-derived-from: UiFlavours@7f4ee6082d774ca1
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Valafind.App;
using Valafind.App.Tests.Headless;
using Xunit;

namespace Valafind.App.Tests.Ail;

[Collection(HeadlessUiDefinition.Name)]
public sealed class UiFlavoursTests(HeadlessFixture fixture)
{
    private static AppOptions WithFlavor(string flavorName)
    {
        object boxed = new AppOptions();
        var property = typeof(AppOptions).GetProperty("Flavor")
            ?? throw new InvalidOperationException("AppOptions has no Flavor property");
        property.SetValue(boxed, Enum.Parse(property.PropertyType, flavorName));
        return (AppOptions)boxed;
    }

    private static List<string> TextsOf(Avalonia.Controls.Window window) =>
        AppHarness.Texts(window)
            .Where(t => t is not null)
            .Select(t => t.Trim())
            .ToList();

    private static int IndexOfExact(List<string> texts, string text) =>
        texts.FindIndex(t => string.Equals(t, text, StringComparison.Ordinal));

    // A key hint shows its keys and its word in one text, as "↑↓ navigate": find the text that ends in the word.
    private static int IndexOfHint(List<string> texts, string text) =>
        IndexOfExact(texts, text) is var exact and >= 0
            ? exact
            : texts.FindIndex(t => t.EndsWith(" " + text, StringComparison.Ordinal));

    // Where the text that shows this word starts, from the window's left edge.
    private static double LeftOf(Avalonia.Controls.Window window, string text)
    {
        var block = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .Where(v => v.IsEffectivelyVisible && OwnText(v)?.Trim() is { } shown
                && (shown == text || shown.EndsWith(" " + text, StringComparison.Ordinal)))
            .FirstOrDefault() ?? throw new InvalidOperationException($"No text shows \"{text}\"");
        return Avalonia.VisualExtensions.TranslatePoint(block, new Avalonia.Point(0, 0), window)?.X
            ?? throw new InvalidOperationException($"\"{text}\" is not laid out");
    }

    private static string? OwnText(Avalonia.Visual visual) => visual switch
    {
        Avalonia.Controls.TextBlock block => block.Text
            ?? string.Concat(block.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(run => run.Text) ?? []),
        Avalonia.Controls.Presenters.TextPresenter presenter => presenter.Text,
        _ => null,
    };

    private static void AssertShows(List<string> texts, string text) =>
        Assert.True(
            IndexOfHint(texts, text) >= 0,
            $"Expected the launcher to show \"{text}\". It shows: {string.Join(" | ", texts)}");

    [Fact(DisplayName = "the Windows design")]
    public Task TheWindowsDesign() => AppHarness.RunAsync(fixture, async app =>
    {
        await app.StartAsync();
        await app.WaitUntilIndexedAsync();

        var texts = TextsOf(app.LauncherWindow);

        AssertShows(texts, "Search files by meaning");
        AssertShows(texts, "Any time");
        Assert.NotEmpty(app.Launcher.Filters);

        var searchLine = IndexOfExact(texts, "Search files by meaning");
        var anyTime = IndexOfExact(texts, "Any time");
        Assert.True(searchLine < anyTime, "\"Any time\" should sit under the search line");

        foreach (var key in new[] { "Navigate", "Open", "Show in folder", "Preview" })
        {
            AssertShows(texts, key);
        }
        AssertShows(texts, "Up to date");

        AppHarness.SaveScreen(app.LauncherWindow, "the-windows-design.png");
    }, WithFlavor("Windows"));

    [Fact(DisplayName = "the Mac design")]
    public Task TheMacDesign() => AppHarness.RunAsync(fixture, async app =>
    {
        await app.StartAsync();
        await app.WaitUntilIndexedAsync();

        var texts = TextsOf(app.LauncherWindow);

        AssertShows(texts, "Describe what you are looking for");
        Assert.NotEmpty(app.Launcher.Filters);

        var shortcutMentions = texts.Count(t => t.Contains("⌃⌥Space", StringComparison.Ordinal));
        Assert.True(
            shortcutMentions >= 2,
            $"Expected \"⌃⌥Space\" in the welcome and in the foot. It shows: {string.Join(" | ", texts)}");

        AssertShows(texts, "Up to date");
        var upToDate = LeftOf(app.LauncherWindow, "Up to date");
        foreach (var key in new[] { "Navigate", "Open", "Reveal" })
        {
            AssertShows(texts, key);
            Assert.True(
                LeftOf(app.LauncherWindow, key) > upToDate,
                $"\"{key}\" should sit on the other side of \"Up to date\" in the foot");
        }

        AppHarness.SaveScreen(app.LauncherWindow, "the-mac-design.png");
    }, WithFlavor("Mac"));

    [Fact(DisplayName = "the Linux design")]
    public Task TheLinuxDesign() => AppHarness.RunAsync(fixture, async app =>
    {
        await app.StartAsync();
        await app.WaitUntilIndexedAsync();

        var texts = TextsOf(app.LauncherWindow);

        AssertShows(texts, "Best matches by meaning");
        Assert.NotEmpty(app.Launcher.Filters);

        AssertShows(texts, "Up to date");
        foreach (var key in new[] { "navigate", "open", "show in Files", "preview" })
        {
            AssertShows(texts, key);
        }

        AppHarness.SaveScreen(app.LauncherWindow, "the-linux-design.png");
    });
}
