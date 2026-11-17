using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Valafind.App.Services;

/// <summary>Windows 11 DWM tweaks: rounded corners and the system-drawn Mica backdrop.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsChrome
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    /// <summary>Asks DWM to round the corners of <paramref name="hwnd"/>. No-op before Windows 11.</summary>
    public static void TryRoundCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return;
        try
        {
            var preference = DwmwcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Older Windows: nothing to do.
        }
    }

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtMainWindow = 2; // Mica
    private const int DwmsbtAuto = 0;

    /// <summary>
    /// Turns on the backdrop Windows itself draws for Settings and other native windows (DWMWA_SYSTEMBACKDROP_TYPE =
    /// DWMSBT_MAINWINDOW, Windows 11 22H2+): extends the frame over the whole client area (so DWM paints the material
    /// behind our transparent pixels, also for a caption-less window) and sets light/dark to match the app theme.
    /// Returns false when Windows refuses; the caller then falls back to Avalonia's own Mica.
    /// </summary>
    public static bool TryApplySystemMica(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, Platform.WindowsBackdropPolicy.SystemBackdropMinBuild))
            return false;
        try
        {
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            if (DwmExtendFrameIntoClientArea(hwnd, ref margins) < 0)
                return false;
            SetDarkMode(hwnd, dark);
            var backdrop = DwmsbtMainWindow;
            return DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) >= 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Undoes <see cref="TryApplySystemMica"/> before falling back to another backdrop.</summary>
    public static void RemoveSystemBackdrop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, Platform.WindowsBackdropPolicy.SystemBackdropMinBuild))
            return;
        try
        {
            var backdrop = DwmsbtAuto;
            _ = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
            var margins = default(Margins);
            _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>Light or dark system backdrop and frame (DWMWA_USE_IMMERSIVE_DARK_MODE).</summary>
    public static void SetDarkMode(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return;
        try
        {
            var value = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
}
