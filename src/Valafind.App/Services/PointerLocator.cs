using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Valafind.App.Services;

/// <summary>A global pointer position and the coordinate space it is expressed in.</summary>
/// <param name="X">Horizontal position.</param>
/// <param name="Y">Vertical position (top-left origin).</param>
/// <param name="InPhysicalPixels">True for device pixels (Windows, X11); false for logical points (macOS).</param>
internal readonly record struct PointerPosition(double X, double Y, bool InPhysicalPixels);

/// <summary>
/// Reads the global mouse position so the launcher opens on the monitor the user is looking at. Avalonia has no
/// cross-platform API for this; Wayland deliberately offers none, in which case callers fall back to another screen.
/// </summary>
internal static partial class PointerLocator
{
    /// <summary>Returns the pointer position, or null when the platform cannot tell.</summary>
    public static PointerPosition? TryGetPosition()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return Windows();
            if (OperatingSystem.IsMacOS())
                return Mac();
            if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
                return X11();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException or BadImageFormatException)
        {
            // Missing native library (e.g. no libX11 on a pure Wayland system): fall back.
        }
        return null;
    }

    // ------------------------------------------------------------------ Windows

    [SupportedOSPlatform("windows")]
    private static PointerPosition? Windows() =>
        GetCursorPos(out var p) ? new PointerPosition(p.X, p.Y, true) : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static partial bool GetCursorPos(out Point32 point);

    // ------------------------------------------------------------------ macOS

    [SupportedOSPlatform("macos")]
    private static PointerPosition? Mac()
    {
        // CGEventCreate(NULL) + CGEventGetLocation: global display coordinates in points, top-left origin.
        var ev = CGEventCreate(IntPtr.Zero);
        if (ev == IntPtr.Zero)
            return null;
        try
        {
            var p = CGEventGetLocation(ev);
            return new PointerPosition(p.X, p.Y, false);
        }
        finally
        {
            CFRelease(ev);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X;
        public double Y;
    }

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [LibraryImport(CoreGraphics)]
    [SupportedOSPlatform("macos")]
    private static partial IntPtr CGEventCreate(IntPtr source);

    [LibraryImport(CoreGraphics)]
    [SupportedOSPlatform("macos")]
    private static partial CGPoint CGEventGetLocation(IntPtr ev);

    [LibraryImport(CoreFoundation)]
    [SupportedOSPlatform("macos")]
    private static partial void CFRelease(IntPtr cf);

    // ------------------------------------------------------------------ X11 (also XWayland)

    private static PointerPosition? X11()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            return null;
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
            return null;
        try
        {
            var root = XDefaultRootWindow(display);
            return XQueryPointer(display, root, out _, out _, out var x, out var y, out _, out _, out _) != 0
                ? new PointerPosition(x, y, true)
                : null;
        }
        finally
        {
            _ = XCloseDisplay(display);
        }
    }

    private const string LibX11 = "libX11.so.6";

    [LibraryImport(LibX11)]
    private static partial IntPtr XOpenDisplay(IntPtr name);

    [LibraryImport(LibX11)]
    private static partial int XCloseDisplay(IntPtr display);

    [LibraryImport(LibX11)]
    private static partial IntPtr XDefaultRootWindow(IntPtr display);

    [LibraryImport(LibX11)]
    private static partial int XQueryPointer(IntPtr display, IntPtr window, out IntPtr root, out IntPtr child,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);
}
