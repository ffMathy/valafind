using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Valafind.App.Platform;
using Valafind.App.Updates;
using Valafind.Core.Configuration;
using Valafind.App.Localization;

namespace Valafind.App.ViewModels;

/// <summary>Application services the settings window needs.</summary>
public interface ISettingsHost
{
    /// <summary>Locates the EmbeddingGemma model (see Core ModelLocator); null when not found.</summary>
    string? DetectModel();

    /// <summary>Folder the fetch script installs the model into by default.</summary>
    string DefaultModelDirectory { get; }

    /// <summary>Folder of the executable; the fetch scripts ship in its <c>tools</c> sub-folder.</summary>
    string AppDirectory { get; }

    /// <summary>Folder holding Valafind's log files.</summary>
    string LogDirectory { get; }

    /// <summary>Opens <see cref="LogDirectory"/> in the file manager.</summary>
    void OpenLogFolder();

    /// <summary>Whether Valafind starts at login.</summary>
    bool IsAutostartEnabled();

    /// <summary>Enables/disables start at login.</summary>
    void SetAutostartEnabled(bool enabled);

    /// <summary>True when the system, not Valafind, switches start at login (the Microsoft Store package's startup
    /// task); the window then says where to change it instead of offering the switch.</summary>
    bool IsAutostartManagedBySystem => false;

    /// <summary>macOS App Sandbox: a bookmark that keeps access to the folder <paramref name="path"/>, which the user
    /// just picked, after Valafind restarts (see <see cref="FolderAccess"/>); null elsewhere.</summary>
    string? RememberFolderAccess(string path) => null;

    /// <summary>Applies saved settings live (hotkey re-registration, core restart, …). Completes with a hotkey error
    /// message, or null when the hotkey registered. Registration can take seconds (Wayland portal), so it is async.</summary>
    Task<string?> ApplyAsync(SettingsSnapshot before, ValafindSettings after);

    /// <summary>Quits and starts the downloaded update right away (and shows its launcher).</summary>
    void RestartToUpdate();
}

/// <summary>The settings that need action when they change.</summary>
public sealed record SettingsSnapshot(string Hotkey, int Dimensions, IReadOnlyList<string> Roots, IReadOnlyList<string> Excludes)
{
    /// <summary>Captures the relevant parts of <paramref name="settings"/>.</summary>
    public static SettingsSnapshot Of(ValafindSettings settings) =>
        new(settings.EffectiveHotkey, settings.EmbeddingDimensions, [.. settings.Roots], [.. settings.Excludes]);

    /// <summary>True when the index must be rebuilt / the core restarted to honour the change.</summary>
    public bool RequiresCoreRestart(ValafindSettings after) =>
        Dimensions != after.EmbeddingDimensions
        || !Roots.SequenceEqual(after.Roots, StringComparer.Ordinal)
        || !Excludes.SequenceEqual(after.Excludes, StringComparer.Ordinal);
}

/// <summary>View model of the settings window.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ValafindSettings _settings;
    private readonly ISettingsHost _host;
    private readonly int _originalDimensions;
    private readonly string _originalLanguage;
    private readonly bool _macOS;
    private readonly string _home;
    private readonly bool _foldersNeedGrant;
    private readonly Dictionary<string, string> _bookmarks;

    /// <summary>Creates the view model over the live <paramref name="settings"/> instance.</summary>
    public SettingsViewModel(ValafindSettings settings, ISettingsHost host)
        : this(settings, host, OperatingSystem.IsMacOS(), AppPaths.UserHome, FolderAccess.IsRequired)
    {
    }

    /// <param name="settings">Live settings.</param>
    /// <param name="host">Application services.</param>
    /// <param name="macOS">Apply macOS hotkey rules (tests can simulate macOS).</param>
    /// <param name="home">The home folder (indexed when no folders are listed).</param>
    /// <param name="foldersNeedGrant">The macOS App Sandbox: only folders the user picked are indexed, and each needs
    /// a bookmark to stay readable after a restart.</param>
    internal SettingsViewModel(ValafindSettings settings, ISettingsHost host, bool macOS, string home, bool foldersNeedGrant = false)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _macOS = macOS;
        _home = home;
        _foldersNeedGrant = foldersNeedGrant;
        _bookmarks = new Dictionary<string, string>(settings.RootBookmarks, StringComparer.Ordinal);
        _originalDimensions = settings.EmbeddingDimensions;

        Hotkey = settings.EffectiveHotkey;
        Roots = new ObservableCollection<string>(settings.Roots);
        Roots.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoRoots));
        MaxCpuPercent = settings.MaxCpuPercent;
        MaxDiskBusyPercent = settings.MaxDiskBusyPercent;
        MaxGpuPercent = settings.MaxGpuPercent;
        PauseOnBattery = settings.PauseOnBattery;
        SelectedDimensions = DimensionOptions.Contains(settings.EmbeddingDimensions) ? settings.EmbeddingDimensions : 768;
        StartAtLogin = SafeIsAutostart();
        AutomaticUpdates = settings.AutomaticUpdates;
        _originalLanguage = settings.Language ?? string.Empty;
        SelectedLanguage = LanguageOptions.FirstOrDefault(l => string.Equals(l.Code, settings.Language, StringComparison.OrdinalIgnoreCase))
            ?? LanguageOptions[0];
        UpdateModelDetection();
    }

    /// <summary>Folder picker supplied by the window; returns null when cancelled.</summary>
    public Func<string, Task<string?>>? PickFolderAsync { get; set; }

    /// <summary>Raised when the window should close (after save or cancel).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Global hotkey chord, e.g. "Ctrl+Alt+Space".</summary>
    [ObservableProperty] public partial string Hotkey { get; set; }

    /// <summary>Message about the hotkey (registration failure).</summary>
    [ObservableProperty] public partial string? HotkeyMessage { get; set; }

    /// <summary>Platform default chord, shown as a hint.</summary>
    public static string DefaultHotkeyText => Loc.Format(Strings.SettingsDefaultHotkey, ValafindSettings.DefaultHotkey);

    /// <summary>Indexed folders; empty means the home folder (nothing in the macOS App Sandbox).</summary>
    public ObservableCollection<string> Roots { get; }

    /// <summary>True when no folder is listed, i.e. the home folder is indexed.</summary>
    public bool HasNoRoots => Roots.Count == 0;

    /// <summary>Empty-state row: "Your home folder (/home/me)", or in the macOS App Sandbox, a prompt to add folders.</summary>
    public string RootsEmptyText => _foldersNeedGrant ? Strings.SettingsNoFoldersRow : Loc.Format(Strings.SettingsHomeFolderRow, _home);

    /// <summary>Explains what is (not) covered and how to add more.</summary>
    public string RootsHint => _foldersNeedGrant ? Strings.SettingsRootsHintSandbox : Strings.SettingsRootsHint;

    /// <summary>True when Windows switches start at login (Microsoft Store copies), so the switch is replaced by
    /// <see cref="StartAtLoginManagedText"/>.</summary>
    public bool IsStartAtLoginManagedBySystem => _host.IsAutostartManagedBySystem;

    /// <summary>Says where to change start at login when Windows manages it.</summary>
    public static string StartAtLoginManagedText => Strings.SettingsStartAtLoginWindowsStore;

    /// <summary>Where the log files are written.</summary>
    public string LogDirectory => _host.LogDirectory;

    /// <summary>Index only while CPU stays below this.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxCpuText))]
    public partial double MaxCpuPercent { get; set; }

    /// <summary>"30%".</summary>
    public string MaxCpuText => MaxCpuPercent.ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>Index only while disk busy time stays below this.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxDiskText))]
    public partial double MaxDiskBusyPercent { get; set; }

    /// <summary>"25%".</summary>
    public string MaxDiskText => MaxDiskBusyPercent.ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>Keep the GPU below this while the model runs on it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxGpuText))]
    public partial double MaxGpuPercent { get; set; }

    /// <summary>"30%".</summary>
    public string MaxGpuText => MaxGpuPercent.ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>Pause indexing on battery power.</summary>
    [ObservableProperty] public partial bool PauseOnBattery { get; set; }

    /// <summary>Allowed Matryoshka sizes.</summary>
    public IReadOnlyList<int> DimensionOptions { get; } = [768, 512, 256, 128];

    /// <summary>Chosen embedding size.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowReindexWarning))]
    public partial int SelectedDimensions { get; set; }

    /// <summary>True when saving will rebuild the index.</summary>
    public bool ShowReindexWarning => SelectedDimensions != _originalDimensions;

    /// <summary>"Detected: …" or "Not found …".</summary>
    [ObservableProperty] public partial string ModelDetectionText { get; private set; } = string.Empty;

    /// <summary>True when the model was found.</summary>
    [ObservableProperty] public partial bool IsModelDetected { get; private set; }

    /// <summary>Start Valafind when the user logs in.</summary>
    [ObservableProperty] public partial bool StartAtLogin { get; set; }

    /// <summary>Download and install new releases automatically.</summary>
    [ObservableProperty] public partial bool AutomaticUpdates { get; set; }

    /// <summary>Language choices: the system's language first (empty code), then every shipped translation.</summary>
    public IReadOnlyList<UiLanguage> LanguageOptions { get; } = [new(string.Empty, Strings.SettingsLanguageSystem), .. Loc.Languages];

    /// <summary>Chosen UI language.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLanguageRestartHint))]
    public partial UiLanguage SelectedLanguage { get; set; }

    /// <summary>True when the chosen language differs from the one in use (it applies at the next start).</summary>
    public bool ShowLanguageRestartHint => !string.Equals(SelectedLanguage?.Code ?? string.Empty, _originalLanguage, StringComparison.OrdinalIgnoreCase);

    /// <summary>The updater, when the app has one (not in the designer or in tests).</summary>
    public Updater? Updater
    {
        get => _updater;
        init
        {
            _updater = value;
            if (value is not null)
                value.StateChanged += OnUpdaterStateChanged;
        }
    }

    private readonly Updater? _updater;

    /// <summary>The version of Valafind that is running, e.g. "7.1.0".</summary>
    public string AppVersion => (_updater?.CurrentVersion ?? ReleaseVersion.Current()).ToString();

    /// <summary>What the updater is doing, or why this copy cannot update itself.</summary>
    public string UpdateStatus => _updater?.StatusText ?? Strings.UpdateChecksGitHub;

    /// <summary>Stops following the updater; called when the window closes.</summary>
    public void Detach()
    {
        if (_updater is not null)
            _updater.StateChanged -= OnUpdaterStateChanged;
    }

    private void OnUpdaterStateChanged(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(UpdateStatus));
            OnPropertyChanged(nameof(UpdateButtonText));
        });

    /// <summary>True once a newer version is in place and only a restart is needed to run it.</summary>
    public bool IsUpdateReady => _updater is { State: UpdateState.ReadyToRestart };

    /// <summary>"Check for updates", or "Restart to update" once an update has been downloaded.</summary>
    public string UpdateButtonText => IsUpdateReady ? Strings.SettingsRestartToUpdate : Strings.SettingsCheckForUpdates;

    /// <summary>
    /// Restarts into a downloaded update; otherwise checks GitHub now. Copies that cannot update themselves open the
    /// releases page instead.
    /// </summary>
    [RelayCommand]
    private void CheckForUpdates()
    {
        if (IsUpdateReady)
        {
            _host.RestartToUpdate();
            return;
        }
        if (_updater is { IsSupported: true })
        {
            _ = _updater.CheckNowAsync();
            return;
        }
        try
        {
            ShellIntegration.OpenUrl(Services.Installation.App.ReleasesPage);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            ErrorText = Loc.Format(Strings.ErrorOpenReleasesPage, e.Message);
        }
    }

    /// <summary>Save failure message.</summary>
    [ObservableProperty] public partial string? ErrorText { get; private set; }

    private void UpdateModelDetection()
    {
        string? found;
        try
        {
            found = _host.DetectModel();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            found = null;
        }
        IsModelDetected = found is not null;
        ModelDetectionText = found is not null
            ? Loc.Format(Strings.SettingsModelDetected, found)
            : Loc.Format(Strings.SettingsModelNotFound, ModelInstallHelp.FetchCommand(OperatingSystem.IsWindows(), _host.AppDirectory, null), _host.DefaultModelDirectory);
    }

    [RelayCommand]
    private async Task AddRoot()
    {
        if (PickFolderAsync is null)
            return;
        var path = await PickFolderAsync(Strings.PickFolderToIndex).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path) || Roots.Contains(path, StringComparer.Ordinal))
            return;
        // In the sandbox the open panel's access ends when Valafind quits; a bookmark made now keeps it.
        if (_foldersNeedGrant && _host.RememberFolderAccess(path) is { } bookmark)
            _bookmarks[path] = bookmark;
        Roots.Add(path);
    }

    [RelayCommand]
    private void RemoveRoot(string? root)
    {
        if (root is not null)
            Roots.Remove(root);
    }

    [RelayCommand]
    private void ResetHotkey() => Hotkey = ValafindSettings.DefaultHotkey;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenLogFolder() => _host.OpenLogFolder();

    /// <summary>Checks the chord before saving; returns a message for the user, or null when it is acceptable.</summary>
    internal string? ValidateHotkey(string? chord)
    {
        if (string.IsNullOrWhiteSpace(chord))
            return null; // Default.
        if (!HotkeyChord.TryParse(chord.Trim(), out var parsed, out var error))
            return error;
        return _macOS ? HotkeyChord.MacRestriction(parsed) : null;
    }

    [RelayCommand]
    private async Task Save()
    {
        ErrorText = null;
        if (ValidateHotkey(Hotkey) is { } invalid)
        {
            // Keep the window open on the shortcut problem; nothing is saved.
            HotkeyMessage = invalid;
            return;
        }

        var before = SettingsSnapshot.Of(_settings);
        var previous = Capture(_settings);

        _settings.Hotkey = string.Equals(Hotkey?.Trim(), ValafindSettings.DefaultHotkey, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(Hotkey)
            ? null
            : Hotkey.Trim();
        _settings.Roots = [.. Roots];
        _settings.RootBookmarks = Roots.Where(_bookmarks.ContainsKey).ToDictionary(root => root, root => _bookmarks[root], StringComparer.Ordinal);
        _settings.MaxCpuPercent = Math.Clamp(MaxCpuPercent, 5, 100);
        _settings.MaxDiskBusyPercent = Math.Clamp(MaxDiskBusyPercent, 5, 100);
        _settings.MaxGpuPercent = Math.Clamp(MaxGpuPercent, 5, 100);
        _settings.PauseOnBattery = PauseOnBattery;
        _settings.EmbeddingDimensions = SelectedDimensions;
        var updatesTurnedOn = AutomaticUpdates && !_settings.AutomaticUpdates;
        _settings.AutomaticUpdates = AutomaticUpdates;
        _settings.Language = string.IsNullOrEmpty(SelectedLanguage?.Code) ? null : SelectedLanguage.Code;

        try
        {
            _settings.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Restore(_settings, previous);
            ErrorText = Loc.Format(Strings.ErrorSaveSettings, e.Message);
            return;
        }

        try
        {
            if (!IsStartAtLoginManagedBySystem && StartAtLogin != SafeIsAutostart())
                _host.SetAutostartEnabled(StartAtLogin);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            ErrorText = Loc.Format(Strings.ErrorAutostart, e.Message);
        }

        if (updatesTurnedOn && _updater is { IsSupported: true, State: UpdateState.Idle or UpdateState.Disabled })
            _ = _updater.CheckNowAsync();

        var hotkeyChanged = !string.Equals(before.Hotkey, _settings.EffectiveHotkey, StringComparison.OrdinalIgnoreCase);
        HotkeyMessage = await _host.ApplyAsync(before, _settings).ConfigureAwait(true);
        // Stay open when the user just picked a chord that could not be registered, so they can choose another.
        if (ErrorText is null && (HotkeyMessage is null || !hotkeyChanged))
            CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool SafeIsAutostart()
    {
        try
        {
            return _host.IsAutostartEnabled();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static (string?, List<string>, Dictionary<string, string>, double, double, double, bool, int, bool, string?) Capture(ValafindSettings s) =>
        (s.Hotkey, [.. s.Roots], new(s.RootBookmarks, StringComparer.Ordinal), s.MaxCpuPercent, s.MaxDiskBusyPercent, s.MaxGpuPercent, s.PauseOnBattery,
            s.EmbeddingDimensions, s.AutomaticUpdates, s.Language);

    private static void Restore(ValafindSettings s, (string?, List<string>, Dictionary<string, string>, double, double, double, bool, int, bool, string?) v) =>
        (s.Hotkey, s.Roots, s.RootBookmarks, s.MaxCpuPercent, s.MaxDiskBusyPercent, s.MaxGpuPercent, s.PauseOnBattery, s.EmbeddingDimensions,
            s.AutomaticUpdates, s.Language) = v;

    /// <summary>Inert host for design-time data.</summary>
    internal sealed class DesignHost : ISettingsHost
    {
        public void RestartToUpdate() { }

        public static DesignHost Instance { get; } = new();
        public string? DetectModel() => null;
        public string DefaultModelDirectory => Path.Combine(AppPaths.ModelsDirectory, "embeddinggemma-2");
        public string AppDirectory => AppContext.BaseDirectory;
        public string LogDirectory => AppPaths.LogDirectory;
        public void OpenLogFolder() { }
        public bool IsAutostartEnabled() => true;
        public void SetAutostartEnabled(bool enabled) { }
        public Task<string?> ApplyAsync(SettingsSnapshot before, ValafindSettings after) => Task.FromResult<string?>(null);
    }
}
