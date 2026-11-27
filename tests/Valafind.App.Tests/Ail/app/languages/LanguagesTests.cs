// ail-derived-from: Languages@2e9621cd94ac9311
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Valafind.App.Tests.Headless;
using Xunit;

namespace Valafind.App.Tests.Ail;

[Collection(HeadlessUiDefinition.Name)]
public sealed class LanguagesTests(HeadlessFixture fixture)
{
    private const string Hint = "Takes effect the next time Valafind starts.";

    [Fact(DisplayName = "Arabic reads from right to left")]
    public Task ArabicReadsFromRightToLeft() => AppHarness.RunAsync(fixture, async app =>
    {
        await app.StartAsync();

        var launcher = app.LauncherWindow;
        Assert.NotNull(launcher);
        Assert.Equal(FlowDirection.RightToLeft, launcher!.FlowDirection);

        var launcherTexts = AppHarness.Texts(launcher);
        Assert.Contains("الكل", launcherTexts);
        Assert.Contains(launcherTexts, t => t.Any(IsArabicLetter));

        // The filters start from the right with "الكل".
        var all = AppHarness.Find<TextBlock>(launcher).First(t => t.Text == "الكل");
        var allCenter = CenterIn(all, launcher);
        Assert.True(allCenter.X > launcher.Bounds.Width / 2,
            $"\"الكل\" should sit on the right, but its centre is at x={allCenter.X} of {launcher.Bounds.Width}");

        var settings = await app.OpenSettingsAsync();
        Assert.NotNull(settings);
        Assert.Equal("إعدادات Valafind", settings.Title);
        Assert.Equal(FlowDirection.RightToLeft, settings.FlowDirection);

        // Its controls on the left.
        var combo = AppHarness.Find<ComboBox>(settings).First();
        var comboCenter = CenterIn(combo, settings);
        Assert.True(comboCenter.X < settings.Bounds.Width / 2,
            $"the settings controls should sit on the left, but a drop-down's centre is at x={comboCenter.X} of {settings.Bounds.Width}");

        AppHarness.SaveScreen(settings, "arabic-reads-from-right-to-left.png");
    }, new AppOptions { Language = "ar" });

    [Fact(DisplayName = "a language picked in the settings waits for the next start")]
    public Task ALanguagePickedInTheSettingsWaitsForTheNextStart() => AppHarness.RunAsync(fixture, async app =>
    {
        await app.StartAsync();

        var launcher = app.LauncherWindow;
        Assert.NotNull(launcher);
        Assert.Contains("All", AppHarness.Texts(launcher!));

        var settings = await app.OpenSettingsAsync();
        Assert.NotNull(settings);

        var picker = AppHarness.Find<ComboBox>(settings)
            .First(c => c.Items.Cast<object?>().Any(i => NameOf(i) == "System default"));
        var deutsch = picker.Items.Cast<object?>().First(i => NameOf(i) == "Deutsch");
        await Dispatcher.UIThread.InvokeAsync(() => picker.SelectedItem = deutsch);
        Dispatcher.UIThread.RunJobs();

        // The hint says when it applies, and the settings stay in English (Save then closes them).
        var settingsTexts = AppHarness.Texts(settings);
        Assert.Contains(Hint, settingsTexts);
        Assert.Equal("System default", NameOf(picker.Items.Cast<object?>().First()));
        Assert.Equal("Deutsch", NameOf(picker.SelectedItem));
        Assert.Equal("Valafind Settings", settings.Title);
        Assert.Contains("Save", settingsTexts);
        AppHarness.SaveScreen(settings, "a-language-picked-in-the-settings-waits-for-the-next-start.png");

        var save = AppHarness.Find<Button>(settings).First(b => ButtonText(b) == "Save");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (save.Command is { } command && command.CanExecute(save.CommandParameter))
                command.Execute(save.CommandParameter);
            else
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        // The open windows stay in English: the launcher, which the settings hid, once it opens again.
        app.ShowLauncher();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("All", AppHarness.Texts(launcher!));
        Assert.DoesNotContain("Alle", AppHarness.Texts(launcher!));
    }, new AppOptions { Language = "en" });

    [Fact(DisplayName = "a parent language stands in for the system's own")]
    public Task AParentLanguageStandsInForTheSystemsOwn() => AppHarness.RunAsync(fixture, async app =>
    {
        await app.StartAsync();

        var launcher = app.LauncherWindow;
        Assert.NotNull(launcher);
        Assert.Equal(FlowDirection.LeftToRight, launcher!.FlowDirection);

        var texts = AppHarness.Texts(launcher);
        Assert.Contains("Alle", texts);
        Assert.DoesNotContain("All", texts);

        AppHarness.SaveScreen(launcher, "a-parent-language-stands-in-for-the-system-s-own.png");
    }, new AppOptions { Language = "de-AT" });

    private static bool IsArabicLetter(char c) => c >= '\u0600' && c <= '\u06FF';

    private static Point CenterIn(Control control, Visual window)
    {
        var center = new Point(control.Bounds.Width / 2, control.Bounds.Height / 2);
        var point = control.TranslatePoint(center, window);
        Assert.True(point.HasValue, "the control is not inside the window");
        return point!.Value;
    }

    private static string? NameOf(object? item)
    {
        if (item is null) return null;
        if (item is string s) return s;
        if (item is ContentControl { Content: { } content }) return NameOf(content);
        if (item is TextBlock tb) return tb.Text;
        var property = item.GetType().GetProperty("NativeName");
        if (property?.GetValue(item) is string name) return name;
        return item.ToString();
    }

    private static string? ButtonText(Button button) => button.Content switch
    {
        string s => s,
        TextBlock tb => tb.Text,
        Control c => AppHarness.Find<TextBlock>(c).Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrEmpty(t)),
        _ => null,
    };
}
