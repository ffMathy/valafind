using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using Valafind.App.Services;
using Valafind.App.ViewModels;
using Valafind.App.Views.Linux;
using Valafind.App.Views.Mac;
using Valafind.App.Views.Windows;

namespace Valafind.App.Views;

/// <summary>
/// The launcher window: borderless, topmost, translucent where the OS allows it, centred horizontally on the screen
/// under the pointer at ~22% from the top. Hides on deactivation and Escape; keyboard drives the result list.
/// </summary>
public partial class LauncherWindow : Window
{
    private static readonly TimeSpan DeactivateGrace = TimeSpan.FromMilliseconds(300);

    private readonly LauncherViewModel _vm;
    private readonly LauncherViewBase _view;
    private DateTime _ignoreDeactivateUntil;
    private bool _allowClose;

    /// <summary>Designer constructor.</summary>
    public LauncherWindow()
        : this(DesignData.Create(UiFlavors.Detect()), UiFlavors.Detect())
    {
    }

    /// <summary>Creates the window for <paramref name="vm"/> using the design for <paramref name="flavor"/>.</summary>
    public LauncherWindow(LauncherViewModel vm, UiFlavor flavor)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        InitializeComponent();
        DataContext = vm;
        _view = CreateView(flavor);
        Content = _view;

        TransparencyLevelHint = OperatingSystem.IsWindows()
            ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Mica, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent]
            : [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent];
        try
        {
            Icon = TrayIconController.LoadIcon("valafind.png");
        }
        catch (Exception e) when (e is IOException or FileNotFoundException)
        {
            // Icon is cosmetic.
        }

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Deactivated += OnDeactivated;
        Opened += (_, _) => ApplySurface();
    }

    /// <summary>The platform view hosted by the window.</summary>
    public LauncherViewBase View => _view;

    /// <summary>Creates the view for a flavor.</summary>
    public static LauncherViewBase CreateView(UiFlavor flavor) => flavor switch
    {
        UiFlavor.Windows => new WindowsLauncherView(),
        UiFlavor.Mac => new MacLauncherView(),
        _ => new LinuxLauncherView(),
    };

    /// <summary>Shows, positions and focuses the launcher.</summary>
    public void ShowLauncher()
    {
        _ignoreDeactivateUntil = DateTime.UtcNow + DeactivateGrace;
        if (!IsVisible)
        {
            PositionOnActiveScreen();
            Show();
        }
        Activate();
        _vm.OnShown();
        _view.FocusSearchBox();
        // Some window managers only accept focus once the window is mapped.
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible)
                return;
            Activate();
            _view.FocusSearchBox();
        }, DispatcherPriority.Input);
    }

    /// <summary>Hides the launcher (it stays alive for instant re-show).</summary>
    public void HideLauncher()
    {
        if (IsVisible)
            Hide();
    }

    /// <summary>Closes the window for real (application exit).</summary>
    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Alt+F4 / Cmd+W hide the launcher instead of destroying it.
        if (!_allowClose)
        {
            e.Cancel = true;
            HideLauncher();
        }
        base.OnClosing(e);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ActualTransparencyLevelProperty)
            ApplySurface();
        else if (change.Property == IsVisibleProperty)
            _vm.IsLauncherVisible = change.GetNewValue<bool>();
    }

    private void ApplySurface()
    {
        var level = ActualTransparencyLevel;
        var surface = level == WindowTransparencyLevel.None ? LauncherSurface.Opaque
            : level == WindowTransparencyLevel.Transparent ? LauncherSurface.Floating
            : LauncherSurface.Blurred;
        _view.SetSurface(surface);
        if (surface == LauncherSurface.Blurred && OperatingSystem.IsWindows() && TryGetPlatformHandle() is { } handle)
            WindowsChrome.TryRoundCorners(handle.Handle);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow < _ignoreDeactivateUntil)
            return;
        HideLauncher();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var command = Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        var mods = e.KeyModifiers;
        switch (e.Key)
        {
            case Key.Escape when mods == KeyModifiers.None:
                _vm.HideCommand.Execute(null);
                HideLauncher();
                e.Handled = true;
                break;
            case Key.Enter when mods == KeyModifiers.None:
                _vm.OpenSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter when mods == command:
                _vm.RevealSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up when mods == KeyModifiers.None:
                _vm.MoveUpCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Down when mods == KeyModifiers.None:
                _vm.MoveDownCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Tab when mods == KeyModifiers.None:
                _vm.TogglePreviewCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.C when mods == command && _vm.HasSelection && !_view.SearchBoxHasSelection:
                _vm.CopyPathCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.OemComma when mods == command:
                _vm.OpenSettingsCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    // ------------------------------------------------------------------ placement

    private void PositionOnActiveScreen()
    {
        var screens = Screens;
        var all = screens.All;
        if (all.Count == 0)
            return;

        Screen? screen = null;
        if (PointerLocator.TryGetPosition() is { } pointer)
            screen = all.FirstOrDefault(s => Contains(s, pointer));
        screen ??= screens.ScreenFromWindow(this) ?? screens.Primary ?? all[0];

        _view.Measure(Size.Infinity);
        var desired = _view.DesiredSize;
        var cardTop = _view.FindControl<Border>("Card")?.Margin.Top ?? 0;
        var position = ComputePosition(screen.WorkingArea, screen.Scaling, desired, cardTop);
        Position = position;
    }

    /// <summary>Centred horizontally, card top at 22% of the working area, clamped to stay on screen.</summary>
    internal static PixelPoint ComputePosition(PixelRect workingArea, double scaling, Size windowSize, double cardTopMargin)
    {
        var w = (int)Math.Ceiling(windowSize.Width * scaling);
        var h = (int)Math.Ceiling(windowSize.Height * scaling);
        var x = workingArea.X + (workingArea.Width - w) / 2;
        var y = workingArea.Y + (int)Math.Round(workingArea.Height * 0.22 - cardTopMargin * scaling);
        y = Math.Min(y, workingArea.Bottom - h);
        y = Math.Max(y, workingArea.Y);
        x = Math.Max(workingArea.X, Math.Min(x, workingArea.Right - w));
        return new PixelPoint(x, y);
    }

    private static bool Contains(Screen screen, PointerPosition p)
    {
        if (p.InPhysicalPixels)
            return screen.Bounds.Contains(new PixelPoint((int)p.X, (int)p.Y));
        // macOS reports points: compare against the screen rectangle in points.
        var s = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var b = screen.Bounds;
        return p.X >= b.X / s && p.X < (b.X + b.Width) / s && p.Y >= b.Y / s && p.Y < (b.Y + b.Height) / s;
    }
}
