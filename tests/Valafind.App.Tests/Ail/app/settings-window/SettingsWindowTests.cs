// ail-derived-from: SettingsWindow@f3c4a441129a85ca
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Valafind.App.Tests.Headless;
using Xunit;

namespace Valafind.App.Tests.Ail;

[Collection(HeadlessUiDefinition.Name)]
public sealed class SettingsWindowTests(HeadlessFixture fixture)
{
    // Linux is AppOptions' default flavor, so the scenario's "on Linux" needs no options.
    [Fact(DisplayName = "the settings window shows every setting with its current value")]
    public Task TheSettingsWindowShowsEverySettingWithItsCurrentValue() => AppHarness.RunAsync(fixture, async app =>
    {
        await app.StartAsync();

        var window = await app.OpenSettingsAsync();

        var texts = AppHarness.Texts(window).ToList();
        var joined = string.Join("\n", texts);

        // The shortcut reads "Ctrl+Alt+Space" with "Default: Ctrl+Alt+Space" under it.
        var boxes = AppHarness.Find<TextBox>(window).ToList();
        Assert.Contains(boxes, box => box.Text == "Ctrl+Alt+Space");
        Assert.Contains("Default: Ctrl+Alt+Space", texts);

        // The folders card shows "Your home folder" and the home folder's path.
        Assert.Contains($"Your home folder ({app.Home})", joined);

        // Maximum CPU reads 30%, Maximum disk busy 25% and Maximum GPU 30%.
        Assert.Contains("Maximum CPU", texts);
        Assert.Contains("Maximum disk busy", texts);
        Assert.Contains("Maximum GPU", texts);
        var sliders = AppHarness.Find<Slider>(window).ToList();
        Assert.Equal(3, sliders.Count);
        Assert.Equal(30, sliders[0].Value);
        Assert.Equal(25, sliders[1].Value);
        Assert.Equal(30, sliders[2].Value);
        Assert.Contains("30%", texts);
        Assert.Contains("25%", texts);

        // Pause on battery is on, start at login is off and automatic updates are on.
        Assert.Contains("Pause indexing on battery power", texts);
        Assert.Contains("Start Valafind when I log in", texts);
        Assert.Contains("Install new versions automatically", texts);
        var switches = AppHarness.Find<ToggleSwitch>(window).ToList();
        Assert.Equal(3, switches.Count);
        Assert.True(switches[0].IsChecked == true);
        Assert.True(switches[1].IsChecked == false);
        Assert.True(switches[2].IsChecked == true);

        // Dimensions reads 768, and the model row says "Detected:" and the model's folder.
        Assert.Contains("Dimensions", texts);
        Assert.Contains("768", texts);
        var detected = texts.FirstOrDefault(t => t.StartsWith("Detected: "));
        Assert.NotNull(detected);
        Assert.Contains("embeddinggemma-2", detected);

        // Language reads "System default".
        Assert.Contains("Language", texts);
        Assert.Contains("System default", texts);

        // The version and the log folder are shown at the end.
        var versionIndex = texts.IndexOf("Version");
        var logsIndex = texts.FindIndex(t => t.StartsWith("Logs: "));
        var languageIndex = texts.IndexOf("Language");
        var updatesIndex = texts.IndexOf("Install new versions automatically");
        Assert.True(versionIndex >= 0);
        Assert.True(logsIndex >= 0);
        Assert.True(versionIndex > languageIndex && versionIndex > updatesIndex);
        Assert.True(logsIndex > versionIndex);
        Assert.Contains(texts.Skip(versionIndex + 1), t => Regex.IsMatch(t, @"\d+\.\d+"));
        Assert.Contains("Open log folder", texts);

        AppHarness.SaveScreen(window, "the-settings-window-shows-every-setting-with-its-current-val.png");
    });
}
