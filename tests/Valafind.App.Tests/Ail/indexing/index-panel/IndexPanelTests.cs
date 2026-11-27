// ail-derived-from: IndexPanel@2f5bf57bb6476877
using System.Text.RegularExpressions;
using Valafind.App.Tests.Headless;
using Xunit;

namespace Valafind.App.Tests.Ail;

[Collection(HeadlessUiDefinition.Name)]
public sealed class IndexPanelTests(HeadlessFixture fixture)
{
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(3);

    private static readonly Regex Percentage = new(@"^\d{1,3}%$");

    private static readonly Regex PausedCaption = new(@"^\d+ of \d+\+? · paused$");

    [Fact(DisplayName = "the panel shows while new files are indexed")]
    public Task ThePanelShowsWhileNewFilesAreIndexed() => AppHarness.RunAsync(fixture, async app =>
    {
        // Given Valafind was up to date ...
        app.SaveText("Notes/first.txt", $"A first note {AppHarness.Unique()}", DateTime.Now.AddDays(-30));
        await app.StartAsync();
        await app.WaitUntilIndexedAsync();

        // ... and the user saved 25 new notes in the home folder
        for (var i = 0; i < 25; i++)
        {
            app.SaveText($"Notes/note-{i:00}.txt",
                $"Note number {i} about the garden, the weather and the week ahead {AppHarness.Unique()}",
                DateTime.Now.AddDays(-20));
        }

        // When the user opens the launcher with nothing typed
        app.ShowLauncher();

        // Then the "Index" panel shows, with a documents count in its bottom row
        var seen = await WaitForAsync(
            () => Snapshot(app) is var texts
                  && texts.Contains("Indexing in the background")
                  && DocumentsCount(texts) is not null,
            Patience);
        Assert.True(seen, "The index panel never showed while the new notes were indexed.");

        var shown = Snapshot(app);
        Assert.Contains("Index", shown);
        Assert.Contains("Phase 1 · Frequently used", shown);
        Assert.Contains("Phase 2 · Home folder", shown);
        Assert.Contains(shown, t => Percentage.IsMatch(t));
        Assert.Contains("Indexing in the background", shown);
        Assert.Contains("CPU", shown);
        Assert.Contains("Disk", shown);
        // Pictured now, while the panel shows: indexing 25 notes can be done before the counting below ends.
        AppHarness.SaveScreen(app.LauncherWindow, "the-panel-shows-while-new-files-are-indexed.png");

        // And the row at the bottom counts the indexed files by type, growing as files are done
        var counts = new List<int> { DocumentsCount(shown)!.Value };
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline && !app.Launcher.Status.IsUpToDate)
        {
            var count = DocumentsCount(Snapshot(app));
            if (count is { } n)
            {
                counts.Add(n);
                if (n > counts[0])
                {
                    break;
                }
            }
            await Task.Delay(25);
        }

        for (var i = 1; i < counts.Count; i++)
        {
            Assert.True(counts[i] >= counts[i - 1],
                $"The Documents count went down: {string.Join(", ", counts)}.");
        }
        Assert.All(counts, n => Assert.InRange(n, 1, 26));

    });

    [Fact(DisplayName = "the panel gives way to \"Up to date\"")]
    public Task ThePanelGivesWayToUpToDate() => AppHarness.RunAsync(fixture, async app =>
    {
        // Given the panel shows while files are indexed
        for (var i = 0; i < 25; i++)
        {
            app.SaveText($"Notes/note-{i:00}.txt",
                $"Note number {i} about a trip to the coast {AppHarness.Unique()}",
                DateTime.Now.AddDays(-20));
        }
        await app.StartAsync();
        var shows = await WaitForAsync(
            () => Snapshot(app).Contains("Phase 1 · Frequently used"),
            Patience);
        Assert.True(shows, "The index panel never showed while the files were indexed.");

        // When the last file is done
        await app.WaitUntilIndexedAsync();
        await WaitForAsync(() => !Snapshot(app).Contains("Phase 1 · Frequently used"), TimeSpan.FromSeconds(10));

        // Then the panel is gone and the footer reads "Up to date"
        var texts = Snapshot(app);
        Assert.DoesNotContain("Phase 1 · Frequently used", texts);
        Assert.DoesNotContain("Phase 2 · Home folder", texts);
        Assert.DoesNotContain("Indexing in the background", texts);
        Assert.Contains("Up to date", texts);

        AppHarness.SaveScreen(app.LauncherWindow, "the-panel-gives-way-to-up-to-date.png");
    });

    [Fact(DisplayName = "pausing shows in the panel")]
    public Task PausingShowsInThePanel() => AppHarness.RunAsync(fixture, async app =>
    {
        for (var i = 0; i < 25; i++)
        {
            app.SaveText($"Notes/note-{i:00}.txt",
                $"Note number {i} about the quarterly budget {AppHarness.Unique()}",
                DateTime.Now.AddDays(-20));
        }
        await app.StartAsync();

        // When the user chooses "Pause indexing" in the launcher's menu
        await app.SetPausedAsync(true);

        // Then the state reads "Paused by you" and Phase 2's caption ends "· paused"
        var paused = await WaitForAsync(
            () => Snapshot(app) is var texts
                  && texts.Contains("Paused by you")
                  && texts.Any(t => PausedCaption.IsMatch(t)),
            TimeSpan.FromSeconds(30));

        var shown = Snapshot(app);
        Assert.True(paused, $"The panel did not show the pause. It showed: {string.Join(" | ", shown)}");
        Assert.Contains("Paused by you", shown);
        Assert.Contains(shown, t => PausedCaption.IsMatch(t));

        AppHarness.SaveScreen(app.LauncherWindow, "pausing-shows-in-the-panel.png");
    });

    private static List<string> Snapshot(dynamic app) =>
        ((IEnumerable<string>)AppHarness.Texts(app.LauncherWindow)).ToList();

    private static int? DocumentsCount(IReadOnlyList<string> texts)
    {
        for (var i = 0; i + 1 < texts.Count; i++)
        {
            if (texts[i] == "Documents" && int.TryParse(texts[i + 1].Trim(), out var n))
            {
                return n;
            }
        }
        return null;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(25);
        }
        return condition();
    }
}
