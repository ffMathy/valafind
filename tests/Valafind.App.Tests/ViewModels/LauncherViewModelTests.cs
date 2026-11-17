using System.Collections.Concurrent;
using Valafind.App.ViewModels;
using Valafind.Core.Abstractions;
using Valafind.Core.Configuration;
using Xunit;

namespace Valafind.App.Tests.ViewModels;

public sealed class LauncherViewModelTests
{
    private const string LinuxAppDir = "/opt/Valafind";
    private const string WindowsAppDir = @"C:\Program Files\Valafind";

    private static LauncherViewModel Create(UiFlavor flavor = UiFlavor.Linux, ValafindSettings? settings = null) =>
        new(flavor, settings ?? new ValafindSettings { Hotkey = "Super+Space" }, TimeProvider.System, static a => a(), "/home/u",
            flavor == UiFlavor.Windows ? WindowsAppDir : LinuxAppDir);

    private static SearchResult Hit(string name, FileKind kind, float score, string snippet = "some passage") =>
        new("/home/u/Documents/" + name, name, "/home/u/Documents", kind, DateTimeOffset.UtcNow.AddDays(-10), 1234, score, snippet,
            [new TextSpan(0, 4)]);

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("Condition not met in time");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void StartsInModelLoadingState()
    {
        using var vm = Create();
        Assert.True(vm.IsModelLoading);
        Assert.Equal(LauncherContent.ModelLoading, vm.Content);
        Assert.Equal("Super+Space", vm.HotkeyLabel);
    }

    [Fact]
    public void ModelMissingShowsFetchInstructions()
    {
        using var vm = Create(UiFlavor.Windows);
        vm.SetModelMissing(@"D:\models\gemma", customTarget: true);
        Assert.True(vm.IsModelMissingVisible);
        Assert.Equal(@"D:\models\gemma", vm.ModelTargetDirectory);
        // The shipped script by absolute path: works from any terminal, whatever the current directory.
        Assert.Equal("powershell -ExecutionPolicy Bypass -File \"C:\\Program Files\\Valafind\\tools\\fetch-model.ps1\" \"D:\\models\\gemma\"",
            vm.FetchCommandText);
        Assert.EndsWith(" -Force", vm.RepairCommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelMissingOnUnixRunsTheShippedScriptThroughBash()
    {
        using var vm = Create(UiFlavor.Linux);
        vm.SetModelMissing("/home/u/.local/share/valafind/models/embeddinggemma-2");
        Assert.Equal("bash '/opt/Valafind/tools/fetch-model.sh'", vm.FetchCommandText);
        Assert.Equal("bash '/opt/Valafind/tools/fetch-model.sh' --force", vm.RepairCommandText);
        Assert.Equal("/home/u/.local/share/valafind/models/embeddinggemma-2", vm.ModelTargetDirectory);
    }

    [Fact]
    public void CheckAgainAsksTheHostToLookForTheModel()
    {
        var actions = new RecordingActions();
        using var vm = Create();
        vm.Actions = actions;
        vm.SetModelMissing("/m");
        vm.CheckModelCommand.Execute(null);
        Assert.Equal(["check-model"], actions.Log);
    }

    [Fact]
    public async Task DebouncesTypingAndSearchesOnlyTheLastQuery()
    {
        var search = new RecordingSearch(_ => [Hit("a.pdf", FileKind.Pdf, 0.9f)]);
        using var vm = Create();
        vm.AttachSearch(search);

        vm.Query = "l";
        vm.Query = "le";
        vm.Query = "lease";
        await WaitUntil(() => vm.Results.Count == 1);

        Assert.Equal(["lease"], search.Queries);
        Assert.Equal(LauncherContent.Results, vm.Content);
        Assert.Equal(0, vm.SelectedIndex);
        Assert.True(vm.Results[0].IsSelected);
        Assert.Matches(@"^1 result · \d+ ms$", vm.ResultSummary);
    }

    [Fact]
    public async Task NewKeystrokeCancelsTheSearchInFlight()
    {
        var gate = new TaskCompletionSource();
        CancellationToken firstToken = default;
        var calls = 0;
        var search = new RecordingSearch(async (query, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstToken = ct;
                await gate.Task.WaitAsync(ct);
            }
            return [Hit(query + ".md", FileKind.Text, 0.5f)];
        });
        using var vm = Create();
        vm.AttachSearch(search);

        vm.Query = "first";
        await WaitUntil(() => Volatile.Read(ref calls) == 1);
        vm.Query = "second";
        await WaitUntil(() => vm.Results.Count == 1);

        Assert.True(firstToken.IsCancellationRequested);
        Assert.Equal("second.md", vm.Results[0].FileName);
        gate.TrySetResult();
    }

    [Fact]
    public async Task ClearingTheQueryClearsResults()
    {
        using var vm = Create();
        vm.AttachSearch(new RecordingSearch(_ => [Hit("a.pdf", FileKind.Pdf, 0.9f)]));
        vm.Query = "x";
        await WaitUntil(() => vm.Results.Count == 1);
        vm.Query = "  ";
        Assert.Empty(vm.Results);
        Assert.Equal(LauncherContent.Hint, vm.Content);
        Assert.Equal(-1, vm.SelectedIndex);
    }

    [Fact]
    public async Task EmptyResultsShowNoResults()
    {
        using var vm = Create();
        vm.AttachSearch(new RecordingSearch(_ => []));
        vm.Query = "nothing";
        await WaitUntil(() => vm.Content == LauncherContent.NoResults);
    }

    [Fact]
    public async Task SearchErrorsAreShown()
    {
        using var vm = Create();
        vm.AttachSearch(new RecordingSearch((_, _) => throw new InvalidOperationException("boom")));
        vm.Query = "x";
        await WaitUntil(() => vm.Content == LauncherContent.Error);
        Assert.Equal("boom", vm.ErrorText);
    }

    [Fact]
    public async Task QueryTypedWhileLoadingRunsOnceTheModelIsReady()
    {
        var search = new RecordingSearch(_ => [Hit("a.pdf", FileKind.Pdf, 0.9f)]);
        using var vm = Create();
        vm.Query = "early";
        await Task.Delay(250, TestContext.Current.CancellationToken);
        Assert.Empty(search.Queries);
        vm.AttachSearch(search);
        await WaitUntil(() => vm.Results.Count == 1);
        Assert.Equal(["early"], search.Queries);
    }

    [Fact]
    public async Task FilterChipsMapToKindSetsAndRerunTheSearch()
    {
        var search = new RecordingSearch(_ => [Hit("a.pdf", FileKind.Pdf, 0.9f)]);
        using var vm = Create();
        vm.AttachSearch(search);
        vm.Query = "x";
        await WaitUntil(() => search.Filters.Count == 1);
        Assert.Same(SearchFilter.None, search.Filters[^1]);

        var docs = vm.Filters.Single(f => f.Label == "Documents");
        docs.SelectCommand!.Execute(null);
        await WaitUntil(() => search.Filters.Count == 2);
        var kinds = search.Filters[^1].Kinds!;
        Assert.True(kinds.SetEquals([FileKind.Document, FileKind.Pdf, FileKind.Text, FileKind.Notebook]));
        Assert.True(docs.IsSelected);
        Assert.False(vm.Filters[0].IsSelected);

        vm.TimeRanges.Single(t => t.Label == "Past week").SelectCommand!.Execute(null);
        await WaitUntil(() => search.Filters.Count == 3);
        var after = search.Filters[^1].ModifiedAfter!.Value;
        Assert.InRange(DateTimeOffset.UtcNow - after, TimeSpan.FromDays(6.9), TimeSpan.FromDays(7.1));
    }

    [Fact]
    public void ChipSetMatchesTheDesign() =>
        Assert.Equal(["All", "Documents", "PDFs", "Spreadsheets", "Slides", "Mail", "Code", "Images"], Create().Filters.Select(f => f.Label));

    [Fact]
    public void NavigationIsClampedAndMovesSelectionFlags()
    {
        using var vm = Create();
        vm.LoadSnapshot("q", [Hit("a.pdf", FileKind.Pdf, 0.9f), Hit("b.eml", FileKind.Mail, 0.8f), Hit("c.md", FileKind.Text, 0.7f)], TimeSpan.FromMilliseconds(5));
        Assert.Equal(0, vm.SelectedIndex);
        vm.MoveUpCommand.Execute(null);
        Assert.Equal(0, vm.SelectedIndex);
        vm.MoveDownCommand.Execute(null);
        vm.MoveDownCommand.Execute(null);
        vm.MoveDownCommand.Execute(null);
        Assert.Equal(2, vm.SelectedIndex);
        Assert.Equal([false, false, true], vm.Results.Select(r => r.IsSelected));
        Assert.True(vm.Results[2].IsExpanded);
        Assert.Same(vm.Results[2], vm.SelectedResult);

        vm.TogglePreviewCommand.Execute(null);
        Assert.False(vm.Results[2].IsExpanded);
        Assert.True(vm.Results[2].IsCompact);
    }

    [Fact]
    public async Task ActionsTargetTheSelectedResult()
    {
        var actions = new RecordingActions();
        using var vm = Create();
        vm.Actions = actions;
        vm.LoadSnapshot("q", [Hit("a.pdf", FileKind.Pdf, 0.9f), Hit("b.eml", FileKind.Mail, 0.8f)], TimeSpan.Zero);
        vm.MoveDownCommand.Execute(null);

        vm.OpenSelectedCommand.Execute(null);
        vm.RevealSelectedCommand.Execute(null);
        await vm.CopyPathCommand.ExecuteAsync(null);

        Assert.Equal(["open /home/u/Documents/b.eml", "hide", "reveal /home/u/Documents/b.eml", "hide", "copy /home/u/Documents/b.eml"], actions.Log);
    }

    [Fact]
    public void ResumeNowUnpausesAUserPauseAndOverridesThrottling()
    {
        var actions = new RecordingActions();
        using var vm = Create();
        vm.Actions = actions;
        vm.SetUserPaused(true);
        vm.UpdateStatus(IndexStatus.Initial with { State = IndexingState.PausedByUser });
        vm.ResumeNowCommand.Execute(null);
        Assert.Equal(["paused False", "resume-now"], actions.Log);

        actions.Log.Clear();
        vm.SetUserPaused(false);
        vm.UpdateStatus(IndexStatus.Initial with { State = IndexingState.Running });
        vm.TogglePauseCommand.Execute(null);
        Assert.Equal(["paused True"], actions.Log);
    }

    [Theory]
    [InlineData(IndexingState.Starting)]
    [InlineData(IndexingState.ModelUnavailable)]
    [InlineData(IndexingState.Running)]
    public void PauseToggleFollowsTheSettingWhateverTheIndexerReports(IndexingState state)
    {
        // Regression: the caption came from the indexer state (PausedByUser only once the Core ran), while the tray
        // toggled the setting — with UserPaused=true and no Core, "Pause indexing" actually resumed.
        var actions = new RecordingActions();
        using var vm = Create(settings: new ValafindSettings { UserPaused = true });
        vm.Actions = actions;
        vm.UpdateStatus(IndexStatus.Initial with { State = state });

        Assert.True(vm.Status.IsUserPaused);
        Assert.Equal("Resume indexing", vm.Status.PauseMenuText);
        Assert.Equal("Resume", vm.Status.ResumeActionText);
        vm.TogglePauseCommand.Execute(null);
        Assert.Equal(["paused False"], actions.Log);

        vm.SetUserPaused(false);
        Assert.Equal("Pause indexing", vm.Status.PauseMenuText);
    }

    [Fact]
    public void MacFlavorGroupsResultsAfterTheTopMatch()
    {
        using var vm = Create(UiFlavor.Mac);
        vm.LoadSnapshot("q",
        [
            Hit("lease.pdf", FileKind.Pdf, 0.91f),
            Hit("mail.eml", FileKind.Mail, 0.86f),
            Hit("notes.md", FileKind.Text, 0.84f),
            Hit("budget.xlsx", FileKind.Spreadsheet, 0.62f),
            Hit("other.docx", FileKind.Document, 0.60f),
        ], TimeSpan.Zero);

        Assert.Equal(["lease.pdf", "mail.eml", "notes.md", "other.docx", "budget.xlsx"], vm.Results.Select(r => r.FileName));
        Assert.Equal(["Top Match", "Mail", "Documents", null, "Spreadsheets"], vm.Results.Select(r => r.GroupHeader));
    }

    [Fact]
    public void ResultItemFormatsForTheFlavor()
    {
        using var vm = Create(UiFlavor.Linux);
        vm.LoadSnapshot("q", [Hit("IMG_1.HEIC", FileKind.Image, 0.756f, "Depositum   kr.\n28.500")], TimeSpan.Zero);
        var item = vm.Results[0];
        Assert.Equal("~/Documents", item.DisplayDirectory);
        Assert.Equal("Documents", item.Breadcrumb);
        Assert.Equal("0.76", item.ScoreText);
        Assert.Equal("Depositum kr. 28.500", item.Snippet);
        Assert.Equal("HEIC", item.ExtensionLabel);
        Assert.Same(vm, item.Launcher);
    }

    [Fact]
    public void DesignDataIsPopulatedForEveryFlavor()
    {
        foreach (var flavor in Enum.GetValues<UiFlavor>())
        {
            using var vm = DesignData.Create(flavor);
            Assert.Equal(LauncherContent.Results, vm.Content);
            Assert.True(vm.Results.Count >= 5);
            Assert.All(vm.Results.Where(r => r.Kind != FileKind.Image), r => Assert.NotEmpty(r.Highlights));
            // Only states the real app produces: images are matched by file name, without a passage (no OCR).
            Assert.All(vm.Results.Where(r => r.Kind == FileKind.Image), r =>
            {
                Assert.False(r.HasSnippet);
                Assert.Empty(r.Highlights);
            });
            Assert.Contains(vm.Results, r => r.Kind == FileKind.Image);
            Assert.Equal("41%", vm.Status.AllPercentText);
            // The default chord in the platform's notation, not a hard-coded label.
            Assert.Equal(flavor == UiFlavor.Mac ? "⌃⌥Space" : "Ctrl+Alt+Space", vm.HotkeyLabel);
        }
    }

    [Fact]
    public async Task ExplanationPatchesHighlightsInPlaceAndKeepsTheSelection()
    {
        var explainGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var search = new RecordingSearch(_ => [Hit("a.pdf", FileKind.Pdf, 0.9f, "alpha beta gamma"), Hit("b.pdf", FileKind.Pdf, 0.8f, "delta epsilon")])
        {
            Explain = async (results, ct) =>
            {
                await explainGate.Task.WaitAsync(ct);
                return [results[0], results[1] with { Highlights = [new TextSpan(6, 7)] }];
            },
        };
        using var vm = Create();
        vm.AttachSearch(search);
        vm.Query = "q";
        await WaitUntil(() => vm.Results.Count == 2);
        var rows = vm.Results.ToList();
        vm.MoveDownCommand.Execute(null);
        Assert.Equal([new TextSpan(0, 4)], rows[1].Highlights);

        var changed = new List<string?>();
        rows[1].PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        explainGate.SetResult();
        await WaitUntil(() => rows[1].Highlights.Count == 1 && rows[1].Highlights[0] == new TextSpan(6, 7));

        Assert.Equal(rows, vm.Results);             // same row objects: no list rebuild / flicker
        Assert.Equal(1, vm.SelectedIndex);          // selection kept
        Assert.Same(rows[1], vm.SelectedResult);
        Assert.Equal([new TextSpan(0, 4)], rows[0].Highlights); // unchanged result left alone
        Assert.Contains(nameof(ResultItemViewModel.Highlights), changed);
    }

    [Fact]
    public async Task NextKeystrokeCancelsTheExplanation()
    {
        CancellationToken explainToken = default;
        var explainStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var search = new RecordingSearch(q => [Hit(q + ".md", FileKind.Text, 0.5f)])
        {
            Explain = async (results, ct) =>
            {
                if (results[0].FileName == "first.md")
                {
                    explainToken = ct;
                    explainStarted.TrySetResult();
                    // Ignores the token on purpose: a late answer must still be discarded.
                    await release.Task;
                    return [results[0] with { Highlights = [new TextSpan(0, 1)] }];
                }
                return results;
            },
        };
        using var vm = Create();
        vm.AttachSearch(search);
        vm.Query = "first";
        await explainStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        vm.Query = "second";
        await WaitUntil(() => vm.Results.Count == 1 && vm.Results[0].FileName == "second.md");
        Assert.True(explainToken.IsCancellationRequested);

        release.SetResult();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal([new TextSpan(0, 4)], vm.Results[0].Highlights);
    }

    [Fact]
    public async Task ExplanationFailureKeepsTheLexicalHighlights()
    {
        var search = new RecordingSearch(_ => [Hit("a.pdf", FileKind.Pdf, 0.9f)])
        {
            Explain = (_, _) => throw new InvalidOperationException("model busy"),
        };
        using var vm = Create();
        vm.AttachSearch(search);
        vm.Query = "q";
        await WaitUntil(() => vm.Results.Count == 1);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(LauncherContent.Results, vm.Content);
        Assert.Equal([new TextSpan(0, 4)], vm.Results[0].Highlights);
    }

    [Fact]
    public async Task ResultsOfASearchInFlightAreDroppedWhenTheCoreIsStopped()
    {
        // Regression: a restart disposed the store/model while the launcher still searched them, and a search that
        // started before SetModelLoading could still post its result or "Cannot access a disposed object".
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        var search = new RecordingSearch(async (_, ct) =>
        {
            token = ct;
            started.TrySetResult();
            await release.Task; // ignores cancellation, like a search stuck in the model
            throw new ObjectDisposedException("SqliteIndexStore");
        });
        using var vm = Create();
        vm.AttachSearch(search);
        vm.Query = "lease";
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        vm.SetModelLoading();
        Assert.True(token.IsCancellationRequested);
        Assert.False(vm.IsSearching);
        release.SetResult();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(LauncherContent.ModelLoading, vm.Content);
        Assert.Null(vm.ErrorText);
        Assert.Single(search.Queries); // nothing new was sent to the stopped Core
    }

    [Fact]
    public void SnippetEllipsesComeOnlyFromTheSnippet()
    {
        // Core's Snippet.Build already puts "…" at cut ends; the views must not add another pair.
        using var vm = Create(UiFlavor.Windows);
        vm.LoadSnapshot("q", [Hit("a.pdf", FileKind.Pdf, 0.9f, "…met on Tuesday to review…"), Hit("b.md", FileKind.Text, 0.8f, "Start of file")],
            TimeSpan.Zero);
        Assert.EndsWith(" · “…met on Tuesday to review…”", vm.Results[0].CaptionWithSnippet, StringComparison.Ordinal);
        Assert.EndsWith(" · “Start of file”", vm.Results[1].CaptionWithSnippet, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UiFlavor.Mac, "Ctrl+Alt+Space", "⌃⌥Space")]
    [InlineData(UiFlavor.Mac, "Cmd+Shift+K", "⇧⌘K")]
    [InlineData(UiFlavor.Windows, "Ctrl+Alt+Space", "Ctrl+Alt+Space")]
    [InlineData(UiFlavor.Windows, "Super+F", "Win+F")]
    [InlineData(UiFlavor.Linux, "Ctrl+Alt+Space", "Ctrl+Alt+Space")]
    [InlineData(UiFlavor.Linux, "Meta+Space", "Super+Space")]
    public void HotkeyLabelShowsTheConfiguredChordPerPlatform(UiFlavor flavor, string chord, string expected)
    {
        using var vm = Create(flavor, new ValafindSettings { Hotkey = chord });
        Assert.Equal(expected, vm.HotkeyLabel);
    }

    [Fact]
    public void DefaultHotkeyIsCtrlAltSpaceEverywhere()
    {
        using var vm = Create(UiFlavor.Mac, new ValafindSettings());
        Assert.Equal("⌃⌥Space", vm.HotkeyLabel);
    }

    [Fact]
    public void IndexScopeNamesTheHomeFolderOrTheConfiguredFolders()
    {
        var settings = new ValafindSettings();
        using var vm = Create(UiFlavor.Linux, settings);
        Assert.Equal("Home folder", vm.IndexScopeText);
        Assert.Equal("Home", vm.IndexScopeShortText);
        Assert.Equal("Everything in your home folder (~)", vm.IndexScopeDetail);

        settings.Roots = ["/home/u/Documents"];
        vm.RefreshIndexScope();
        Assert.Equal("Documents", vm.IndexScopeText);
        Assert.Equal("Everything in ~/Documents", vm.IndexScopeDetail);

        settings.Roots = ["/home/u/Documents", "/media/u/Photos"];
        vm.RefreshIndexScope();
        Assert.Equal("2 folders", vm.IndexScopeText);
        Assert.Equal("Everything in ~/Documents, /media/u/Photos", vm.IndexScopeDetail);
    }

    private sealed class RecordingSearch(Func<string, CancellationToken, Task<IReadOnlyList<SearchResult>>> handler) : ISearchService
    {
        public Func<IReadOnlyList<SearchResult>, CancellationToken, Task<IReadOnlyList<SearchResult>>>? Explain { get; init; }

        public RecordingSearch(Func<string, IReadOnlyList<SearchResult>> results)
            : this((q, _) => Task.FromResult(results(q)))
        {
        }

        private readonly ConcurrentQueue<string> _queries = new();
        private readonly ConcurrentQueue<SearchFilter> _filters = new();

        public IReadOnlyList<string> Queries => [.. _queries];
        public IReadOnlyList<SearchFilter> Filters => [.. _filters];

        public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, SearchFilter filter, int topK, CancellationToken cancellationToken)
        {
            _queries.Enqueue(query);
            _filters.Enqueue(filter);
            return handler(query, cancellationToken);
        }

        public Task<IReadOnlyList<SearchResult>> ExplainAsync(string query, IReadOnlyList<SearchResult> results, CancellationToken cancellationToken) =>
            Explain?.Invoke(results, cancellationToken) ?? Task.FromResult(results);
    }

    private sealed class RecordingActions : ILauncherActions
    {
        public List<string> Log { get; } = [];
        public void Open(string path) => Log.Add("open " + path);
        public void Reveal(string path) => Log.Add("reveal " + path);
        public void QuickLook(string path) => Log.Add("quicklook " + path);
        public Task CopyTextAsync(string text)
        {
            Log.Add("copy " + text);
            return Task.CompletedTask;
        }
        public void HideLauncher() => Log.Add("hide");
        public void ShowSettings() => Log.Add("settings");
        public void Quit() => Log.Add("quit");
        public void SetUserPaused(bool paused) => Log.Add("paused " + paused);
        public void ResumeNow() => Log.Add("resume-now");
        public void CheckModel() => Log.Add("check-model");
    }
}
