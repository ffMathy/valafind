using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Valafind.App.Platform;

/// <summary>A bookmark resolved back to its folder, which the process can now read.</summary>
/// <param name="Path">Where the folder is now (it may have been moved or renamed).</param>
/// <param name="RefreshedBookmark">A new bookmark to store instead of the old one when macOS reported the old one as
/// stale, otherwise null.</param>
internal sealed record ResolvedBookmark(string Path, string? RefreshedBookmark);

/// <summary>Security-scoped bookmarks: how a sandboxed app keeps access to folders the user picked.</summary>
internal interface IFolderBookmarks
{
    /// <summary>A bookmark (base64) for <paramref name="path"/>, which the process must be able to read now (the user
    /// just picked it in an open panel); null when macOS refuses.</summary>
    string? Create(string path);

    /// <summary>Resolves <paramref name="bookmark"/> and starts accessing the folder for the rest of the process's life;
    /// null when the folder is gone or access is refused.</summary>
    ResolvedBookmark? Resolve(string bookmark);
}

/// <summary>
/// <see cref="IFolderBookmarks"/> over Foundation's <c>NSURL</c>: read-only, app-scoped bookmarks (the entitlements
/// <c>com.apple.security.files.user-selected.read-only</c> and <c>com.apple.security.files.bookmarks.app-scope</c>).
/// Foundation's URL and data classes are thread-safe, so this needs no main thread.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacFolderBookmarks : IFolderBookmarks
{
    private const nuint CreationWithSecurityScope = 1 << 11;
    private const nuint CreationSecurityScopeAllowOnlyReadAccess = 1 << 12;
    private const nuint ResolutionWithSecurityScope = 1 << 10;

    public string? Create(string path)
    {
        var pool = Foundation.objc_autoreleasePoolPush();
        try
        {
            var url = Foundation.FileUrl(path);
            return url == 0 ? null : BookmarkOf(url);
        }
        finally
        {
            Foundation.objc_autoreleasePoolPop(pool);
        }
    }

    public ResolvedBookmark? Resolve(string bookmark)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(bookmark);
        }
        catch (FormatException)
        {
            return null;
        }

        if (bytes.Length == 0)
            return null;

        var pool = Foundation.objc_autoreleasePoolPush();
        try
        {
            nint data;
            unsafe
            {
                fixed (byte* pointer = bytes)
                {
                    data = Foundation.SendPtrPtrNuint(Foundation.Class("NSData"), Foundation.Selector("dataWithBytes:length:"),
                        (nint)pointer, (nuint)bytes.Length);
                }
            }

            if (data == 0)
                return null;
            byte stale = 0;
            nint error = 0;
            var url = Foundation.ResolveBookmark(Foundation.Class("NSURL"),
                Foundation.Selector("URLByResolvingBookmarkData:options:relativeToURL:bookmarkDataIsStale:error:"),
                data, ResolutionWithSecurityScope, 0, ref stale, ref error);
            if (url == 0 || !ObjC.SendBool(url, Foundation.Selector("startAccessingSecurityScopedResource")))
                return null;

            // Access lasts until stopAccessingSecurityScopedResource, which Valafind never calls: it reads the folder
            // for as long as it runs. Keep the URL alive past the pool, as Apple's guidance pairs access with it.
            _ = ObjC.SendPtr(url, Foundation.Selector("retain"));
            var path = Foundation.PathOf(url);
            if (string.IsNullOrEmpty(path))
                return null;
            return new ResolvedBookmark(path, stale != 0 ? BookmarkOf(url) : null);
        }
        finally
        {
            Foundation.objc_autoreleasePoolPop(pool);
        }
    }

    private static string? BookmarkOf(nint url)
    {
        nint error = 0;
        var data = Foundation.CreateBookmark(url,
            Foundation.Selector("bookmarkDataWithOptions:includingResourceValuesForKeys:relativeToURL:error:"),
            CreationWithSecurityScope | CreationSecurityScopeAllowOnlyReadAccess, 0, 0, ref error);
        if (data == 0)
            return null;
        var length = (int)Foundation.SendNuint(data, Foundation.Selector("length"));
        var bytes = ObjC.SendPtr(data, Foundation.Selector("bytes"));
        if (length <= 0 || bytes == 0)
            return null;
        var copy = new byte[length];
        Marshal.Copy(bytes, copy, 0, length);
        return Convert.ToBase64String(copy);
    }
}

/// <summary>
/// Start at login for a sandboxed app: <c>SMAppService.mainAppService</c> (macOS 13 and later), which adds the app
/// itself to System Settings › General › Login Items. A sandboxed app cannot write <c>~/Library/LaunchAgents</c>.
/// The login item starts the app without arguments, which starts it in the background like <c>--background</c>
/// does (only a first run or <c>--show</c> opens the launcher).
/// </summary>
[SupportedOSPlatform("macos13.0")]
internal static class MacLoginItem
{
    private const string Framework = "/System/Library/Frameworks/ServiceManagement.framework/ServiceManagement";

    // SMAppServiceStatus
    private const nint StatusEnabled = 1;
    private const nint StatusRequiresApproval = 2;

    /// <summary>True when the app is a login item, also while it waits for the user's approval in System Settings.</summary>
    public static bool IsEnabled()
    {
        var status = Foundation.SendNint(Service(), Foundation.Selector("status"));
        return status is StatusEnabled or StatusRequiresApproval;
    }

    /// <exception cref="InvalidOperationException">macOS refused the change.</exception>
    public static void SetEnabled(bool enabled)
    {
        nint error = 0;
        var selector = Foundation.Selector(enabled ? "registerAndReturnError:" : "unregisterAndReturnError:");
        if (!Foundation.SendBoolErr(Service(), selector, ref error))
            throw new InvalidOperationException(Foundation.Describe(error) ?? "macOS did not change the login item.");
    }

    private static nint Service()
    {
        NativeLibrary.Load(Framework);
        var service = ObjC.SendPtr(Foundation.Class("SMAppService"), Foundation.Selector("mainAppService"));
        return service != 0 ? service : throw new InvalidOperationException("SMAppService is not available.");
    }
}

/// <summary>The Foundation calls behind <see cref="MacFolderBookmarks"/> and <see cref="MacLoginItem"/>.</summary>
[SupportedOSPlatform("macos")]
internal static partial class Foundation
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";

    public static nint Class(string name) =>
        ObjC.objc_getClass(name) is var cls and not 0 ? cls : throw new InvalidOperationException($"{name} is not available.");

    public static nint Selector(string name) => ObjC.sel_registerName(name);

    [LibraryImport(Lib)]
    public static partial nint objc_autoreleasePoolPush();

    [LibraryImport(Lib)]
    public static partial void objc_autoreleasePoolPop(nint pool);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial nuint SendNuint(nint receiver, nint selector);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial nint SendNint(nint receiver, nint selector);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial nint SendPtrPtr(nint receiver, nint selector, nint arg);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial nint SendPtrPtrNuint(nint receiver, nint selector, nint arg, nuint length);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial nint SendPtrPtrBool(nint receiver, nint selector, nint arg, [MarshalAs(UnmanagedType.U1)] bool flag);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SendBoolErr(nint receiver, nint selector, ref nint error);

    /// <summary><c>-[NSURL bookmarkDataWithOptions:includingResourceValuesForKeys:relativeToURL:error:]</c></summary>
    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial nint CreateBookmark(nint url, nint selector, nuint options, nint keys, nint relativeTo, ref nint error);

    /// <summary><c>+[NSURL URLByResolvingBookmarkData:options:relativeToURL:bookmarkDataIsStale:error:]</c></summary>
    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial nint ResolveBookmark(nint cls, nint selector, nint data, nuint options, nint relativeTo, ref byte stale, ref nint error);

    /// <summary>A file URL for a folder path (<c>+[NSURL fileURLWithPath:isDirectory:]</c>).</summary>
    public static nint FileUrl(string path)
    {
        var text = NewString(path);
        return text == 0 ? 0 : SendPtrPtrBool(Class("NSURL"), Selector("fileURLWithPath:isDirectory:"), text, true);
    }

    /// <summary>The file-system path of a file URL.</summary>
    public static string? PathOf(nint url)
    {
        var path = ObjC.SendPtr(url, Selector("path"));
        return path == 0 ? null : Marshal.PtrToStringUTF8(ObjC.SendPtr(path, Selector("UTF8String")));
    }

    /// <summary>An NSError's localized description, or null.</summary>
    public static string? Describe(nint error)
    {
        if (error == 0)
            return null;
        var text = ObjC.SendPtr(error, Selector("localizedDescription"));
        return text == 0 ? null : Marshal.PtrToStringUTF8(ObjC.SendPtr(text, Selector("UTF8String")));
    }

    private static nint NewString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return SendPtrPtr(Class("NSString"), Selector("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }
}
