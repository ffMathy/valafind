using Valafind.App.ViewModels;
using Valafind.Core.Configuration;
using Xunit;

namespace Valafind.App.Tests.ViewModels;

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "valafind-settings-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousDataDir = Environment.GetEnvironmentVariable("VALAFIND_DATA_DIR");

    public SettingsViewModelTests() => Environment.SetEnvironmentVariable("VALAFIND_DATA_DIR", _dataDir);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VALAFIND_DATA_DIR", _previousDataDir);
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void SnapshotDetectsChangesThatNeedACoreRestart()
    {
        var s = new ValafindSettings { EmbeddingDimensions = 256, Roots = ["/a"] };
        var snap = SettingsSnapshot.Of(s);
        Assert.False(snap.RequiresCoreRestart(s));
        s.MaxCpuPercent = 50;
        Assert.False(snap.RequiresCoreRestart(s));
        s.EmbeddingDimensions = 128;
        Assert.True(snap.RequiresCoreRestart(s));
        s.EmbeddingDimensions = 256;
        s.Roots = ["/a", "/b"];
        Assert.True(snap.RequiresCoreRestart(s));
    }

    [Fact]
    public async Task SaveWritesSettingsAppliesThemAndCloses()
    {
        var settings = new ValafindSettings();
        var host = new FakeHost();
        var vm = new SettingsViewModel(settings, host);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.Hotkey = "Ctrl+Shift+K";
        vm.MaxCpuPercent = 45;
        vm.SelectedDimensions = 128;
        Assert.True(vm.ShowReindexWarning);
        vm.Roots.Add("/data/docs");
        vm.StartAtLogin = true;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Equal("Ctrl+Shift+K", settings.Hotkey);
        Assert.Equal(45, settings.MaxCpuPercent);
        Assert.Equal(128, settings.EmbeddingDimensions);
        Assert.Equal(["/data/docs"], settings.Roots);
        Assert.True(host.Autostart);
        Assert.NotNull(host.Applied);
        Assert.True(host.Applied!.RequiresCoreRestart(settings));
        Assert.True(File.Exists(AppPaths.SettingsFile));
        Assert.Equal(128, ValafindSettings.Load().EmbeddingDimensions);
    }

    [Fact]
    public async Task DefaultChordIsStoredAsNull()
    {
        var settings = new ValafindSettings { Hotkey = "Ctrl+K" };
        var vm = new SettingsViewModel(settings, new FakeHost());
        vm.ResetHotkeyCommand.Execute(null);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Null(settings.Hotkey);
    }

    [Fact]
    public async Task LanguageChoiceIsSavedAndSystemDefaultIsStoredAsNull()
    {
        var settings = new ValafindSettings();
        var vm = new SettingsViewModel(settings, new FakeHost());
        Assert.Equal(string.Empty, vm.SelectedLanguage.Code);
        Assert.False(vm.ShowLanguageRestartHint);

        vm.SelectedLanguage = vm.LanguageOptions.Single(l => l.Code == "de");
        Assert.True(vm.ShowLanguageRestartHint);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("de", settings.Language);

        var again = new SettingsViewModel(settings, new FakeHost());
        Assert.Equal("de", again.SelectedLanguage.Code);
        again.SelectedLanguage = again.LanguageOptions[0];
        await again.SaveCommand.ExecuteAsync(null);
        Assert.Null(settings.Language);
    }

    [Fact]
    public async Task FailedNewHotkeyKeepsTheWindowOpen()
    {
        var vm = new SettingsViewModel(new ValafindSettings(), new FakeHost { HotkeyError = "taken" });
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        vm.Hotkey = "Ctrl+Alt+J";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(closed);
        Assert.Equal("taken", vm.HotkeyMessage);
    }

    [Fact]
    public void ModelDetectionTextReflectsTheHost()
    {
        var host = new FakeHost { Detected = "/models/embeddinggemma-300m" };
        var vm = new SettingsViewModel(new ValafindSettings(), host);
        Assert.True(vm.IsModelDetected);
        Assert.Equal("Detected: /models/embeddinggemma-300m", vm.ModelDetectionText);
        host.Detected = null;
        vm.ModelDirectory = "/nowhere";
        Assert.False(vm.IsModelDetected);
        Assert.StartsWith("Not found", vm.ModelDetectionText, StringComparison.Ordinal);
        Assert.Contains("/nowhere", vm.ModelDetectionText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Alt+Space")]
    [InlineData("Option+Shift+Space")]
    public async Task MacRejectsOptionOnlyChordsWithAClearMessage(string chord)
    {
        var settings = new ValafindSettings();
        var host = new FakeHost();
        var vm = new SettingsViewModel(settings, host, macOS: true, home: "/Users/u");
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        vm.Hotkey = chord;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(closed);
        Assert.Null(host.Applied);              // nothing saved or applied
        Assert.Null(settings.Hotkey);
        Assert.Contains("only Option", vm.HotkeyMessage, StringComparison.Ordinal);
        Assert.Contains("⌃⌥Space", vm.HotkeyMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Ctrl+Alt+Space", true)]
    [InlineData("Cmd+Alt+K", true)]
    [InlineData("Alt+Space", false)]
    public void OptionOnlyChordsAreFineOutsideMacOs(string chord, bool mac)
    {
        var vm = new SettingsViewModel(new ValafindSettings(), new FakeHost(), macOS: mac, home: "/home/u");
        Assert.Null(vm.ValidateHotkey(chord));
    }

    [Fact]
    public void FoldersShowTheHomeFolderWhenNoneAreListed()
    {
        var vm = new SettingsViewModel(new ValafindSettings(), new FakeHost(), macOS: false, home: "/home/u");
        Assert.True(vm.HasNoRoots);
        Assert.Equal("Your home folder (/home/u)", vm.RootsEmptyText);
        Assert.Contains("external", SettingsViewModel.RootsHint, StringComparison.Ordinal);

        var raised = false;
        vm.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(SettingsViewModel.HasNoRoots);
        vm.Roots.Add("/media/u/Photos");
        Assert.True(raised);
        Assert.False(vm.HasNoRoots);
    }

    [Fact]
    public void ModelHintNamesTheShippedScriptAndTarget()
    {
        var vm = new SettingsViewModel(new ValafindSettings(), new FakeHost(), macOS: false, home: "/home/u");
        Assert.False(vm.IsModelDetected);
        Assert.Contains("tools", vm.ModelDetectionText, StringComparison.Ordinal);
        Assert.Contains("/opt/Valafind", vm.ModelDetectionText, StringComparison.Ordinal);
        Assert.Contains("/default/models/embeddinggemma-300m", vm.ModelDetectionText, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenLogFolderGoesThroughTheHost()
    {
        var host = new FakeHost();
        var vm = new SettingsViewModel(new ValafindSettings(), host);
        Assert.Equal("/data/logs", vm.LogDirectory);
        vm.OpenLogFolderCommand.Execute(null);
        Assert.Equal(1, host.LogFolderOpened);
    }

    private sealed class FakeHost : ISettingsHost
    {
        public string? Detected { get; set; }
        public string? HotkeyError { get; init; }
        public bool Autostart { get; private set; }
        public SettingsSnapshot? Applied { get; private set; }
        public string DefaultModelDirectory => "/default/models/embeddinggemma-300m";
        public string AppDirectory => "/opt/Valafind";
        public string LogDirectory => "/data/logs";
        public int LogFolderOpened { get; private set; }
        public void OpenLogFolder() => LogFolderOpened++;
        public string? DetectModel(string? configuredDirectory) => Detected;
        public bool IsAutostartEnabled() => Autostart;
        public void SetAutostartEnabled(bool enabled) => Autostart = enabled;
        public async Task<string?> ApplyAsync(SettingsSnapshot before, ValafindSettings after)
        {
            Applied = before;
            await Task.Yield();
            return HotkeyError;
        }
    }
}
