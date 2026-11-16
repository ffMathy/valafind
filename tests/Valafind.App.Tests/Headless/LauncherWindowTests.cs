using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Valafind.App.ViewModels;
using Valafind.App.Views;
using Xunit;

namespace Valafind.App.Tests.Headless;

/// <summary>Keyboard behaviour of the real launcher window on the headless platform.</summary>
[Collection(HeadlessUiDefinition.Name)]
public sealed class LauncherWindowTests(HeadlessFixture fixture)
{
    [Theory]
    [InlineData(UiFlavor.Windows)]
    [InlineData(UiFlavor.Mac)]
    [InlineData(UiFlavor.Linux)]
    public Task KeyboardDrivesSelectionPreviewAndHiding(UiFlavor flavor) =>
        fixture.Session.Dispatch(() =>
        {
            var vm = DesignData.Create(flavor);
            var actions = new Actions();
            vm.Actions = actions;
            var window = new LauncherWindow(vm, flavor);
            try
            {
                Assert.False(vm.IsLauncherVisible);
                window.ShowLauncher();
                Assert.True(window.IsVisible);
                // The host makes indexing yield while this is true (IIndexingService.SetInteractive).
                Assert.True(vm.IsLauncherVisible);
                Assert.True(window.View.FindControl<TextBox>("SearchBox")!.IsFocused);

                window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                Assert.Equal(2, vm.SelectedIndex);
                window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
                Assert.Equal(1, vm.SelectedIndex);

                var preview = vm.IsPreviewVisible;
                window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                Assert.Equal(!preview, vm.IsPreviewVisible);

                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                Assert.Equal("open " + vm.Results[1].Path, actions.Opened);

                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.False(window.IsVisible);
                Assert.False(vm.IsLauncherVisible);
                Assert.True(actions.Hidden > 0);

                // Re-showing keeps the window alive; closing via the OS only hides it.
                window.ShowLauncher();
                window.Close();
                Assert.False(window.IsVisible);
            }
            finally
            {
                window.CloseForShutdown();
            }
        }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(UiFlavor.Windows)]
    [InlineData(UiFlavor.Mac)]
    [InlineData(UiFlavor.Linux)]
    public Task SurfaceStylesReachThePlatformViews(UiFlavor flavor) =>
        fixture.Session.Dispatch(() =>
        {
            // Each platform view is a subclass of LauncherViewBase; the surface styles must match it (a plain type
            // selector only matches the exact type, which once left blur/opaque styles dead on every OS).
            var view = LauncherWindow.CreateView(flavor);
            view.DataContext = DesignData.Create(flavor);
            var window = new Window { Content = view };
            try
            {
                window.Show();
                var card = view.FindControl<Border>("Card")!;
                Assert.NotEqual(0, card.Margin.Left); // Floating: room for the drop shadow.

                view.SetSurface(LauncherSurface.Opaque);
                Assert.Equal(new Avalonia.Thickness(0), card.Margin);
                Assert.Equal(new Avalonia.CornerRadius(0), card.CornerRadius);

                view.SetSurface(LauncherSurface.Blurred);
                Assert.Equal(new Avalonia.Thickness(0), card.Margin);

                view.SetSurface(LauncherSurface.Floating);
                Assert.NotEqual(0, card.Margin.Left);
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    private sealed class Actions : ILauncherActions
    {
        public string? Opened { get; private set; }
        public int Hidden { get; private set; }
        public void Open(string path) => Opened = "open " + path;
        public void Reveal(string path) { }
        public void QuickLook(string path) { }
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public void HideLauncher() => Hidden++;
        public void ShowSettings() { }
        public void Quit() { }
        public void SetUserPaused(bool paused) { }
        public void ResumeNow() { }
        public void CheckModel() { }
    }
}
