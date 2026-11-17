using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Valafind.App.Platform;
using Valafind.App.Updates;
using Valafind.Core.Configuration;

namespace Valafind.App.ViewModels;

/// <summary>Application services the settings window needs.</summary>
public interface ISettingsHost
{
    /// <summary>Locates the EmbeddingGemma model (see Core ModelLocator); null when not found.</summary>
    string? DetectModel(string? configuredDirectory);

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

    /// <summary>Applies saved settings live (hotkey re-registration, core restart, …). Completes with a hotkey error
    /// message, or null when the hotkey registered. Registration can take seconds (Wayland portal), so it is async.</summary>
    Task<string?> ApplyAsync(SettingsSnapshot before, ValafindSettings after);
}

/// <summary>The settings that need action when they change.</summary>
public sealed record SettingsSnapshot(string Hotkey, int Dimensions, string? ModelDirectory, IReadOnlyList<string> Roots, IReadOnlyList<string> Excludes)
{
    /// <summary>Captures the relevant parts of <paramref name="settings"/>.</summary>
    public static SettingsSnapshot Of(ValafindSettings settings) =>
        new(settings.EffectiveHotkey, settings.EmbeddingDimensions, settings.ModelDirectory, [.. settings.Roots], [.. settings.Excludes]);

    /// <summary>True when the index must be rebuilt / the core restarted to honour the change.</summary>
    public bool RequiresCoreRestart(ValafindSettings after) =>
        Dimensions != after.EmbeddingDimensions
        || !string.Equals(ModelDirectory ?? string.Empty, after.ModelDirectory ?? string.Empty, StringComparison.Ordinal)
        || !Roots.SequenceEqual(after.Roots, StringComparer.Ordinal)
        || !Excludes.SequenceEqual(after.Excludes, StringComparer.Ordinal);
}

/// <summary>View model of the settings window.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ValafindSettings _settings;
    private readonly ISettingsHost _host;
    private readonly int _originalDimensions;
    private readonly bool _macOS;
    private readonly string _home;

    /// <summary>Creates the view model over the live <paramref name="settings"/> instance.</summary>
    public SettingsViewModel(ValafindSettings settings, ISettingsHost host)
        : this(settings, host, OperatingSystem.IsMacOS(), AppPaths.Home)
    {
    }

    /// <param name="settings">Live settings.</param>
    /// <param name="host">Application services.</param>
    /// <param name="macOS">Apply macOS hotkey rules (tests can simulate macOS).</param>
    /// <param name="home">The home folder (indexed when no folders are listed).</param>
    internal SettingsViewModel(ValafindSettings settings, ISettingsHost host, bool macOS, string home)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _macOS = macOS;
        _home = home;
        _originalDimensions = settings.EmbeddingDimensions;

        Hotkey = settings.EffectiveHotkey;
        Roots = new ObservableCollection<string>(settings.Roots);
        Roots.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoRoots));
        MaxCpuPercent = settings.MaxCpuPercent;
        MaxDiskBusyPercent = settings.MaxDiskBusyPercent;
        PauseOnBattery = settings.PauseOnBattery;
        SelectedDimensions = DimensionOptions.Contains(settings.EmbeddingDimensions) ? settings.EmbeddingDimensions : 256;
        ModelDirectory = settings.ModelDirectory ?? string.Empty;
        StartAtLogin = SafeIsAutostart();
        AutomaticUpdates = settings.AutomaticUpdates;
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
    public static string DefaultHotkeyText => "Default: " + ValafindSettings.DefaultHotkey;

    /// <summary>Indexed folders; empty means the home folder.</summary>
    public ObservableCollection<string> Roots { get; }

    /// <summary>True when no folder is listed, i.e. the home folder is indexed.</summary>
    public bool HasNoRoots => Roots.Count == 0;

    /// <summary>Empty-state row: "Your home folder (/home/me)".</summary>
    public string RootsEmptyText => "Your home folder (" + _home + ")";

    /// <summary>Explains what is (not) covered and how to add more.</summary>
    public static string RootsHint =>
        "Only the folders listed here are indexed — by default your home folder. Add other drives, external disks or "
        + "network folders with “Add folder…”. Adding a folder replaces the default, so add your home folder too if you "
        + "still want it.";

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

    /// <summary>Model directory override; empty = auto-locate.</summary>
    [ObservableProperty] public partial string ModelDirectory { get; set; }

    /// <summary>"Detected: …" or "Not found …".</summary>
    [ObservableProperty] public partial string ModelDetectionText { get; private set; } = string.Empty;

    /// <summary>True when the model was found.</summary>
    [ObservableProperty] public partial bool IsModelDetected { get; private set; }

    /// <summary>Start Valafind when the user logs in.</summary>
    [ObservableProperty] public partial bool StartAtLogin { get; set; }

    /// <summary>Download and install new releases automatically.</summary>
    [ObservableProperty] public partial bool AutomaticUpdates { get; set; }

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

    /// <summary>What the updater is doing, or why this copy cannot update itself.</summary>
    public string UpdateStatus => _updater?.StatusText ?? "Valafind checks GitHub for new versions";

    /// <summary>Stops following the updater; called when the window closes.</summary>
    public void Detach()
    {
        if (_updater is not null)
            _updater.StateChanged -= OnUpdaterStateChanged;
    }

    private void OnUpdaterStateChanged(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(UpdateStatus)));

    /// <summary>Checks GitHub now; copies that cannot update themselves open the releases page instead.</summary>
    [RelayCommand]
    private void CheckForUpdates()
    {
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
            ErrorText = "Could not open the releases page: " + e.Message;
        }
    }

    /// <summary>Save failure message.</summary>
    [ObservableProperty] public partial string? ErrorText { get; private set; }

    partial void OnModelDirectoryChanged(string value) => UpdateModelDetection();

    private void UpdateModelDetection()
    {
        string? found;
        try
        {
            found = _host.DetectModel(string.IsNullOrWhiteSpace(ModelDirectory) ? null : ModelDirectory.Trim());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            found = null;
        }
        IsModelDetected = found is not null;
        var custom = string.IsNullOrWhiteSpace(ModelDirectory) ? null : ModelDirectory.Trim();
        ModelDetectionText = found is not null
            ? "Detected: " + found
            : "Not found — install it by running " + ModelInstallHelp.FetchCommand(OperatingSystem.IsWindows(), _host.AppDirectory, custom)
              + " (installs into " + (custom ?? _host.DefaultModelDirectory) + "). Valafind picks it up automatically.";
    }

    [RelayCommand]
    private async Task AddRoot()
    {
        if (PickFolderAsync is null)
            return;
        var path = await PickFolderAsync("Choose a folder to index").ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(path) && !Roots.Contains(path, StringComparer.Ordinal))
            Roots.Add(path);
    }

    [RelayCommand]
    private void RemoveRoot(string? root)
    {
        if (root is not null)
            Roots.Remove(root);
    }

    [RelayCommand]
    private async Task BrowseModelDirectory()
    {
        if (PickFolderAsync is null)
            return;
        var path = await PickFolderAsync("Choose the EmbeddingGemma 2 model folder").ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(path))
            ModelDirectory = path;
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
        _settings.MaxCpuPercent = Math.Clamp(MaxCpuPercent, 5, 100);
        _settings.MaxDiskBusyPercent = Math.Clamp(MaxDiskBusyPercent, 5, 100);
        _settings.PauseOnBattery = PauseOnBattery;
        _settings.EmbeddingDimensions = SelectedDimensions;
        _settings.ModelDirectory = string.IsNullOrWhiteSpace(ModelDirectory) ? null : ModelDirectory.Trim();
        var updatesTurnedOn = AutomaticUpdates && !_settings.AutomaticUpdates;
        _settings.AutomaticUpdates = AutomaticUpdates;

        try
        {
            _settings.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Restore(_settings, previous);
            ErrorText = "Could not save settings: " + e.Message;
            return;
        }

        try
        {
            if (StartAtLogin != SafeIsAutostart())
                _host.SetAutostartEnabled(StartAtLogin);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            ErrorText = "Settings saved, but start at login could not be changed: " + e.Message;
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

    private static (string?, List<string>, double, double, bool, int, string?, bool) Capture(ValafindSettings s) =>
        (s.Hotkey, [.. s.Roots], s.MaxCpuPercent, s.MaxDiskBusyPercent, s.PauseOnBattery, s.EmbeddingDimensions, s.ModelDirectory, s.AutomaticUpdates);

    private static void Restore(ValafindSettings s, (string?, List<string>, double, double, bool, int, string?, bool) v) =>
        (s.Hotkey, s.Roots, s.MaxCpuPercent, s.MaxDiskBusyPercent, s.PauseOnBattery, s.EmbeddingDimensions, s.ModelDirectory, s.AutomaticUpdates) = v;

    /// <summary>Inert host for design-time data.</summary>
    internal sealed class DesignHost : ISettingsHost
    {
        public static DesignHost Instance { get; } = new();
        public string? DetectModel(string? configuredDirectory) => null;
        public string DefaultModelDirectory => Path.Combine(AppPaths.ModelsDirectory, "embeddinggemma-2");
        public string AppDirectory => AppContext.BaseDirectory;
        public string LogDirectory => AppPaths.LogDirectory;
        public void OpenLogFolder() { }
        public bool IsAutostartEnabled() => true;
        public void SetAutostartEnabled(bool enabled) { }
        public Task<string?> ApplyAsync(SettingsSnapshot before, ValafindSettings after) => Task.FromResult<string?>(null);
    }
}
