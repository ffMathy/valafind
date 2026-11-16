using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Threading;

namespace Valafind.App.Platform;

/// <summary>Carbon (HIToolbox) hot-key API. All functions must be called on the main thread.</summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class Carbon
{
    private const string Lib = "/System/Library/Frameworks/Carbon.framework/Carbon";

    public const uint kEventClassKeyboard = 0x6B657962;     // 'keyb'
    public const uint kEventHotKeyPressed = 5;
    public const uint kEventParamDirectObject = 0x2D2D2D2D; // '----'
    public const uint typeEventHotKeyID = 0x686B6964;       // 'hkid'
    public const uint Signature = 0x564C4644;               // 'VLFD'

    public const uint cmdKey = 0x0100;
    public const uint shiftKey = 0x0200;
    public const uint optionKey = 0x0800;
    public const uint controlKey = 0x1000;

    public const int noErr = 0;
    public const int eventNotHandledErr = -9874;
    public const int eventHotKeyExistsErr = -9878;
    public const int eventHotKeyInvalidErr = -9879;

    [StructLayout(LayoutKind.Sequential)]
    public struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventHotKeyID
    {
        public uint Signature;
        public uint Id;
    }

    [LibraryImport(Lib)]
    public static partial nint GetApplicationEventTarget();

    [LibraryImport(Lib)]
    public static partial int InstallEventHandler(
        nint target,
        delegate* unmanaged[Cdecl]<nint, nint, nint, int> handler,
        nuint numTypes,
        EventTypeSpec* typeList,
        nint userData,
        out nint handlerRef);

    [LibraryImport(Lib)]
    public static partial int RemoveEventHandler(nint handlerRef);

    [LibraryImport(Lib)]
    public static partial int RegisterEventHotKey(
        uint hotKeyCode, uint hotKeyModifiers, EventHotKeyID hotKeyId, nint target, uint options, out nint hotKeyRef);

    [LibraryImport(Lib)]
    public static partial int UnregisterEventHotKey(nint hotKeyRef);

    [LibraryImport(Lib)]
    public static partial int GetEventParameter(
        nint inEvent, uint name, uint desiredType, uint* outActualType, nuint bufferSize, nuint* outActualSize, void* outData);
}

/// <summary>Objective-C runtime entry points used for NSApplication activation.</summary>
[SupportedOSPlatform("macos")]
internal static partial class ObjC
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint objc_getClass(string name);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint sel_registerName(string name);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial nint SendPtr(nint receiver, nint selector);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial void SendVoid(nint receiver, nint selector);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SendBool(nint receiver, nint selector);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SendBoolPtr(nint receiver, nint selector, nint arg);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.U1)] bool arg);

    /// <summary>True when the calling thread is the process main thread ([NSThread isMainThread]).</summary>
    public static bool IsMainThread()
    {
        var nsThread = objc_getClass("NSThread");
        return nsThread != 0 && SendBool(nsThread, sel_registerName("isMainThread"));
    }

    /// <summary>The shared NSApplication instance (creating it if needed).</summary>
    public static nint SharedApplication()
    {
        var cls = objc_getClass("NSApplication");
        return cls == 0 ? 0 : SendPtr(cls, sel_registerName("sharedApplication"));
    }

    public static bool RespondsTo(nint receiver, string selector) =>
        receiver != 0 && SendBoolPtr(receiver, sel_registerName("respondsToSelector:"), sel_registerName(selector));
}

/// <summary>Runs AppKit/Carbon work on the main thread, hopping through Avalonia's UI dispatcher when needed.</summary>
[SupportedOSPlatform("macos")]
internal static class MacMainThread
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Runs <paramref name="func"/> synchronously on the main thread and returns its result.</summary>
    /// <exception cref="InvalidOperationException">The main thread could not be reached.</exception>
    public static T Run<T>(Func<T> func)
    {
        if (ObjC.IsMainThread())
            return func();

        var dispatcher = Dispatcher.UIThread;
        if (dispatcher.CheckAccess())
            throw new InvalidOperationException("Avalonia's UI thread is not the macOS main thread.");

        var task = dispatcher.InvokeAsync(() =>
        {
            if (!ObjC.IsMainThread())
                throw new InvalidOperationException("Avalonia's UI thread is not the macOS main thread.");
            return func();
        }).GetTask();

        if (!task.Wait(Timeout))
            throw new InvalidOperationException("Timed out waiting for the UI thread. Is the Avalonia application running?");
        return task.Result;
    }

    /// <summary>Runs <paramref name="action"/> now when on the main thread, otherwise queues it to the UI thread.</summary>
    public static void Post(Action action)
    {
        if (ObjC.IsMainThread())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (ObjC.IsMainThread())
                action();
        });
    }
}
