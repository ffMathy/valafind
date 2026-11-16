using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Valafind.App.Platform;

/// <summary>Minimal libX11 surface for passive key grabs.</summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("freebsd")]
internal static unsafe partial class Xlib
{
    private const string Lib = "libX11.so.6";

    public const int KeyPress = 2;
    public const int KeyRelease = 3;
    public const int GrabModeAsync = 1;
    public const byte BadAccess = 10;

    public const uint ShiftMask = 1 << 0;
    public const uint LockMask = 1 << 1;
    public const uint ControlMask = 1 << 2;
    public const uint Mod1Mask = 1 << 3; // Alt
    public const uint Mod2Mask = 1 << 4; // NumLock
    public const uint Mod4Mask = 1 << 6; // Super

    /// <summary>XErrorEvent (Xlib.h). Field order and natural alignment match the C layout on LP64 and ILP32.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct XErrorEvent
    {
        public int type;
        public nint display;
        public nuint resourceid;
        public nuint serial;
        public byte error_code;
        public byte request_code;
        public byte minor_code;
    }

    /// <summary>XKeyEvent (Xlib.h).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct XKeyEvent
    {
        public int type;
        public nuint serial;
        public int send_event;
        public nint display;
        public nuint window;
        public nuint root;
        public nuint subwindow;
        public nuint time;
        public int x, y, x_root, y_root;
        public uint state;
        public uint keycode;
        public int same_screen;
    }

    /// <summary>XEvent is a union padded to 24 longs.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct XEvent
    {
        public fixed long pad[24];
    }

    [LibraryImport(Lib)]
    public static partial int XInitThreads();

    [LibraryImport(Lib)]
    public static partial nint XOpenDisplay(nint displayName);

    [LibraryImport(Lib)]
    public static partial int XCloseDisplay(nint display);

    [LibraryImport(Lib)]
    public static partial nuint XDefaultRootWindow(nint display);

    [LibraryImport(Lib)]
    public static partial int XConnectionNumber(nint display);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nuint XStringToKeysym(string name);

    [LibraryImport(Lib)]
    public static partial byte XKeysymToKeycode(nint display, nuint keysym);

    [LibraryImport(Lib)]
    public static partial int XGrabKey(nint display, int keycode, uint modifiers, nuint grabWindow, int ownerEvents, int pointerMode, int keyboardMode);

    [LibraryImport(Lib)]
    public static partial int XUngrabKey(nint display, int keycode, uint modifiers, nuint grabWindow);

    [LibraryImport(Lib)]
    public static partial int XSync(nint display, int discard);

    [LibraryImport(Lib)]
    public static partial int XFlush(nint display);

    [LibraryImport(Lib)]
    public static partial int XPending(nint display);

    [LibraryImport(Lib)]
    public static partial int XNextEvent(nint display, XEvent* ev);

    [LibraryImport(Lib)]
    public static partial nint XSetErrorHandler(delegate* unmanaged[Cdecl]<nint, XErrorEvent*, int> handler);

    [LibraryImport(Lib)]
    public static partial int XkbSetDetectableAutoRepeat(nint display, int detectable, int* supported);
}

/// <summary>libc calls used to wait on the X connection and a wake-up pipe.</summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("freebsd")]
internal static unsafe partial class LibC
{
    private const string Lib = "libc";

    public const short POLLIN = 0x001;
    public const int EINTR = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    [LibraryImport(Lib, EntryPoint = "poll", SetLastError = true)]
    public static partial int Poll(PollFd* fds, nuint count, int timeoutMs);

    [LibraryImport(Lib, EntryPoint = "pipe", SetLastError = true)]
    public static partial int Pipe(int* fds);

    [LibraryImport(Lib, EntryPoint = "read", SetLastError = true)]
    public static partial nint Read(int fd, void* buffer, nuint count);

    [LibraryImport(Lib, EntryPoint = "write", SetLastError = true)]
    public static partial nint Write(int fd, void* buffer, nuint count);

    [LibraryImport(Lib, EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int fd);
}
