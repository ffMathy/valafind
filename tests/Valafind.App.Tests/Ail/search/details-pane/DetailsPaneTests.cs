// ail-derived-from: DetailsPane@d76b137f340a14a3
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Valafind.App.Tests.Headless;
using Xunit;

namespace Valafind.App.Tests.Ail;

[Collection(HeadlessUiDefinition.Name)]
public sealed class DetailsPaneTests(HeadlessFixture fixture)
{
    private const double PaneWidth = 440;

    [Fact(DisplayName = "a document in the pane")]
    public Task ADocumentInThePane() => AppHarness.RunAsync(fixture, async app =>
    {
        var content = ("Receipt for the apartment deposit\n" +
                       "Landlord: Bolig Housing ApS\n" +
                       "Tenant: Ana Jensen\n" +
                       "Deposit paid: 15,000 DKK on 1 March\n" +
                       "The deposit is returned when the apartment is handed back clean and undamaged.\n" +
                       "Signed by both parties").PadRight(266, '.');
        Assert.Equal(266, Encoding.UTF8.GetByteCount(content));
        var modified = DateTime.Today.AddHours(20).AddMinutes(30);
        app.SaveText("Documents/Bolig/scan_0042.txt", content, modified);

        await app.StartAsync();
        await app.WaitUntilIndexedAsync();
        await app.SearchUntilAsync("receipt for the apartment deposit",
            () => app.ResultNames.Contains("scan_0042.txt"));

        var window = app.LauncherWindow;
        await WaitForAsync(window, () => PreviewBlock(window, content) is not null, "the preview of scan_0042.txt");

        var pane = FindPane(window);
        Assert.NotNull(pane);
        var texts = PaneTexts(pane);

        // Its icon: a 48 by 48 square at the top.
        Assert.Contains(Visible(pane).OfType<Control>(),
            c => Math.Abs(c.Bounds.Width - 48) < 0.5 && Math.Abs(c.Bounds.Height - 48) < 0.5);

        // The name in bold.
        Assert.NotNull(NameBlock(pane, "scan_0042.txt"));

        // The folder and the meta line.
        Assert.Contains("~/Documents/Bolig", texts);
        Assert.Contains("TXT · 266 B · Modified Today 20:30", string.Concat(texts));

        // "Match" with a bar filled to the score, and the score.
        var matchIndex = texts.IndexOf("Match");
        Assert.True(matchIndex >= 0, "\"Match\" is shown");
        var scoreText = texts.Skip(matchIndex + 1).FirstOrDefault(t => Regex.IsMatch(t, @"^[01]\.\d\d$"));
        Assert.NotNull(scoreText);
        var score = double.Parse(scoreText, System.Globalization.CultureInfo.InvariantCulture);
        var bar = Visible(pane).OfType<Valafind.App.Controls.MeterBar>().SingleOrDefault();
        Assert.NotNull(bar);
        var fraction = bar.Value;
        Assert.InRange(fraction, score - 0.01, score + 0.01);

        // "Why it matched" over at most four lines of the excerpt, with highlights.
        var whyIndex = texts.IndexOf("Why it matched");
        var previewIndex = texts.IndexOf("Preview");
        Assert.True(whyIndex >= 0, "\"Why it matched\" is shown");
        Assert.True(previewIndex > whyIndex, "\"Preview\" is shown below \"Why it matched\"");
        var excerpt = Visible(pane).OfType<TextBlock>().FirstOrDefault(t =>
            t.Inlines is { } inlines && inlines.OfType<Run>().Any(r => r.Background is not null));
        Assert.NotNull(excerpt);
        var highlighted = excerpt.Inlines!.OfType<Run>().Where(r => r.Background is not null).ToList();
        Assert.Contains(highlighted, r => !string.IsNullOrWhiteSpace(r.Text) && r.FontWeight >= FontWeight.SemiBold);
        Assert.True(excerpt.TextLayout.TextLines.Count <= 4, "the excerpt takes at most four lines");

        // "Preview": the start of the file's text, as it is, on a panel filling the rest of the height.
        var preview = PreviewBlock(window, content);
        Assert.NotNull(preview);
        var panel = preview.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Background is not null);
        Assert.NotNull(panel);

        // At the bottom, the three buttons in one row.
        var open = ButtonsNamed(window, "Open").Single();
        var reveal = ButtonsNamed(window, "Show in Files").Single();
        var copy = ButtonsNamed(window, "Copy Path").Single();
        var openRect = InWindow(open, window);
        var revealRect = InWindow(reveal, window);
        var copyRect = InWindow(copy, window);
        Assert.InRange(revealRect.Top, openRect.Top - 1, openRect.Top + 1);
        Assert.InRange(copyRect.Top, openRect.Top - 1, openRect.Top + 1);
        Assert.True(openRect.Left < revealRect.Left && revealRect.Left < copyRect.Left, "Open, Show in Files, Copy Path from left to right");

        var panelRect = InWindow(panel, window);
        Assert.True(panelRect.Bottom <= openRect.Top + 0.5, "the preview panel is above the buttons");
        Assert.True(openRect.Top - panelRect.Bottom <= 32, "the preview panel fills the height down to the buttons");

        AppHarness.SaveScreen(window, "a-document-in-the-pane.png");
    }, new AppOptions { Language = "en" });

    [Fact(DisplayName = "code in the pane")]
    public Task CodeInThePane() => AppHarness.RunAsync(fixture, async app =>
    {
        var content = "def insertion_sort(values):\n" +
                      "    for i in range(1, len(values)):\n" +
                      "        current = values[i]\n" +
                      "        j = i - 1\n" +
                      "        while j >= 0 and values[j] > current:\n" +
                      "            values[j + 1] = values[j]\n" +
                      "            j -= 1\n" +
                      "        values[j + 1] = current\n" +
                      "    return values";
        app.SaveText("Projects/sort.py", content, DateTime.Now.AddHours(-1));

        await app.StartAsync();
        await app.WaitUntilIndexedAsync();
        await app.SearchUntilAsync("sort a list of numbers", () => app.ResultNames.Contains("sort.py"));

        var window = app.LauncherWindow;
        await WaitForAsync(window, () => PreviewBlock(window, content) is not null, "the preview of sort.py");

        var pane = FindPane(window);
        Assert.NotNull(pane);
        Assert.NotNull(NameBlock(pane, "sort.py"));

        var preview = PreviewBlock(window, content);
        Assert.NotNull(preview);
        // Indented as in the file.
        Assert.Contains("\n        current = values[i]\n", Normalize(TextOf(preview)));
        // In a fixed-width font.
        var family = preview.FontFamily.ToString();
        string[] monospace = ["mono", "consolas", "courier", "menlo", "monaco", "cascadia", "fixed"];
        Assert.True(monospace.Any(m => family.Contains(m, StringComparison.OrdinalIgnoreCase)),
            $"the preview font \"{family}\" is fixed-width");

        AppHarness.SaveScreen(window, "code-in-the-pane.png");
    }, new AppOptions { Language = "en" });

    [Fact(DisplayName = "a picture in the pane")]
    public Task APictureInThePane() => AppHarness.RunAsync(fixture, async app =>
    {
        app.SavePicture("Pictures/red_circle.png", Picture.RedCircle);

        await app.StartAsync();
        await app.WaitUntilIndexedAsync();
        await app.SearchUntilAsync("a red circle", () => app.ResultNames.Contains("red_circle.png"));

        var window = app.LauncherWindow;
        await WaitForAsync(window, () => FindPane(window) is { } p && NameBlock(p, "red_circle.png") is not null,
            "the pane showing red_circle.png");
        await WaitForAsync(window, () => LargestImage(FindPane(window)!) is { } i && Math.Max(i.Bounds.Width, i.Bounds.Height) >= 300,
            "the picture shown large");

        var pane = FindPane(window)!;
        var texts = PaneTexts(pane);
        Assert.DoesNotContain("Why it matched", texts);
        Assert.DoesNotContain("Preview", texts);

        var image = LargestImage(pane);
        Assert.NotNull(image);
        Assert.True(Math.Max(image.Bounds.Width, image.Bounds.Height) >= 300, "the picture is as large as fits");

        AppHarness.SaveScreen(window, "a-picture-in-the-pane.png");
    }, new AppOptions { Language = "en" });

    [Fact(DisplayName = "the pane follows the choice")]
    public Task ThePaneFollowsTheChoice() => AppHarness.RunAsync(fixture, async app =>
    {
        var contents = new Dictionary<string, string>
        {
            ["tomatoes.txt"] = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"Day {i}: watered the tomato plants in the garden.")),
            ["roses.txt"] = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"Week {i}: watered the rose bushes in the garden.")),
        };
        foreach (var (name, text) in contents)
            app.SaveText($"Garden/{name}", text, DateTime.Now.AddHours(-2));

        await app.StartAsync();
        await app.WaitUntilIndexedAsync();
        await app.SearchUntilAsync("notes about watering plants in the garden",
            () => app.ResultNames.Contains("tomatoes.txt") && app.ResultNames.Contains("roses.txt"));

        var window = app.LauncherWindow;
        var names = app.ResultNames.ToList();
        var first = names[0];
        var second = names[1];
        Assert.True(contents.ContainsKey(first) && contents.ContainsKey(second));

        await WaitForAsync(window, () => PreviewBlock(window, contents[first]) is not null, $"the preview of {first}");
        var firstScroller = PreviewBlock(window, contents[first])!.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        Assert.NotNull(firstScroller);
        firstScroller.Offset = new Vector(0, Math.Max(0, firstScroller.Extent.Height - firstScroller.Viewport.Height));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.True(firstScroller.Offset.Y > 0, "the first preview was scrolled down");

        await app.PressAsync(Key.Down, RawInputModifiers.None);

        await WaitForAsync(window, () => PreviewBlock(window, contents[second]) is not null, $"the preview of {second}");
        var pane = FindPane(window);
        Assert.NotNull(pane);
        Assert.NotNull(NameBlock(pane, second));
        Assert.Null(NameBlock(pane, first));

        var scroller = PreviewBlock(window, contents[second])!.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        Assert.NotNull(scroller);
        Assert.Equal(0, scroller.Offset.Y, 1);

        AppHarness.SaveScreen(window, "the-pane-follows-the-choice.png");
    }, new AppOptions { Language = "en" });

    [Fact(DisplayName = "no pane without results")]
    public Task NoPaneWithoutResults() => AppHarness.RunAsync(fixture, async app =>
    {
        await app.StartAsync();
        await app.WaitUntilIndexedAsync();

        var window = app.LauncherWindow;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        // The hint is shown.
        AssertNoPane(window);

        await app.SearchAsync(AppHarness.Unique());
        await WaitForAsync(window, () => AppHarness.Texts(window).Contains("No matching files"), "\"No matching files\"");

        AssertNoPane(window);

        AppHarness.SaveScreen(window, "no-pane-without-results.png");
    }, new AppOptions { Language = "en" });

    private static void AssertNoPane(Window window)
    {
        Assert.Empty(ButtonsNamed(window, "Open"));
        Assert.Empty(ButtonsNamed(window, "Show in Files"));
        Assert.Empty(ButtonsNamed(window, "Copy Path"));
        var texts = AppHarness.Texts(window).ToList();
        Assert.DoesNotContain("Why it matched", texts);
        Assert.DoesNotContain("Preview", texts);
        Assert.DoesNotContain("Match", texts);
        foreach (var list in AppHarness.Find<ListBox>(window))
            Assert.True(list.Bounds.Width > window.Bounds.Width - PaneWidth,
                "the list fills the width");
    }

    private static async Task WaitForAsync(Window window, Func<bool> condition, string what, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            if (condition()) return;
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for {what}.");
            await Task.Delay(25);
        }
    }

    private static IEnumerable<Visual> Visible(Control root) =>
        root.GetVisualDescendants().Where(v => v.IsEffectivelyVisible);

    private static string TextOf(TextBlock block) =>
        block.Text ?? (block.Inlines is { } inlines
            ? string.Concat(inlines.Select(i => i is Run r ? r.Text : i is LineBreak ? "\n" : ""))
            : "");

    private static string Normalize(string text) => text.Replace("\u200B", "").ReplaceLineEndings("\n");

    private static List<string> PaneTexts(Control pane) =>
        Visible(pane).OfType<TextBlock>().Select(t => Normalize(TextOf(t)).Trim()).Where(t => t.Length > 0).ToList();

    private static string ButtonLabel(Button button)
    {
        var name = AutomationProperties.GetName(button);
        if (!string.IsNullOrEmpty(name)) return name;
        if (button.Content is string s) return s;
        return string.Join(" ", button.GetVisualDescendants().OfType<TextBlock>().Select(TextOf).Where(t => t.Length > 0));
    }

    private static List<Button> ButtonsNamed(Window window, string name) =>
        AppHarness.Find<Button>(window).Where(b => ButtonLabel(b) == name).ToList();

    private static Control? FindPane(Window window)
    {
        var open = ButtonsNamed(window, "Open").FirstOrDefault();
        return open?.GetVisualAncestors().OfType<Control>()
            .FirstOrDefault(c => Math.Abs(c.Bounds.Width - PaneWidth) < 1.5);
    }

    private static TextBlock? NameBlock(Control pane, string fileName) =>
        Visible(pane).OfType<TextBlock>().FirstOrDefault(t =>
            Normalize(TextOf(t)).Trim() == fileName && t.FontWeight >= FontWeight.Bold);

    private static TextBlock? PreviewBlock(Window window, string expected)
    {
        var pane = FindPane(window);
        if (pane is null) return null;
        var want = Normalize(expected);
        return Visible(pane).OfType<TextBlock>().FirstOrDefault(t => Normalize(TextOf(t)) == want);
    }

    private static Image? LargestImage(Control pane) =>
        Visible(pane).OfType<Image>().Where(i => i.Source is not null)
            .OrderByDescending(i => i.Bounds.Width * i.Bounds.Height).FirstOrDefault();

    private static Rect InWindow(Control control, Window window)
    {
        var origin = control.TranslatePoint(new Point(0, 0), window);
        Assert.NotNull(origin);
        return new Rect(origin.Value, control.Bounds.Size);
    }
}
