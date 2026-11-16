using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Valafind.App.ViewModels;
using Valafind.App.Views;
using Xunit;

namespace Valafind.App.Tests.Headless;

/// <summary>
/// Renders each platform launcher with the design-time data at 1x. When <c>VALAFIND_SCREENSHOT_DIR</c> is set, the
/// frames are saved there as PNG for visual review against the mockups.
/// </summary>
[Collection(HeadlessUiDefinition.Name)]
public sealed class LauncherScreenshotTests(HeadlessFixture fixture)
{
    private const string ScreenshotDirVariable = "VALAFIND_SCREENSHOT_DIR";

    public static TheoryData<UiFlavor, string> Cases() => new()
    {
        { UiFlavor.Windows, "light" },
        { UiFlavor.Windows, "dark" },
        { UiFlavor.Mac, "light" },
        { UiFlavor.Mac, "dark" },
        { UiFlavor.Linux, "light" },
        { UiFlavor.Linux, "dark" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public Task RendersLauncherWithSampleResults(UiFlavor flavor, string theme) =>
        fixture.Session.Dispatch(() =>
        {
            var vm = DesignData.Create(flavor);
            var frame = Render(flavor, vm, theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light, out var view);

            Assert.True(frame.PixelSize.Width >= 700, $"unexpected width {frame.PixelSize.Width}");
            Assert.True(frame.PixelSize.Height >= 400, $"unexpected height {frame.PixelSize.Height}");
            Assert.Equal(vm.Results.Count, CountRows(view));
            Save(frame, $"{flavor.ToString().ToLowerInvariant()}-{theme}.png");
        }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(UiFlavor.Windows)]
    [InlineData(UiFlavor.Mac)]
    [InlineData(UiFlavor.Linux)]
    public Task RendersModelMissingState(UiFlavor flavor) =>
        fixture.Session.Dispatch(() =>
        {
            var vm = DesignData.CreateModelMissing(flavor);
            var frame = Render(flavor, vm, ThemeVariant.Light, out _);
            Assert.True(frame.PixelSize.Width >= 700);
            Save(frame, $"{flavor.ToString().ToLowerInvariant()}-model-missing.png");
        }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(UiFlavor.Windows)]
    [InlineData(UiFlavor.Mac)]
    [InlineData(UiFlavor.Linux)]
    public Task RendersModelFailedState(UiFlavor flavor) =>
        fixture.Session.Dispatch(() =>
        {
            var vm = DesignData.CreateModelFailed(flavor);
            Assert.True(vm.IsModelFailedVisible);
            var frame = Render(flavor, vm, ThemeVariant.Light, out _);
            Assert.True(frame.PixelSize.Width >= 700);
            Save(frame, $"{flavor.ToString().ToLowerInvariant()}-model-failed.png");
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task RendersHotkeyFallbackBanner() =>
        fixture.Session.Dispatch(() =>
        {
            var vm = DesignData.Create(UiFlavor.Linux);
            vm.SetHotkey("Ctrl+Alt+Space", Valafind.App.Platform.GlobalHotkeyFactory.WaylandUnavailableMessage);
            var frame = Render(UiFlavor.Linux, vm, ThemeVariant.Light, out _);
            Assert.True(frame.PixelSize.Width >= 700);
            Save(frame, "linux-wayland-hotkey.png");
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task TogglingPreviewCollapsesTheExpandedRow() =>
        fixture.Session.Dispatch(() =>
        {
            var vm = DesignData.Create(UiFlavor.Windows);
            var expanded = Render(UiFlavor.Windows, vm, ThemeVariant.Light, out _);
            vm.TogglePreviewCommand.Execute(null);
            var compact = Render(UiFlavor.Windows, vm, ThemeVariant.Light, out _);
            Assert.True(compact.PixelSize.Height < expanded.PixelSize.Height);
            vm.MoveDownCommand.Execute(null);
            vm.TogglePreviewCommand.Execute(null);
            var second = Render(UiFlavor.Windows, vm, ThemeVariant.Light, out _);
            Save(second, "windows-second-selected.png");
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task RendersSettingsWindow() =>
        fixture.Session.Dispatch(() =>
        {
            var window = new SettingsWindow(DesignData.Settings) { RequestedThemeVariant = ThemeVariant.Light };
            window.Show();
            try
            {
                var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered");
                Assert.Equal(580, frame.PixelSize.Width);
                Save(frame, "settings-light.png");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    private static WriteableBitmap Render(UiFlavor flavor, LauncherViewModel vm, ThemeVariant theme, out LauncherViewBase view)
    {
        view = LauncherWindow.CreateView(flavor);
        view.DataContext = vm;
        var window = new Window
        {
            RequestedThemeVariant = theme,
            SizeToContent = SizeToContent.WidthAndHeight,
            // Stand-in for the desktop behind the translucent launcher (headless has no real blur).
            Background = Backdrop(flavor, theme == ThemeVariant.Dark),
            Content = view,
        };
        window.Show();
        try
        {
            return window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered");
        }
        finally
        {
            window.Close();
        }
    }

    private static LinearGradientBrush Backdrop(UiFlavor flavor, bool dark)
    {
        (string a, string b) = (flavor, dark) switch
        {
            (_, true) => ("#1E2733", "#121820"),
            (UiFlavor.Windows, _) => ("#DFE9F5", "#B3CBE7"),
            (UiFlavor.Mac, _) => ("#B9C9D8", "#E4DBCF"),
            _ => ("#2F5579", "#1D364F"),
        };
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse(a), 0), new GradientStop(Color.Parse(b), 1) },
        };
    }

    private static int CountRows(LauncherViewBase view) =>
        view.FindControl<ItemsControl>("ResultsList")?.ItemCount ?? -1;

    private static void Save(Bitmap frame, string name)
    {
        var dir = Environment.GetEnvironmentVariable(ScreenshotDirVariable);
        if (string.IsNullOrWhiteSpace(dir))
            return;
        Directory.CreateDirectory(dir);
        using var file = File.Create(Path.Combine(dir, name));
        frame.Save(file, new PngBitmapEncoderOptions());
    }
}
