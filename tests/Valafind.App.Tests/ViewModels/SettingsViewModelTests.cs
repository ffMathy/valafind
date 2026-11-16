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
    public void SaveWritesSettingsAppliesThemAndCloses()
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
        vm.SaveCommand.Execute(null);

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
    public void DefaultChordIsStoredAsNull()
    {
        var settings = new ValafindSettings { Hotkey = "Ctrl+K" };
        var vm = new SettingsViewModel(settings, new FakeHost());
        vm.ResetHotkeyCommand.Execute(null);
        vm.SaveCommand.Execute(null);
        Assert.Null(settings.Hotkey);
    }

    [Fact]
    public void FailedNewHotkeyKeepsTheWindowOpen()
    {
        var vm = new SettingsViewModel(new ValafindSettings(), new FakeHost { HotkeyError = "taken" });
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        vm.Hotkey = "Ctrl+Alt+J";
        vm.SaveCommand.Execute(null);
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

    private sealed class FakeHost : ISettingsHost
    {
        public string? Detected { get; set; }
        public string? HotkeyError { get; init; }
        public bool Autostart { get; private set; }
        public SettingsSnapshot? Applied { get; private set; }
        public string DefaultModelDirectory => "/default/models/embeddinggemma-300m";
        public string? DetectModel(string? configuredDirectory) => Detected;
        public bool IsAutostartEnabled() => Autostart;
        public void SetAutostartEnabled(bool enabled) => Autostart = enabled;
        public string? Apply(SettingsSnapshot before, ValafindSettings after)
        {
            Applied = before;
            return HotkeyError;
        }
    }
}
