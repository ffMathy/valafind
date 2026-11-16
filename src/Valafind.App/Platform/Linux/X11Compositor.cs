using System.Runtime.Versioning;

namespace Valafind.App.Platform;

/// <summary>
/// Tells whether an X11 compositing manager is running. Without one, X11 ignores window alpha: Avalonia still reports
/// a transparent window, but the "transparent" shadow margin around the launcher card is painted as a solid box. The
/// launcher then switches to its opaque, margin-less surface.
/// </summary>
internal static class X11Compositor
{
    /// <summary>
    /// True/false on an X11 session (per the EWMH convention, a compositor owns the <c>_NET_WM_CM_S&lt;screen&gt;</c>
    /// selection); null when not on X11 or the display cannot be queried (callers keep their default).
    /// Wayland always composites, so it returns null there too.
    /// </summary>
    public static bool? IsCompositing()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD())
            return null;
        if (GlobalHotkeyFactory.DetectLinuxSession(Environment.GetEnvironmentVariable) != GlobalHotkeyFactory.LinuxSession.X11)
            return null;
        return Query();
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("freebsd")]
    private static bool? Query()
    {
        try
        {
            // A short-lived private connection: never touches Avalonia's own Display.
            var display = Xlib.XOpenDisplay(0);
            if (display == 0)
                return null;
            try
            {
                var screen = Xlib.XDefaultScreen(display);
                var atom = Xlib.XInternAtom(display, "_NET_WM_CM_S" + screen.ToString(System.Globalization.CultureInfo.InvariantCulture), 0);
                return atom != 0 && Xlib.XGetSelectionOwner(display, atom) != 0;
            }
            finally
            {
                _ = Xlib.XCloseDisplay(display);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}
