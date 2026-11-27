// ail-derived-from: LauncherMessages@a8e280d5d3ce5002
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Valafind.App.Tests.Headless;
using Xunit;

namespace Valafind.App.Tests.Ail;

[Collection(HeadlessUiDefinition.Name)]
public sealed class LauncherMessagesTests(HeadlessFixture fixture)
{
    private const string HintTitle = "Search your files by meaning";
    private const string HintSearching = "Searching…";
    private const string HintBody =
        "Describe what you are looking for in your own words — “the lease where we negotiated the deposit”. Everything runs on this computer.";
    private const string HintSummon = "Summon Valafind from anywhere with Ctrl+Alt+Space.";
    private const string NoResultsTitle = "No matching files";
    private const string NoResultsBody =
        "Try describing it differently, pick another filter, or wait for indexing to cover more of your files.";

    private static string WithoutWhitespace(string text) =>
        Regex.Replace(text, @"\s+", string.Empty);

    // The summon line is composed from parts (the shortcut is in bold), so it is
    // read from the whole text the window shows rather than from one element.
    private static bool ShowsComposed(IEnumerable<string> texts, string message) =>
        WithoutWhitespace(string.Concat(texts)).Contains(WithoutWhitespace(message), StringComparison.Ordinal);

    [Fact(DisplayName = "the hint")]
    public Task TheHint() => AppHarness.RunAsync(fixture, async app =>
    {
        await app.StartAsync();
        await app.WaitUntilIndexedAsync();

        var texts = AppHarness.Texts(app.LauncherWindow).ToList();

        Assert.Contains(HintTitle, texts);
        Assert.Contains(HintBody, texts);
        Assert.True(ShowsComposed(texts, HintSummon),
            $"Expected the launcher to show \"{HintSummon}\", but it shows: {string.Join(" | ", texts)}");

        AppHarness.SaveScreen(app.LauncherWindow, "the-hint.png");
    });

    [Fact(DisplayName = "nothing matched")]
    public Task NothingMatched() => AppHarness.RunAsync(fixture, async app =>
    {
        // No indexed file at all, so no indexed file passes the chosen filters.
        await app.StartAsync();
        await app.WaitUntilIndexedAsync();

        await app.SearchAsync("the lease where we negotiated the deposit");

        Assert.Empty(app.ResultNames);

        var texts = AppHarness.Texts(app.LauncherWindow).ToList();
        Assert.Contains(NoResultsTitle, texts);
        Assert.Contains(NoResultsBody, texts);

        var shown = Regex.Replace(string.Join(" ", texts), @"\s+", " ");
        Assert.Matches(new Regex(@"0\s*results\s*·\s*[\d.,]+\s*ms"), shown);

        AppHarness.SaveScreen(app.LauncherWindow, "nothing-matched.png");
    });

    [Fact(DisplayName = "a search on the way from the hint")]
    public Task ASearchOnTheWayFromTheHint() => AppHarness.RunAsync(fixture, async app =>
    {
        await app.StartAsync();
        await app.WaitUntilIndexedAsync();

        Assert.Contains(HintTitle, AppHarness.Texts(app.LauncherWindow).ToList());

        var search = app.SearchAsync("the lease where we negotiated the deposit");

        var sawSearching = AppHarness.Texts(app.LauncherWindow).Contains(HintSearching);
        while (!sawSearching && !search.IsCompleted)
        {
            await Task.Delay(1);
            if (search.IsCompleted)
            {
                break;
            }
            sawSearching = AppHarness.Texts(app.LauncherWindow).Contains(HintSearching);
        }

        Assert.True(sawSearching, "Expected the hint's title to read \"Searching…\" before the first search answered.");

        await search;

        Assert.DoesNotContain(HintSearching, AppHarness.Texts(app.LauncherWindow).ToList());

        AppHarness.SaveScreen(app.LauncherWindow, "a-search-on-the-way-from-the-hint.png");
    });
}
