using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Valafind.Core.Abstractions;
using Valafind.Core.Configuration;

namespace Valafind.App.ViewModels;

/// <summary>State of the embedding model as far as the launcher is concerned.</summary>
public enum ModelState
{
    /// <summary>Model is being loaded in the background.</summary>
    Loading,
    /// <summary>Search is available.</summary>
    Ready,
    /// <summary>Model files were not found on disk.</summary>
    Missing,
    /// <summary>Model files exist but could not be loaded.</summary>
    Failed,
}

/// <summary>What the launcher's main area shows.</summary>
public enum LauncherContent
{
    /// <summary>Empty query: a short hint.</summary>
    Hint,
    /// <summary>Result rows.</summary>
    Results,
    /// <summary>The search returned nothing.</summary>
    NoResults,
    /// <summary>The search failed.</summary>
    Error,
    /// <summary>Model still loading.</summary>
    ModelLoading,
    /// <summary>Model not installed.</summary>
    ModelMissing,
    /// <summary>Model failed to load.</summary>
    ModelFailed,
}

/// <summary>View model behind every launcher design: query, debounced search, results, filters, index status.</summary>
public sealed partial class LauncherViewModel : ObservableObject, IDisposable
{
    /// <summary>Delay between the last keystroke and the search.</summary>
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>How long "Index now" overrides throttling.</summary>
    public static readonly TimeSpan ResumeNowDuration = TimeSpan.FromMinutes(30);

    private const int MaxResults = 30;

    private readonly ValafindSettings _settings;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private readonly string? _home;
    private readonly string _appDirectory;
    private ISearchService? _search;
    private CancellationTokenSource? _searchCts;
    private int _searchGeneration;
    private bool _disposed;

    /// <summary>Creates the launcher view model.</summary>
    /// <param name="flavor">Design flavor (affects path/hotkey formatting and grouping).</param>
    /// <param name="settings">Live settings instance (thresholds, hotkey).</param>
    public LauncherViewModel(UiFlavor flavor, ValafindSettings settings)
        : this(flavor, settings, TimeProvider.System, static a => Dispatcher.UIThread.Post(a), AppPaths.Home)
    {
    }

    /// <param name="flavor">Design flavor.</param>
    /// <param name="settings">Live settings instance.</param>
    /// <param name="time">Clock (fake in tests).</param>
    /// <param name="post">Marshals to the UI thread.</param>
    /// <param name="home">Home directory used to shorten paths.</param>
    /// <param name="appDirectory">Folder of the executable (where tools/fetch-model.* ship); defaults to
    /// <see cref="AppContext.BaseDirectory"/>.</param>
    internal LauncherViewModel(UiFlavor flavor, ValafindSettings settings, TimeProvider time, Action<Action> post, string? home,
        string? appDirectory = null)
    {
        Flavor = flavor;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time;
        _post = post;
        _home = home;
        _appDirectory = appDirectory ?? AppContext.BaseDirectory;
        Filters = FilterChipViewModel.CreateDefaults();
        TimeRanges = TimeRangeOption.CreateDefaults();
        SelectedFilter = Filters[0];
        Filters[0].IsSelected = true;
        foreach (var chip in Filters)
            chip.SelectCommand = new RelayCommand(() => SelectFilter(chip));
        foreach (var range in TimeRanges)
            range.SelectCommand = new RelayCommand(() => SelectTimeRange(range));
        SelectedTimeRange = TimeRanges[0];
        SetHotkey(settings.EffectiveHotkey, null);
        ModelTargetDirectory = settings.ModelDirectory ?? AppPaths.ModelsDirectory;
        FetchCommandText = BuildFetchCommand(null, force: false);
        RepairCommandText = BuildFetchCommand(null, force: true);
        Status.SetUserPaused(settings.UserPaused);
        RefreshIndexScope();
    }

    /// <summary>Design flavor.</summary>
    public UiFlavor Flavor { get; }

    /// <summary>Host callbacks (shell, clipboard, window). Set by the composition root.</summary>
    public ILauncherActions? Actions { get; set; }

    /// <summary>The search text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuery))]
    public partial string Query { get; set; } = string.Empty;

    /// <summary>True when the query is not blank.</summary>
    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);

    /// <summary>The query the current results belong to.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchExplanation))]
    public partial string LastQuery { get; private set; } = string.Empty;

    /// <summary>"Close in meaning to “…” — no keyword overlap needed."</summary>
    public string MatchExplanation => "Close in meaning to “" + LastQuery + "” — no keyword overlap needed.";

    /// <summary>Visible results, in display order.</summary>
    public ObservableCollection<ResultItemViewModel> Results { get; } = [];

    /// <summary>Index into <see cref="Results"/> of the keyboard selection, or -1.</summary>
    [ObservableProperty]
    public partial int SelectedIndex { get; set; } = -1;

    /// <summary>The selected result, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial ResultItemViewModel? SelectedResult { get; private set; }

    /// <summary>True when a result is selected.</summary>
    public bool HasSelection => SelectedResult is not null;

    /// <summary>File-type filter chips.</summary>
    public IReadOnlyList<FilterChipViewModel> Filters { get; }

    /// <summary>The active filter chip.</summary>
    [ObservableProperty]
    public partial FilterChipViewModel SelectedFilter { get; private set; }

    /// <summary>Modification-time options.</summary>
    public IReadOnlyList<TimeRangeOption> TimeRanges { get; }

    /// <summary>The active time range.</summary>
    [ObservableProperty]
    public partial TimeRangeOption SelectedTimeRange { get; private set; }

    /// <summary>"6 results · 38 ms".</summary>
    [ObservableProperty]
    public partial string ResultSummary { get; private set; } = string.Empty;

    /// <summary>True while a search is in flight.</summary>
    [ObservableProperty]
    public partial bool IsSearching { get; private set; }

    /// <summary>Last search error message.</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    /// <summary>Whether the preview (expanded row / side pane / snippet lines) is shown. Toggled with Tab.</summary>
    [ObservableProperty]
    public partial bool IsPreviewVisible { get; set; } = true;

    /// <summary>Embedding model state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModelLoading), nameof(IsModelMissing), nameof(IsModelFailed), nameof(IsModelReady))]
    public partial ModelState ModelState { get; private set; } = ModelState.Loading;

    /// <summary>Model state flags for bindings.</summary>
    public bool IsModelLoading => ModelState == ModelState.Loading;
    /// <inheritdoc cref="IsModelLoading"/>
    public bool IsModelMissing => ModelState == ModelState.Missing;
    /// <inheritdoc cref="IsModelLoading"/>
    public bool IsModelFailed => ModelState == ModelState.Failed;
    /// <inheritdoc cref="IsModelLoading"/>
    public bool IsModelReady => ModelState == ModelState.Ready;

    /// <summary>Why the model failed to load.</summary>
    [ObservableProperty]
    public partial string? ModelErrorText { get; private set; }

    /// <summary>Where tools/fetch-model.* installs the model (the folder Valafind looks in).</summary>
    [ObservableProperty]
    public partial string ModelTargetDirectory { get; private set; }

    /// <summary>Command that installs the model: the shipped script by absolute path, e.g.
    /// <c>bash '/opt/Valafind/tools/fetch-model.sh'</c>.</summary>
    [ObservableProperty]
    public partial string FetchCommandText { get; private set; }

    /// <summary>Same as <see cref="FetchCommandText"/> but re-downloads every file (repairs a broken install).</summary>
    [ObservableProperty]
    public partial string RepairCommandText { get; private set; }

    private string BuildFetchCommand(string? customTarget, bool force) =>
        ModelInstallHelp.FetchCommand(Flavor == UiFlavor.Windows, _appDirectory, customTarget, force);

    /// <summary>What phase 2 covers: "Home folder", the single configured folder's name, or "3 folders".</summary>
    [ObservableProperty]
    public partial string IndexScopeText { get; private set; } = string.Empty;

    /// <summary>Compact form for tight status strips: "Home", the folder's name, or "3 folders".</summary>
    [ObservableProperty]
    public partial string IndexScopeShortText { get; private set; } = string.Empty;

    /// <summary>Longer form for tooltips: "Everything in your home folder (~)".</summary>
    [ObservableProperty]
    public partial string IndexScopeDetail { get; private set; } = string.Empty;

    /// <summary>Recomputes <see cref="IndexScopeText"/> from the configured folders (call after settings change).</summary>
    public void RefreshIndexScope()
    {
        var roots = _settings.Roots.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        var comparison = Flavor == UiFlavor.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (roots.Count == 0)
        {
            IndexScopeText = "Home folder";
            IndexScopeShortText = "Home";
            IndexScopeDetail = "Everything in your home folder" + (_home is null ? string.Empty : " (" + DisplayHome() + ")");
            return;
        }
        if (roots.Count == 1)
        {
            var name = Path.GetFileName(roots[0].TrimEnd('/', '\\'));
            IndexScopeText = string.IsNullOrEmpty(name) ? roots[0] : name;
        }
        else
        {
            IndexScopeText = roots.Count.ToString(CultureInfo.InvariantCulture) + " folders";
        }
        IndexScopeShortText = IndexScopeText;
        IndexScopeDetail = "Everything in " + string.Join(", ", roots.Select(r => DisplayFormat.ShortenHome(r, _home, comparison)));

        string DisplayHome() => Flavor == UiFlavor.Windows ? _home! : "~";
    }

    /// <summary>What the main area shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHintVisible), nameof(IsResultsVisible), nameof(IsNoResultsVisible), nameof(IsErrorVisible),
        nameof(IsModelLoadingVisible), nameof(IsModelMissingVisible), nameof(IsModelFailedVisible), nameof(IsMessageVisible))]
    public partial LauncherContent Content { get; private set; } = LauncherContent.ModelLoading;

    /// <summary>Content flags for bindings.</summary>
    public bool IsHintVisible => Content == LauncherContent.Hint;
    /// <inheritdoc cref="IsHintVisible"/>
    public bool IsResultsVisible => Content == LauncherContent.Results;
    /// <inheritdoc cref="IsHintVisible"/>
    public bool IsNoResultsVisible => Content == LauncherContent.NoResults;
    /// <inheritdoc cref="IsHintVisible"/>
    public bool IsErrorVisible => Content == LauncherContent.Error;
    /// <inheritdoc cref="IsHintVisible"/>
    public bool IsModelLoadingVisible => Content == LauncherContent.ModelLoading;
    /// <inheritdoc cref="IsHintVisible"/>
    public bool IsModelMissingVisible => Content == LauncherContent.ModelMissing;
    /// <inheritdoc cref="IsHintVisible"/>
    public bool IsModelFailedVisible => Content == LauncherContent.ModelFailed;
    /// <summary>True for every non-results state (hint, empty, error, model states).</summary>
    public bool IsMessageVisible => Content != LauncherContent.Results;

    /// <summary>Index status projection.</summary>
    public IndexStatusViewModel Status { get; } = new();

    /// <summary>Hotkey as one label: "Ctrl+Alt+Space", "⌃⌥Space".</summary>
    [ObservableProperty]
    public partial string HotkeyLabel { get; private set; } = string.Empty;

    /// <summary>Hotkey split into keycaps.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> HotkeyKeys { get; private set; } = [];

    /// <summary>Registration problem, e.g. Wayland; null when the hotkey works.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHotkeyError))]
    public partial string? HotkeyError { get; private set; }

    /// <summary>True when <see cref="HotkeyError"/> is set.</summary>
    public bool HasHotkeyError => !string.IsNullOrEmpty(HotkeyError);

    /// <summary>Platform label of the reveal action ("Show in folder", "Show in Finder", "Show in Files").</summary>
    public string RevealLabel => Flavor switch
    {
        UiFlavor.Mac => "Show in Finder",
        UiFlavor.Linux => "Show in Files",
        _ => "Show in folder",
    };

    // ------------------------------------------------------------------ host API

    /// <summary>Marks the model as loading (search unavailable). Also called before the Core is stopped for a restart,
    /// so no search reaches services that are being disposed and late results of the old Core are dropped.</summary>
    public void SetModelLoading()
    {
        DetachSearch();
        ModelState = ModelState.Loading;
        ModelErrorText = null;
        ClearResults();
        UpdateContent();
    }

    /// <summary>Marks the model as not installed.</summary>
    /// <param name="targetDirectory">Where the model is expected.</param>
    /// <param name="customTarget">True when the directory is a user override (passed to the fetch script).</param>
    public void SetModelMissing(string targetDirectory, bool customTarget = false)
    {
        DetachSearch();
        ModelTargetDirectory = targetDirectory;
        FetchCommandText = BuildFetchCommand(customTarget ? targetDirectory : null, force: false);
        RepairCommandText = BuildFetchCommand(customTarget ? targetDirectory : null, force: true);
        ModelState = ModelState.Missing;
        ClearResults();
        UpdateContent();
    }

    /// <summary>Marks the model as failed to load.</summary>
    public void SetModelFailed(string message)
    {
        DetachSearch();
        ModelErrorText = message;
        ModelState = ModelState.Failed;
        ClearResults();
        UpdateContent();
    }

    // Drops the search service and everything in flight against it (results and explanations of an older Core).
    private void DetachSearch()
    {
        _search = null;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        _searchGeneration++;
        IsSearching = false;
        ErrorText = null;
    }

    /// <summary>Makes search available and runs the pending query, if any.</summary>
    public void AttachSearch(ISearchService search)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        ModelState = ModelState.Ready;
        ModelErrorText = null;
        UpdateContent();
        if (HasQuery)
            ScheduleSearch(TimeSpan.Zero);
    }

    /// <summary>Applies a new index status (must be called on the UI thread).</summary>
    public void UpdateStatus(IndexStatus status) =>
        Status.Update(status, _settings.MaxCpuPercent, _settings.MaxDiskBusyPercent);

    /// <summary>Shows whether indexing is paused by the user. The persisted setting is the single source of truth, so
    /// the captions are right even before the Core runs (or while the model is unavailable).</summary>
    public void SetUserPaused(bool paused) => Status.SetUserPaused(paused);

    /// <summary>True while the launcher window is on screen (set by the window; the host makes indexing yield).</summary>
    [ObservableProperty]
    public partial bool IsLauncherVisible { get; set; }

    /// <summary>Sets the hotkey shown in the UI and any registration error.</summary>
    public void SetHotkey(string chord, string? error)
    {
        HotkeyLabel = DisplayFormat.HotkeyLabel(chord, Flavor);
        HotkeyKeys = DisplayFormat.HotkeyKeys(chord, Flavor);
        HotkeyError = error;
    }

    /// <summary>Called when the launcher is shown: keeps the query but re-selects the first result.</summary>
    public void OnShown()
    {
        if (Results.Count > 0 && SelectedIndex < 0)
            SelectedIndex = 0;
    }

    /// <summary>Selects the given row.</summary>
    public void Select(ResultItemViewModel item)
    {
        var index = Results.IndexOf(item);
        if (index >= 0)
            SelectedIndex = index;
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private void OpenSelected()
    {
        if (CurrentOrFirst() is not { } item || Actions is null)
            return;
        Actions.Open(item.Path);
        Actions.HideLauncher();
    }

    [RelayCommand]
    private void RevealSelected()
    {
        if (CurrentOrFirst() is not { } item || Actions is null)
            return;
        Actions.Reveal(item.Path);
        Actions.HideLauncher();
    }

    [RelayCommand]
    private void QuickLookSelected()
    {
        if (CurrentOrFirst() is { } item)
            Actions?.QuickLook(item.Path);
    }

    [RelayCommand]
    private async Task CopyPath()
    {
        if (CurrentOrFirst() is { } item && Actions is not null)
            await Actions.CopyTextAsync(item.Path).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CopyText(string? text)
    {
        if (!string.IsNullOrEmpty(text) && Actions is not null)
            await Actions.CopyTextAsync(text).ConfigureAwait(true);
    }

    [RelayCommand]
    private void MoveUp()
    {
        if (Results.Count == 0)
            return;
        SelectedIndex = SelectedIndex <= 0 ? 0 : SelectedIndex - 1;
    }

    [RelayCommand]
    private void MoveDown()
    {
        if (Results.Count == 0)
            return;
        SelectedIndex = Math.Min(Results.Count - 1, SelectedIndex + 1);
    }

    [RelayCommand]
    private void Hide() => Actions?.HideLauncher();

    [RelayCommand]
    private void OpenSettings() => Actions?.ShowSettings();

    [RelayCommand]
    private void Quit() => Actions?.Quit();

    [RelayCommand]
    private void ResumeNow()
    {
        if (Status.IsUserPaused)
            Actions?.SetUserPaused(false);
        Actions?.ResumeNow();
    }

    [RelayCommand]
    private void TogglePause() => Actions?.SetUserPaused(!Status.IsUserPaused);

    /// <summary>"Check again": looks for the model right away (it is also detected automatically).</summary>
    [RelayCommand]
    private void CheckModel() => Actions?.CheckModel();

    [RelayCommand]
    private void TogglePreview() => IsPreviewVisible = !IsPreviewVisible;

    [RelayCommand]
    private void ClearQuery() => Query = string.Empty;

    [RelayCommand]
    private void SelectFilter(FilterChipViewModel? chip)
    {
        if (chip is null || ReferenceEquals(chip, SelectedFilter))
            return;
        foreach (var f in Filters)
            f.IsSelected = ReferenceEquals(f, chip);
        SelectedFilter = chip;
        ScheduleSearch(TimeSpan.Zero);
    }

    [RelayCommand]
    private void SelectTimeRange(TimeRangeOption? range)
    {
        if (range is null || ReferenceEquals(range, SelectedTimeRange))
            return;
        SelectedTimeRange = range;
        ScheduleSearch(TimeSpan.Zero);
    }

    // ------------------------------------------------------------------ search

    partial void OnQueryChanged(string value)
    {
        if (!_suppressSearch)
            ScheduleSearch(DebounceDelay);
    }

    private bool _suppressSearch;

    /// <summary>Shows a fixed query + results without running a search (design-time data and screenshots).</summary>
    internal void LoadSnapshot(string query, IReadOnlyList<SearchResult> results, TimeSpan elapsed)
    {
        _suppressSearch = true;
        try
        {
            Query = query;
        }
        finally
        {
            _suppressSearch = false;
        }
        ModelState = ModelState.Ready;
        ApplyResults(query, results, elapsed);
    }

    partial void OnSelectedIndexChanged(int value)
    {
        var selected = value >= 0 && value < Results.Count ? Results[value] : null;
        for (var i = 0; i < Results.Count; i++)
        {
            var item = Results[i];
            item.IsSelected = ReferenceEquals(item, selected);
            item.IsExpanded = item.IsSelected && IsPreviewVisible;
        }
        SelectedResult = selected;
    }

    partial void OnIsPreviewVisibleChanged(bool value)
    {
        foreach (var item in Results)
            item.IsExpanded = item.IsSelected && value;
    }

    /// <summary>The filter that corresponds to the current chip and time range.</summary>
    internal SearchFilter BuildFilter()
    {
        DateTimeOffset? after = SelectedTimeRange.MaxAge is { } age ? _time.GetUtcNow() - age : null;
        return SelectedFilter.Kinds is null && after is null ? SearchFilter.None : new SearchFilter(SelectedFilter.Kinds, after);
    }

    private void ScheduleSearch(TimeSpan delay)
    {
        if (_disposed)
            return;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        var generation = ++_searchGeneration;
        ErrorText = null;

        var query = (Query ?? string.Empty).Trim();
        if (query.Length == 0 || _search is null)
        {
            IsSearching = false;
            ClearResults();
            UpdateContent();
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        IsSearching = true;
        _ = RunSearchAsync(_search, query, BuildFilter(), delay, generation, cts.Token);
    }

    private async Task RunSearchAsync(ISearchService search, string query, SearchFilter filter, TimeSpan delay, int generation, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            var started = _time.GetTimestamp();
            var results = await Task.Run(() => search.SearchAsync(query, filter, MaxResults, ct), ct).ConfigureAwait(false);
            var elapsed = _time.GetElapsedTime(started);
            ct.ThrowIfCancellationRequested();
            _post(() =>
            {
                if (generation != _searchGeneration || _disposed)
                    return;
                ApplyResults(query, results, elapsed);
                if (results.Count > 0)
                    _ = RunExplainAsync(search, query, results, generation, ct);
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
        catch (Exception e)
        {
            _post(() =>
            {
                if (generation != _searchGeneration || _disposed)
                    return;
                IsSearching = false;
                ErrorText = e.Message;
                ClearResults();
                Content = LauncherContent.Error;
            });
        }
    }

    // Second pass: semantic "why it matched" highlights for the top results. Shown results are patched in place (no
    // list rebuild, selection kept). Cancelled by the next keystroke through the same token.
    private async Task RunExplainAsync(ISearchService search, string query, IReadOnlyList<SearchResult> results, int generation, CancellationToken ct)
    {
        try
        {
            var explained = await Task.Run(() => search.ExplainAsync(query, results, ct), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _post(() =>
            {
                if (generation == _searchGeneration && !_disposed)
                    ApplyExplanations(results, explained);
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Optional pass: the lexical highlights already shown stay.
        }
    }

    /// <summary>Replaces snippets/highlights of the shown rows with the explained ones (matched by path).</summary>
    internal void ApplyExplanations(IReadOnlyList<SearchResult> shown, IReadOnlyList<SearchResult> explained)
    {
        if (explained.Count != shown.Count)
            return;
        var byPath = new Dictionary<string, SearchResult>(StringComparer.Ordinal);
        for (var i = 0; i < shown.Count; i++)
        {
            if (!ReferenceEquals(shown[i], explained[i]) && string.Equals(shown[i].Path, explained[i].Path, StringComparison.Ordinal))
                byPath[explained[i].Path] = explained[i];
        }
        if (byPath.Count == 0)
            return;
        foreach (var item in Results)
        {
            if (byPath.TryGetValue(item.Path, out var better))
                item.ApplyExplanation(better);
        }
    }

    internal void ApplyResults(string query, IReadOnlyList<SearchResult> results, TimeSpan elapsed)
    {
        var context = new ResultDisplayContext(Flavor, _home, _time.GetUtcNow(), _time.LocalTimeZone);
        var items = results.Select(r => new ResultItemViewModel(r, context) { Launcher = this }).ToList();
        if (Flavor == UiFlavor.Mac)
            items = GroupForMac(items);

        SelectedIndex = -1;
        Results.Clear();
        foreach (var item in items)
            Results.Add(item);
        LastQuery = query;
        ErrorText = null;
        IsSearching = false;
        var ms = Math.Max(1, (int)Math.Round(elapsed.TotalMilliseconds));
        ResultSummary = string.Create(CultureInfo.InvariantCulture,
            $"{Results.Count} {(Results.Count == 1 ? "result" : "results")} · {ms} ms");
        SelectedIndex = Results.Count > 0 ? 0 : -1;
        UpdateContent();
    }

    /// <summary>macOS design: "Top Match" first, then the rest grouped by category in order of first appearance.</summary>
    private static List<ResultItemViewModel> GroupForMac(List<ResultItemViewModel> items)
    {
        if (items.Count == 0)
            return items;
        var ordered = new List<ResultItemViewModel>(items.Count) { items[0] };
        items[0].GroupHeader = "Top Match";
        foreach (var group in items.Skip(1).GroupBy(i => i.GroupName))
        {
            var first = true;
            foreach (var item in group)
            {
                item.GroupHeader = first ? group.Key : null;
                first = false;
                ordered.Add(item);
            }
        }
        return ordered;
    }

    private void ClearResults()
    {
        SelectedIndex = -1;
        if (Results.Count > 0)
            Results.Clear();
        ResultSummary = string.Empty;
    }

    private void UpdateContent()
    {
        Content = ModelState switch
        {
            ModelState.Missing => LauncherContent.ModelMissing,
            ModelState.Failed => LauncherContent.ModelFailed,
            ModelState.Loading => LauncherContent.ModelLoading,
            _ when ErrorText is not null && HasQuery && Results.Count == 0 => LauncherContent.Error,
            _ when Results.Count > 0 => LauncherContent.Results,
            _ when HasQuery && !IsSearching => LauncherContent.NoResults,
            _ when HasQuery => Content is LauncherContent.Results ? LauncherContent.Results : LauncherContent.Hint,
            _ => LauncherContent.Hint,
        };
    }

    private ResultItemViewModel? CurrentOrFirst() =>
        SelectedResult ?? (Results.Count > 0 ? Results[0] : null);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
    }
}
