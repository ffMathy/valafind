// ail-derived-from: NarrowResults@3e03f81817a9d529
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Input;
using Valafind.App.Tests.Headless;
using Xunit;

namespace Valafind.App.Tests.Ail;

[Collection(HeadlessUiDefinition.Name)]
public sealed class NarrowResultsTests(HeadlessFixture fixture)
{
    [Fact(DisplayName = "a kind chip keeps only files of that kind")]
    public Task AKindChipKeepsOnlyFilesOfThatKind() => AppHarness.RunAsync(fixture, async app =>
    {
        app.SaveText("pears.txt", "Notes about pears: ripe pears, pear trees and how to store pears over winter.", DateTime.Now);
        app.SaveText("pears.py", "# Counts the pears picked from each pear tree\npears = {\"conference\": 12, \"williams\": 8}\nprint(sum(pears.values()), \"pears\")\n", DateTime.Now);
        await app.StartAsync();
        await app.WaitUntilIndexedAsync();

        await app.SearchUntilAsync("pears", () =>
            app.ResultNames.Contains("pears.txt") && app.ResultNames.Contains("pears.py"));

        PickFilter(app.Launcher, "Code");
        await app.SearchUntilAsync("pears", () =>
            app.ResultNames.Contains("pears.py") && !app.ResultNames.Contains("pears.txt"));

        Assert.Equal("Code", LabelOf(app.Launcher.SelectedFilter));
        Assert.Contains("pears.py", app.ResultNames);
        Assert.DoesNotContain("pears.txt", app.ResultNames);

        PickFilter(app.Launcher, "All");
        await app.SearchUntilAsync("pears", () =>
            app.ResultNames.Contains("pears.txt") && app.ResultNames.Contains("pears.py"));

        Assert.Equal("All", LabelOf(app.Launcher.SelectedFilter));
        Assert.Contains("pears.py", app.ResultNames);
        Assert.Contains("pears.txt", app.ResultNames);

        AppHarness.SaveScreen(app.LauncherWindow, "a-kind-chip-keeps-only-files-of-that-kind.png");
    });

    [Fact(DisplayName = "a kind with no files shows No matching files")]
    public Task AKindWithNoFilesShowsNoMatchingFiles() => AppHarness.RunAsync(fixture, async app =>
    {
        app.SaveText("budget-notes.txt", "Notes on the household budget for the coming year.", DateTime.Now);
        await app.StartAsync();
        await app.WaitUntilIndexedAsync();

        await app.SearchAsync("budget");
        PickFilter(app.Launcher, "Spreadsheets");
        await app.SearchUntilAsync("budget", () =>
            app.ResultNames.Count() == 0
            && AppHarness.Texts(app.LauncherWindow).Any(t => t == "No matching files"));

        Assert.Equal("Spreadsheets", LabelOf(app.Launcher.SelectedFilter));
        Assert.Empty(app.ResultNames);
        Assert.Contains("No matching files", AppHarness.Texts(app.LauncherWindow));
        var summary = $"{app.Launcher.ResultSummary}";
        Assert.Matches(new Regex(@"^0 results · \d+ ms$"), summary);

        AppHarness.SaveScreen(app.LauncherWindow, "a-kind-with-no-files-shows-no-matching-files.png");
    });

    [Fact(DisplayName = "the time menu keeps only recent files")]
    public Task TheTimeMenuKeepsOnlyRecentFiles() => AppHarness.RunAsync(fixture, async app =>
    {
        app.SaveText("old-notes.txt", "Notes about plums: plum jam, plum trees and when plums ripen.", new DateTime(2020, 3, 3, 12, 0, 0, DateTimeKind.Local));
        app.SaveText("new-notes.txt", "More notes about plums: a plum cake recipe and drying plums.", DateTime.Now);
        await app.StartAsync();
        await app.WaitUntilIndexedAsync();

        await app.SearchUntilAsync("plums", () =>
            app.ResultNames.Contains("old-notes.txt") && app.ResultNames.Contains("new-notes.txt"));

        PickTimeRange(app.Launcher, "Past week");
        await app.SearchUntilAsync("plums", () =>
            app.ResultNames.Contains("new-notes.txt") && !app.ResultNames.Contains("old-notes.txt"));

        Assert.Equal("Past week", LabelOf(app.Launcher.SelectedTimeRange));
        Assert.Contains("Past week", AppHarness.Texts(app.LauncherWindow));
        Assert.Contains("new-notes.txt", app.ResultNames);
        Assert.DoesNotContain("old-notes.txt", app.ResultNames);

        PickTimeRange(app.Launcher, "Any time");
        await app.SearchUntilAsync("plums", () =>
            app.ResultNames.Contains("old-notes.txt")
            && AppHarness.Texts(app.LauncherWindow).Any(t => t.Contains("3 Mar 2020")));

        Assert.Equal("Any time", LabelOf(app.Launcher.SelectedTimeRange));
        Assert.Contains("old-notes.txt", app.ResultNames);
        Assert.Contains(AppHarness.Texts(app.LauncherWindow), t => t.Contains("3 Mar 2020"));

        AppHarness.SaveScreen(app.LauncherWindow, "the-time-menu-keeps-only-recent-files.png");
    }, new AppOptions { Language = "en" });

    [Fact(DisplayName = "the filters are kept until the user changes them")]
    public Task TheFiltersAreKeptUntilTheUserChangesThem() => AppHarness.RunAsync(fixture, async app =>
    {
        app.SaveText("kiwis.txt", "Notes about kiwis: how kiwis grow and when to pick kiwis.", DateTime.Now);
        app.SaveText("kiwis.py", "# Weighs the kiwis harvested each day\nkiwis = [92, 88, 101]\nprint(sum(kiwis), \"grams of kiwis\")\n", DateTime.Now);
        await app.StartAsync();
        await app.WaitUntilIndexedAsync();

        await app.SearchUntilAsync("kiwis", () =>
            app.ResultNames.Contains("kiwis.txt") && app.ResultNames.Contains("kiwis.py"));
        PickFilter(app.Launcher, "Code");
        await app.SearchUntilAsync("kiwis", () =>
            app.ResultNames.Contains("kiwis.py") && !app.ResultNames.Contains("kiwis.txt"));

        await app.PressAsync(Key.Escape, RawInputModifiers.None);

        app.ShowLauncher();
        await app.SearchUntilAsync("kiwis", () => app.ResultNames.Contains("kiwis.py"));

        Assert.Equal("Code", LabelOf(app.Launcher.SelectedFilter));
        Assert.Contains("kiwis.py", app.ResultNames);
        Assert.DoesNotContain("kiwis.txt", app.ResultNames);

        AppHarness.SaveScreen(app.LauncherWindow, "the-filters-are-kept-until-the-user-changes-them.png");
    });

    private static void PickFilter(object launcher, string label)
    {
        var chip = FindByLabel(launcher, "Filters", label);
        if (!TryExecuteSelect(chip))
            Pick(launcher, chip, "SelectFilter", "SelectedFilter");
    }

    private static void PickTimeRange(object launcher, string label)
    {
        var range = FindByLabel(launcher, "TimeRanges", label);
        if (!TryExecuteSelect(range))
            Pick(launcher, range, "SelectTimeRange", "SelectedTimeRange");
    }

    private static object FindByLabel(object launcher, string listName, string label)
    {
        var list = launcher.GetType().GetProperty(listName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(launcher) as IEnumerable
            ?? throw new InvalidOperationException($"The launcher has no {listName}.");
        foreach (var item in list)
        {
            if (item is not null && LabelOf(item) == label)
                return item;
        }
        throw new InvalidOperationException($"No \"{label}\" in the launcher's {listName}.");
    }

    private static bool TryExecuteSelect(object item)
    {
        if (item.GetType().GetProperty("SelectCommand", BindingFlags.Public | BindingFlags.Instance)?.GetValue(item) is ICommand command)
        {
            command.Execute(null);
            return true;
        }
        return false;
    }

    private static void Pick(object launcher, object item, string methodName, string propertyName)
    {
        var type = launcher.GetType();
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, [item.GetType()]);
        if (method is not null)
        {
            method.Invoke(launcher, [item]);
            return;
        }
        var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property is { CanWrite: true })
        {
            property.SetValue(launcher, item);
            return;
        }
        throw new InvalidOperationException($"The launcher offers no way to pick {LabelOf(item)}.");
    }

    private static string? LabelOf(object? item)
    {
        if (item is null)
            return null;
        foreach (var name in new[] { "Label", "Name", "Text", "Title" })
        {
            if (item.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(item) is string text)
                return text;
        }
        return item.ToString();
    }
}
