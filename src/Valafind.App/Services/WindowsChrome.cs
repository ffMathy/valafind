using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Valafind.App.Services;

/// <summary>Windows 11 DWM tweaks for the borderless launcher (rounded corners when the window is blurred).</summary>
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

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
