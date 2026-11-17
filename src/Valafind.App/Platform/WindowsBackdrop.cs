using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Valafind.App.Services;

namespace Valafind.App.Platform;

/// <summary>Which backdrop a Valafind window ended up with on Windows.</summary>
internal enum BackdropKind
{
    /// <summary>No transparency at all: draw an opaque window.</summary>
    None,
    /// <summary>Per-pixel transparency without blur.</summary>
    Transparent,
    /// <summary>Avalonia's Gaussian blur or acrylic (Windows 10).</summary>
    Blur,
    /// <summary>Mica drawn by Avalonia's composition layer.</summary>
    AvaloniaMica,
    /// <summary>Mica drawn by Windows itself (DWM system backdrop), exactly as in Settings.</summary>
    SystemMica,
}

/// <summary>The order Valafind tries backdrops in on Windows, as pure, testable decisions.</summary>
internal static class WindowsBackdropPolicy
{
    /// <summary>DWMWA_SYSTEMBACKDROP_TYPE exists from Windows 11 22H2 (build 22621).</summary>
    public const int SystemBackdropMinBuild = 22621;

    /// <summary>While Windows draws the Mica, Avalonia only has to render transparent pixels.</summary>
    public static IReadOnlyList<WindowTransparencyLevel> SystemMicaHint { get; } = [WindowTransparencyLevel.Transparent];

    /// <summary>Second and later choices: Avalonia's Mica, then acrylic, blur, plain transparency (else opaque).</summary>
    public static IReadOnlyList<WindowTransparencyLevel> AvaloniaChain { get; } =
        [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent];

    /// <summary>Whether to try Windows' own Mica first on this Windows build.</summary>
    public static bool PrefersSystemMica(int windowsBuild) => windowsBuild >= SystemBackdropMinBuild;

    /// <summary>What Avalonia's achieved level means when Windows does not draw the backdrop.</summary>
    public static BackdropKind FromAvalonia(WindowTransparencyLevel level) =>
        level == WindowTransparencyLevel.Mica ? BackdropKind.AvaloniaMica
        : level == WindowTransparencyLevel.AcrylicBlur || level == WindowTransparencyLevel.Blur ? BackdropKind.Blur
        : level == WindowTransparencyLevel.Transparent ? BackdropKind.Transparent
        : BackdropKind.None;

    /// <summary>
    /// One step of the controller. <paramref name="systemMode"/>: still trying Windows' own Mica. Returns the kind now
    /// in effect and whether to give up on system Mica and switch to <see cref="AvaloniaChain"/>.
    /// <paramref name="tryApplySystem"/> is called only when the window is ready for it (transparent and created).
    /// </summary>
    public static (BackdropKind Kind, bool FallBack) Decide(bool systemMode, WindowTransparencyLevel actual, bool hasHandle,
        Func<bool> tryApplySystem)
    {
        if (!systemMode)
            return (FromAvalonia(actual), false);
        if (!hasHandle)
            return (BackdropKind.None, false); // Not created yet: decide once the window exists.
        if (actual != WindowTransparencyLevel.Transparent)
            return (FromAvalonia(actual), true); // Avalonia cannot even render transparent pixels here.
        return tryApplySystem() ? (BackdropKind.SystemMica, false) : (BackdropKind.Transparent, true);
    }
}

/// <summary>
/// Gives a window the best backdrop Windows offers: Windows' own Mica (DWM system backdrop, Windows 11 22H2+), else
/// Avalonia's Mica, else acrylic / blur / transparency, else opaque. <see cref="KindChanged"/> tells the window which
/// one it got so it can style its surface. Avalonia resets the DWM frame margins on show and state changes, so the
/// system backdrop is re-applied from a WndProc hook after those.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsBackdropController
{
    private const uint WmActivate = 0x0006;
    private const uint WmShowWindow = 0x0018;
    private const uint WmWindowPosChanged = 0x0047;
    private const uint WmThemeChanged = 0x031A;
    private const uint WmDwmCompositionChanged = 0x031E;

    private readonly Window _window;
    private bool _systemMode;
    private bool _reapplyQueued;

    public WindowsBackdropController(Window window, int windowsBuild)
    {
        _window = window;
        _systemMode = WindowsBackdropPolicy.PrefersSystemMica(windowsBuild);
        _window.TransparencyLevelHint = _systemMode ? WindowsBackdropPolicy.SystemMicaHint : WindowsBackdropPolicy.AvaloniaChain;
        _window.PropertyChanged += (_, e) =>
        {
            if (e.Property == TopLevel.ActualTransparencyLevelProperty)
                Evaluate();
        };
        _window.ActualThemeVariantChanged += (_, _) =>
        {
            if (Kind == BackdropKind.SystemMica)
                Reapply(); // Light/dark Mica follows the app theme.
        };
        _window.Opened += (_, _) => Evaluate();
        Win32Properties.AddWndProcHookCallback(_window, Hook);
    }

    /// <summary>The backdrop currently in effect.</summary>
    public BackdropKind Kind { get; private set; } = BackdropKind.None;

    /// <summary>Raised on the UI thread whenever <see cref="Kind"/> changes.</summary>
    public event EventHandler? KindChanged;

    private nint Handle => _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    private bool IsDark => _window.ActualThemeVariant == ThemeVariant.Dark;

    private void Evaluate()
    {
        var handle = Handle;
        var (kind, fallBack) = WindowsBackdropPolicy.Decide(_systemMode, _window.ActualTransparencyLevel, handle != IntPtr.Zero,
            () => WindowsChrome.TryApplySystemMica(handle, IsDark));
        if (fallBack)
        {
            _systemMode = false;
            WindowsChrome.RemoveSystemBackdrop(handle);
            _window.TransparencyLevelHint = WindowsBackdropPolicy.AvaloniaChain; // ActualTransparencyLevel change re-evaluates.
        }
        SetKind(kind);
    }

    private void SetKind(BackdropKind kind)
    {
        if (Kind == kind)
            return;
        Kind = kind;
        KindChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Reapply()
    {
        _reapplyQueued = false;
        if (Kind == BackdropKind.SystemMica && !WindowsChrome.TryApplySystemMica(Handle, IsDark))
            Evaluate();
    }

    private nint Hook(nint hwnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        // Runs before Avalonia handles the message; re-apply after it, once per burst of messages.
        if (Kind == BackdropKind.SystemMica && !_reapplyQueued &&
            msg is WmActivate or WmShowWindow or WmWindowPosChanged or WmThemeChanged or WmDwmCompositionChanged)
        {
            _reapplyQueued = true;
            Dispatcher.UIThread.Post(Reapply, DispatcherPriority.Background);
        }
        return IntPtr.Zero;
    }
}
