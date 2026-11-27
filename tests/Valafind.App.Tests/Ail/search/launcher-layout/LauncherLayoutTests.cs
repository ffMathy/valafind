// ail-derived-from: LauncherLayout@00fb318ca9d0d98b
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Valafind.App.Tests.Headless;
using Xunit;

namespace Valafind.App.Tests.Ail;

[Collection(HeadlessUiDefinition.Name)]
public sealed class LauncherLayoutTests(HeadlessFixture fixture)
{
    private const string Placeholder = "Describe what you are looking for";
    private const string Heading = "Best matches by meaning";
    private const string HintTitle = "Search your files by meaning";
    private const string NoMatches = "No matching files";
    private const string Incomplete = "Indexing isn\u2019t finished yet, so results may be incomplete";
    private const string FooterKeys = "↑↓ navigate ↵ open Ctrl+↵ show in Files Tab preview";

    private static readonly Regex NoticePattern =
        new(Regex.Escape(Incomplete) + @"\s*·\s*\d+%\s*of your files are searchable");

    private static readonly string[] ChipNames =
        ["All", "Documents", "PDFs", "Spreadsheets", "Slides", "Mail", "Code", "Images"];

    [Fact(DisplayName = "the launcher at rest")]
    public Task TheLauncherAtRest() => AppHarness.RunAsync(fixture, async app =>
    {
        app.SaveText("Notes/pears.txt", "Pears ripen best off the tree, in a cool room.");
        await app.StartAsync();
        await app.WaitUntilIndexedAsync();
        var window = app.LauncherWindow;

        await Until(() => Texts(window).Contains(HintTitle) && Texts(window).Contains("Up to date"),
            "the launcher at rest to show the hint and the quiet up-to-date footer");

        var texts = Texts(window);
        var placeholder = texts.IndexOf(Placeholder);
        var shortcut = texts.IndexOf("Ctrl+Alt+Space");
        var heading = texts.IndexOf(Heading);
        var hint = texts.IndexOf(HintTitle);
        Assert.True(placeholder >= 0, "the search box shows its placeholder");
        Assert.True(shortcut > placeholder, "the shortcut sits at the search box's right end");
        Assert.True(heading > shortcut, "the heading is under the header");
        Assert.True(hint > heading, "\"Best matches by meaning\" heads the hint");

        var previous = hint;
        foreach (var chip in ChipNames)
        {
            var at = IndexAfter(texts, chip, previous);
            Assert.True(at > previous, $"the chip \"{chip}\" follows in the chips row");
            previous = at;
        }
        var anyTime = IndexAfter(texts, "Any time", previous);
        Assert.True(anyTime > previous, "\"Any time\" is at the right of the chips");
        Assert.Same(Chip(app.Launcher.Filters, "All"), app.Launcher.SelectedFilter);

        // A green tick and "Up to date" at the left; the key hints at the right.
        Assert.Contains("Up to date", texts);
        Assert.Contains(FooterKeys, Joined(texts));
        var upToDate = LeftOf(window, "Up to date");
        Assert.True(LeftOf(window, "↑↓ navigate") > upToDate, "the key hints are at the right of \"Up to date\"");

        AppHarness.SaveScreen(window, "the-launcher-at-rest.png");
    });

    [Fact(DisplayName = "while indexing, the index panel takes the place of \"Up to date\"")]
    public Task WhileIndexingTheIndexPanelTakesThePlaceOfUpToDate() => AppHarness.RunAsync(fixture, async app =>
    {
        SaveManyNotes(app, 800);
        await app.StartAsync();
        var window = app.LauncherWindow;

        await Until(() => !app.Launcher.Status.IsUpToDate && !app.Launcher.Status.IsPaused
                          && Texts(window).Contains(app.Launcher.Status.StateText),
            "the index panel to show while files are indexed");

        Assert.False(app.Launcher.Status.IsUpToDate);
        var texts = Texts(window);
        var state = app.Launcher.Status.StateText;
        var heading = texts.IndexOf(Heading);
        var panel = IndexAfter(texts, state, heading);
        var chips = IndexAfter(texts, "All", panel);
        Assert.True(heading >= 0, "the list's heading is shown");
        Assert.True(panel > heading, "the index panel shows below the list");
        Assert.True(chips > panel, "the index panel shows above the chips");
        Assert.DoesNotContain("Up to date", Joined(texts));

        AppHarness.SaveScreen(window, "while-indexing-the-index-panel-takes-the-place-of-up-to-date.png");
    });

    [Fact(DisplayName = "while indexing, a yellow notice under the search box says the results may be incomplete")]
    public Task WhileIndexingAYellowNoticeUnderTheSearchBoxSaysTheResultsMayBeIncomplete() => AppHarness.RunAsync(fixture, async app =>
    {
        SaveManyNotes(app, 800);
        await app.StartAsync();
        var window = app.LauncherWindow;

        await app.SearchUntilAsync("pears", () => app.ResultNames.Count > 0 && NoticeIndex(Texts(window)) >= 0);
        Assert.False(app.Launcher.Status.IsUpToDate);
        var texts = Texts(window);
        Assert.Matches(NoticePattern, Joined(texts));
        var notice = NoticeIndex(texts);
        var shortcut = texts.IndexOf("Ctrl+Alt+Space");
        var heading = texts.IndexOf(Heading);
        Assert.True(notice > shortcut, "the notice is under the header");
        Assert.True(heading > notice, "the notice is right under the header, above the list");

        await app.SearchAsync("");
        await Until(() => Texts(window).Contains(HintTitle), "the hint for a blank question");
        Assert.False(app.Launcher.Status.IsUpToDate);
        Assert.True(NoticeIndex(Texts(window)) < 0, "no notice with the hint");

        Chip(app.Launcher.Filters, "Spreadsheets").SelectCommand.Execute(null);
        await app.SearchUntilAsync("pears", () => Texts(window).Contains(NoMatches) && NoticeIndex(Texts(window)) >= 0);
        Assert.False(app.Launcher.Status.IsUpToDate);
        texts = Texts(window);
        Assert.Matches(NoticePattern, Joined(texts));
        Assert.True(NoticeIndex(texts) < texts.IndexOf(NoMatches), "the notice shows above \"No matching files\"");

        AppHarness.SaveScreen(window, "while-indexing-a-yellow-notice-under-the-search-box-says-the.png");
    });

    [Fact(DisplayName = "the notice goes once every file is searchable")]
    public Task TheNoticeGoesOnceEveryFileIsSearchable() => AppHarness.RunAsync(fixture, async app =>
    {
        SaveManyNotes(app, 300);
        await app.StartAsync();
        var window = app.LauncherWindow;

        await app.SearchUntilAsync("pears", () => app.ResultNames.Count > 0 && NoticeIndex(Texts(window)) >= 0);
        Assert.True(NoticeIndex(Texts(window)) >= 0, "the notice is shown");

        await app.WaitUntilIndexedAsync();
        await Until(() => NoticeIndex(Texts(window)) < 0 && Texts(window).Contains("Up to date"),
            "the notice to go once indexing finishes");

        var texts = Texts(window);
        Assert.True(NoticeIndex(texts) < 0, "the notice is gone");
        Assert.Contains("Up to date", texts);

        AppHarness.SaveScreen(window, "the-notice-goes-once-every-file-is-searchable.png");
    });

    [Fact(DisplayName = "while paused, the paused banner takes the notice's place")]
    public Task WhilePausedThePausedBannerTakesTheNoticesPlace() => AppHarness.RunAsync(fixture, async app =>
    {
        SaveManyNotes(app, 800);
        await app.StartAsync();
        var window = app.LauncherWindow;

        await app.SearchUntilAsync("pears", () => app.ResultNames.Count > 0);
        Assert.False(app.Launcher.Status.IsUpToDate);
        await app.SetPausedAsync(true);

        await app.SearchAsync("orchard");
        await Until(() => app.Launcher.Status.IsPaused && Texts(window).Contains(app.Launcher.Status.StateText),
            "the paused banner to show", window);

        Assert.True(app.Launcher.Status.IsPaused);
        Assert.False(app.Launcher.Status.IsUpToDate);
        var texts = Texts(window);
        Assert.Contains(app.Launcher.Status.StateText, texts);
        Assert.Matches(new Regex(@"\d+\s*%|\d+\s*(of|/)\s*\d+"), Joined(texts));
        Assert.True(NoticeIndex(texts) < 0, "the yellow notice does not show while paused");

        AppHarness.SaveScreen(window, "while-paused-the-paused-banner-takes-the-notice-s-place.png");
    });

    [Fact(DisplayName = "a tip on \"Up to date\" says how many files can be found")]
    public Task ATipOnUpToDateSaysHowManyFilesCanBeFound() => AppHarness.RunAsync(fixture, async app =>
    {
        app.SaveDocument("Documents/lease.docx", "docx", ["The lease for the flat, with the deposit we negotiated."]);
        app.SaveDocument("Documents/budget-notes.docx", "docx", ["Notes on the travel budget for the summer."]);
        app.SaveText("Code/sort.py", "def sort(items):\n    return sorted(items)\n");
        await app.StartAsync();
        await app.WaitUntilIndexedAsync();
        var window = app.LauncherWindow;

        await Until(() => FindTipHolder(window) is not null, "a tip on \"Up to date\"");
        var holder = FindTipHolder(window)!;
        ToolTip.SetIsOpen(holder, true);

        await Until(() => Regex.IsMatch(Joined(TipTexts(ToolTip.GetTip(holder))), @"Index up to date · \d+ files searchable"),
            "the tip's text");
        var tip = Joined(TipTexts(ToolTip.GetTip(holder)));
        Assert.Contains("Index up to date · 3 files searchable", tip);
        Assert.Contains("Documents 2", tip);
        Assert.Contains("Code 1", tip);
        Assert.True(tip.IndexOf("Documents 2", StringComparison.Ordinal) > tip.IndexOf("files searchable", StringComparison.Ordinal),
            "the files of each kind are under the count");

        AppHarness.SaveScreen(window, "a-tip-on-up-to-date-says-how-many-files-can-be-found.png");
    });

    private static void SaveManyNotes(dynamic app, int count)
    {
        for (var i = 0; i < count; i++)
            app.SaveText($"Notes/batch-{i / 100}/note-{i}.txt",
                $"Note {i} about pears and the orchard: entry {i}, picked on row {i % 17}, stored in crate {i % 23}.");
    }

    private static List<string> Texts(Window window) =>
        AppHarness.Texts(window).Where(t => t is not null).Select(t => t.Trim()).ToList();

    private static string Joined(IEnumerable<string> texts) =>
        Regex.Replace(string.Join(" ", texts), @"\s+", " ").Trim();

    private static int IndexAfter(List<string> texts, string text, int after)
    {
        for (var i = Math.Max(after + 1, 0); i < texts.Count; i++)
            if (texts[i] == text)
                return i;
        return -1;
    }

    private static int NoticeIndex(List<string> texts) =>
        texts.FindIndex(t => t.Contains(Incomplete, StringComparison.Ordinal));

    private static T Chip<T>(IEnumerable<T> chips, string label) =>
        chips.Single(c => c is not null && c.GetType().GetProperties()
            .Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0)
            .Any(p => string.Equals((string?)p.GetValue(c), label, StringComparison.Ordinal)));

    private static Control? FindTipHolder(Window window)
    {
        foreach (var label in AppHarness.Find<TextBlock>(window)
                     .Where(t => (t.Text ?? t.Inlines?.Text ?? "").Contains("Up to date", StringComparison.Ordinal)))
        {
            for (Control? c = label; c is not null && c is not Window; c = c.GetVisualParent() as Control)
                if (ToolTip.GetTip(c) is not null)
                    return c;
        }
        return null;
    }

    private static IEnumerable<string> TipTexts(object? tip)
    {
        switch (tip)
        {
            case null:
                yield break;
            case string s:
                yield return s;
                yield break;
            case Control control:
                var blocks = new List<TextBlock>();
                if (control is TextBlock self)
                    blocks.Add(self);
                blocks.AddRange(control.GetLogicalDescendants().OfType<TextBlock>());
                foreach (var block in blocks)
                {
                    var text = block.Text ?? block.Inlines?.Text;
                    if (!string.IsNullOrWhiteSpace(text))
                        yield return text;
                }
                if (control is ContentControl { Content: string content })
                    yield return content;
                yield break;
            default:
                yield return tip.ToString() ?? "";
                yield break;
        }
    }

    // Where the text starts, from the window's left edge.
    private static double LeftOf(Window window, string text)
    {
        var block = AppHarness.Find<TextBlock>(window)
            .FirstOrDefault(t => t.IsEffectivelyVisible && (t.Text ?? t.Inlines?.Text ?? "").Trim() == text)
            ?? throw new InvalidOperationException($"No text shows \"{text}\"");
        return Avalonia.VisualExtensions.TranslatePoint(block, new Avalonia.Point(0, 0), window)?.X
            ?? throw new InvalidOperationException($"\"{text}\" is not laid out");
    }

    private static async Task Until(Func<bool> condition, string what, Window? window = null, int seconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Timed out waiting for {what}." + (window is null ? "" : $" It shows: {string.Join(" | ", Texts(window))}"));
            await Task.Delay(50);
        }
    }
}
