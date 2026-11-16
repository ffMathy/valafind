using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Valafind.Core.Configuration;

namespace Valafind.App.ViewModels;

/// <summary>Application services the settings window needs.</summary>
public interface ISettingsHost
{
    /// <summary>Locates the EmbeddingGemma model (see Core ModelLocator); null when not found.</summary>
    string? DetectModel(string? configuredDirectory);

    /// <summary>Folder the fetch script installs the model into by default.</summary>
    string DefaultModelDirectory { get; }

    /// <summary>Whether Valafind starts at login.</summary>
    bool IsAutostartEnabled();

    /// <summary>Enables/disables start at login.</summary>
    void SetAutostartEnabled(bool enabled);

    /// <summary>Applies saved settings live (hotkey re-registration, core restart, …).
    /// Returns a hotkey error message, or null when the hotkey registered.</summary>
    string? Apply(SettingsSnapshot before, ValafindSettings after);
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

    /// <summary>Creates the view model over the live <paramref name="settings"/> instance.</summary>
    public SettingsViewModel(ValafindSettings settings, ISettingsHost host)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _originalDimensions = settings.EmbeddingDimensions;

        Hotkey = settings.EffectiveHotkey;
        Roots = new ObservableCollection<string>(settings.Roots);
        MaxCpuPercent = settings.MaxCpuPercent;
        MaxDiskBusyPercent = settings.MaxDiskBusyPercent;
        PauseOnBattery = settings.PauseOnBattery;
        SelectedDimensions = DimensionOptions.Contains(settings.EmbeddingDimensions) ? settings.EmbeddingDimensions : 256;
        ModelDirectory = settings.ModelDirectory ?? string.Empty;
        StartAtLogin = SafeIsAutostart();
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

    /// <summary>Hint shown when <see cref="Roots"/> is empty.</summary>
    public static string RootsEmptyText => "Your home folder (" + AppPaths.Home + ") is indexed when no folders are listed.";

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
        ModelDetectionText = found is not null
            ? "Detected: " + found
            : "Not found — install it with " + (OperatingSystem.IsWindows() ? @"tools\fetch-model.ps1" : "tools/fetch-model.sh")
              + " (target: " + (string.IsNullOrWhiteSpace(ModelDirectory) ? _host.DefaultModelDirectory : ModelDirectory.Trim()) + ")";
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
        var path = await PickFolderAsync("Choose the EmbeddingGemma model folder").ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(path))
            ModelDirectory = path;
    }

    [RelayCommand]
    private void ResetHotkey() => Hotkey = ValafindSettings.DefaultHotkey;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Save()
    {
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

        var hotkeyChanged = !string.Equals(before.Hotkey, _settings.EffectiveHotkey, StringComparison.OrdinalIgnoreCase);
        HotkeyMessage = _host.Apply(before, _settings);
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

    private static (string?, List<string>, double, double, bool, int, string?) Capture(ValafindSettings s) =>
        (s.Hotkey, [.. s.Roots], s.MaxCpuPercent, s.MaxDiskBusyPercent, s.PauseOnBattery, s.EmbeddingDimensions, s.ModelDirectory);

    private static void Restore(ValafindSettings s, (string?, List<string>, double, double, bool, int, string?) v) =>
        (s.Hotkey, s.Roots, s.MaxCpuPercent, s.MaxDiskBusyPercent, s.PauseOnBattery, s.EmbeddingDimensions, s.ModelDirectory) = v;

    /// <summary>Inert host for design-time data.</summary>
    internal sealed class DesignHost : ISettingsHost
    {
        public static DesignHost Instance { get; } = new();
        public string? DetectModel(string? configuredDirectory) => null;
        public string DefaultModelDirectory => Path.Combine(AppPaths.ModelsDirectory, "embeddinggemma-300m");
        public bool IsAutostartEnabled() => true;
        public void SetAutostartEnabled(bool enabled) { }
        public string? Apply(SettingsSnapshot before, ValafindSettings after) => null;
    }
}
