using Avalonia.VisualTree;
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

                // Mica is a blurred surface too (edge to edge, no shadow margin).
                view.SetSurface(LauncherSurface.Mica);
                Assert.Equal(new Avalonia.Thickness(0), card.Margin);
                Assert.True(view.Classes.Contains("blurred") && view.Classes.Contains("mica"));

                view.SetSurface(LauncherSurface.Floating);
                Assert.NotEqual(0, card.Margin.Left);
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task WindowsCardIsTheMicaMaterialAndStripsBecomeLayers() =>
        fixture.Session.Dispatch(() =>
        {
            // Under Mica the card must not paint over the material (it did at ~93% opacity), and the strips use
            // translucent Fluent layer fills; they used to set Background locally, which no surface style could change.
            var view = LauncherWindow.CreateView(UiFlavor.Windows);
            view.DataContext = DesignData.Create(UiFlavor.Windows);
            var window = new Window { Content = view };
            try
            {
                window.Show();
                var card = view.FindControl<Border>("Card")!;
                var pane = view.FindControl<Border>("DetailsPane")!;

                view.SetSurface(LauncherSurface.Floating);
                var opaqueCard = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(card.Background).Color;
                var floatingStrip = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(pane.Background).Color;
                Assert.Equal(255, opaqueCard.A);

                view.SetSurface(LauncherSurface.Mica);
                Assert.Equal(Avalonia.Media.Colors.Transparent, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(card.Background).Color);
                var micaStrip = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(pane.Background).Color;
                Assert.NotEqual(floatingStrip, micaStrip);
                Assert.True(micaStrip.A < 255, "Mica layers are translucent");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task LinuxDetailsPaneFollowsSelectionAndTabHidesIt() =>
        fixture.Session.Dispatch(() =>
        {
            var vm = DesignData.Create(UiFlavor.Linux);
            var view = LauncherWindow.CreateView(UiFlavor.Linux);
            view.DataContext = vm;
            var window = new Window { Content = view };
            try
            {
                window.Show();
                vm.MoveDownCommand.Execute(null);
                var pane = view.FindControl<Border>("DetailsPane")!;
                Assert.True(pane.IsVisible);
                Assert.Same(vm.Results[1], ((Control)pane.Child!).DataContext);

                vm.TogglePreviewCommand.Execute(null);
                Assert.False(pane.IsVisible);

                // No results (hint / no-results / model messages): no empty pane beside the message.
                vm.TogglePreviewCommand.Execute(null);
                vm.ClearQueryCommand.Execute(null);
                Assert.False(vm.IsResultsVisible);
                Assert.False(pane.IsEffectivelyVisible);
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(UiFlavor.Windows, true)]
    [InlineData(UiFlavor.Mac, true)]
    [InlineData(UiFlavor.Linux, false)] // GNOME has the paused banner with the same button instead.
    public Task EveryLauncherShowsIndexPhasesAndPressure(UiFlavor flavor, bool hasOwnResumeButton) =>
        fixture.Session.Dispatch(() =>
        {
            var vm = DesignData.Create(flavor);
            var view = LauncherWindow.CreateView(flavor);
            view.DataContext = vm;
            var window = new Window { Content = view };
            try
            {
                window.Show();
                var panel = Assert.Single(view.GetVisualDescendants().OfType<Valafind.App.Views.Common.IndexPanelView>());
                Assert.True(panel.IsEffectivelyVisible);
                var texts = panel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Inlines?.Text ?? t.Text).ToList();
                Assert.Contains(texts, t => t?.Contains("Phase 1", StringComparison.Ordinal) == true);
                Assert.Contains(texts, t => t?.Contains("Phase 2", StringComparison.Ordinal) == true);
                Assert.Contains(vm.Status.AllPercentText, texts);
                Assert.Contains(vm.Status.CpuText, texts);

                // The button appears only while indexing is paused, and only where the view has no banner for it.
                var resume = panel.FindControl<Button>("ResumeButton")!;
                Assert.Equal(hasOwnResumeButton && vm.Status.ShowBanner, resume.IsEffectivelyVisible);
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(UiFlavor.Windows)]
    [InlineData(UiFlavor.Mac)]
    [InlineData(UiFlavor.Linux)]
    public Task UpToDateIndexIsAQuietCheckInsteadOfThePanel(UiFlavor flavor) =>
        fixture.Session.Dispatch(() =>
        {
            var vm = DesignData.Create(flavor);
            vm.UpdateStatus(new Valafind.Core.Abstractions.IndexStatus(Valafind.Core.Abstractions.IndexingState.UpToDate,
                Valafind.Core.Abstractions.IndexPhase.Incremental, 6214, 6214, 446120, 446120, true, null, null));
            var view = LauncherWindow.CreateView(flavor);
            view.DataContext = vm;
            var window = new Window { Content = view };
            try
            {
                window.Show();
                var panel = Assert.Single(view.GetVisualDescendants().OfType<Valafind.App.Views.Common.IndexPanelView>());
                // Hidden as a whole (not just its content), so the margin around it goes too and the body meets the footer.
                Assert.False(panel.IsVisible, "panel hidden when up to date");

                var texts = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible)
                    .Select(t => t.Inlines?.Text ?? t.Text ?? string.Empty).ToList();
                Assert.Contains("Up to date", texts);
                Assert.DoesNotContain(texts, t => t.Contains("On-device", StringComparison.Ordinal));
                Assert.Equal("Index up to date · 446,120 files searchable", vm.Status.UpToDateDetail);

                // Back to indexing: the panel returns and the quiet check goes away.
                vm.UpdateStatus(new Valafind.Core.Abstractions.IndexStatus(Valafind.Core.Abstractions.IndexingState.Running,
                    Valafind.Core.Abstractions.IndexPhase.All, 6214, 6214, 1000, 446120, true, null, null));
                Assert.True(panel.IsVisible);
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
