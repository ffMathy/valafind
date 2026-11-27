// ail-derived-from: ResultRow@41d01da04459338d
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Valafind.App.Tests.Headless;
using Xunit;

namespace Valafind.App.Tests.Ail;

[Collection(HeadlessUiDefinition.Name)]
public sealed class ResultRowTests(HeadlessFixture fixture)
{
    private static string Plain(string? text) => (text ?? string.Empty).Replace("\u200B", string.Empty);

    [Fact(DisplayName = "a document's row")]
    public Task ADocumentsRow() => AppHarness.RunAsync(fixture, async app =>
    {
        var code = AppHarness.Unique();
        var savedAt = DateTime.Today.AddHours(20).AddMinutes(30);
        app.SaveText("Documents/Bolig/scan_0042.txt",
            $"Lejekontrakt {code} for lejligheden i Bolig. Huslejen betales den første i hver måned, og depositum svarer til tre måneders husleje. " +
            "Opsigelse sker skriftligt med tre måneders varsel til udgangen af en måned.",
            savedAt);
        await app.StartAsync();
        await app.WaitUntilIndexedAsync();
        await app.SearchUntilAsync(code, () => app.ResultNames.Contains("scan_0042.txt"));

        var row = app.Launcher.Results.First(r => r.FileName == "scan_0042.txt");
        Assert.Equal("~/Documents/Bolig · Today 20:30", row.Caption);
        Assert.False(string.IsNullOrWhiteSpace(row.Snippet));

        var texts = AppHarness.Texts(app.LauncherWindow).Select(Plain).ToList();
        Assert.Contains("~/Documents/Bolig · Today 20:30", texts);

        var blocks = AppHarness.Find<TextBlock>(app.LauncherWindow).ToList();
        var name = blocks.First(b => Plain(b.Text) == "scan_0042.txt");
        Assert.True(name.FontWeight >= FontWeight.Bold);

        var snippetStart = row.Snippet.Substring(0, Math.Min(20, row.Snippet.Length));
        var excerpt = blocks.First(b => Plain(b.Text).StartsWith(snippetStart, StringComparison.Ordinal)
            || Plain(string.Concat(b.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text) ?? Array.Empty<string?>())).StartsWith(snippetStart, StringComparison.Ordinal));
        Assert.Equal(TextTrimming.CharacterEllipsis, excerpt.TextTrimming);

        // The match comes from the real model; the row shows it with two decimals, in bold, over a bar of the same value.
        Assert.Equal(row.ScoreFraction.ToString("0.00", CultureInfo.InvariantCulture), row.ScoreText);
        var score = blocks.First(b => Plain(b.Text) == row.ScoreText);
        Assert.True(score.FontWeight >= FontWeight.Bold);
        Assert.InRange(row.ScoreFraction, 0.0, 1.0);

        AppHarness.SaveScreen(app.LauncherWindow, "a-document-s-row.png");
    });

    [Fact(DisplayName = "the dates")]
    public Task TheDates() => AppHarness.RunAsync(fixture, async app =>
    {
        var code = AppHarness.Unique();
        app.SaveText($"Notes/today_{code}.txt", $"Notat {code} skrevet i dag om indkøb.", DateTime.Today.AddHours(9).AddMinutes(12));
        app.SaveText($"Notes/yesterday_{code}.txt", $"Notat {code} skrevet i går om indkøb.", DateTime.Today.AddDays(-1).AddHours(12));
        app.SaveText($"Notes/old_{code}.txt", $"Notat {code} skrevet for længe siden om indkøb.", new DateTime(2020, 3, 3, 12, 0, 0, DateTimeKind.Local));
        await app.StartAsync();
        await app.WaitUntilIndexedAsync();
        await app.SearchUntilAsync(code, () =>
            app.ResultNames.Contains($"today_{code}.txt")
            && app.ResultNames.Contains($"yesterday_{code}.txt")
            && app.ResultNames.Contains($"old_{code}.txt"));

        string Caption(string file) => app.Launcher.Results.First(r => r.FileName == file).Caption;
        Assert.EndsWith(" · Today 09:12", Caption($"today_{code}.txt"));
        Assert.EndsWith(" · Yesterday", Caption($"yesterday_{code}.txt"));
        Assert.EndsWith(" · 3 Mar 2020", Caption($"old_{code}.txt"));

        var texts = AppHarness.Texts(app.LauncherWindow).Select(Plain).ToList();
        Assert.Contains(texts, t => t.EndsWith(" · Today 09:12", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.EndsWith(" · Yesterday", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.EndsWith(" · 3 Mar 2020", StringComparison.Ordinal));

        AppHarness.SaveScreen(app.LauncherWindow, "the-dates.png");
    });

    [Fact(DisplayName = "the chosen row")]
    public Task TheChosenRow() => AppHarness.RunAsync(fixture, async app =>
    {
        var code = AppHarness.Unique();
        app.SaveText($"Documents/first_{code}.txt", $"Referat {code} fra mødet om budgettet.", DateTime.Now.AddHours(-2));
        app.SaveText($"Documents/second_{code}.txt", $"Referat {code} fra mødet om planen.", DateTime.Now.AddHours(-3));
        await app.StartAsync();
        await app.WaitUntilIndexedAsync();
        await app.SearchUntilAsync(code, () => app.Launcher.Results.Count >= 2);

        await app.PressAsync(Avalonia.Input.Key.Down, Avalonia.Input.RawInputModifiers.None);
        var chosen = app.Launcher.SelectedResult;
        Assert.NotNull(chosen);
        Assert.True(chosen!.IsSelected);
        Assert.All(app.Launcher.Results.Where(r => !ReferenceEquals(r, chosen)), r => Assert.False(r.IsSelected));

        // The details pane beside the list shows the chosen file: its name appears once in the row and again in the pane.
        var texts = AppHarness.Texts(app.LauncherWindow).Select(Plain).ToList();
        Assert.True(texts.Count(t => t == chosen.FileName) >= 2);

        AppHarness.SaveScreen(app.LauncherWindow, "the-chosen-row.png");
    });

    [Fact(DisplayName = "a picture's row")]
    public Task APicturesRow() => AppHarness.RunAsync(fixture, async app =>
    {
        var code = AppHarness.Unique();
        var file = $"picture_{code}.png";
        app.SavePicture($"Pictures/{file}", Picture.RedCircle, code);
        await app.StartAsync();
        await app.WaitUntilIndexedAsync();
        await app.SearchUntilAsync(code, () => app.ResultNames.Contains(file));

        var row = app.Launcher.Results.First(r => r.FileName == file);
        Assert.NotNull(row.Thumbnail);
        Assert.True(string.IsNullOrEmpty(row.Snippet));
        Assert.Contains(AppHarness.Find<Image>(app.LauncherWindow), i => i.Source is not null);

        AppHarness.SaveScreen(app.LauncherWindow, "a-picture-s-row.png");
    });
}
