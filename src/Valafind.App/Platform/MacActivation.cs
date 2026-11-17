using System.Runtime.Versioning;

namespace Valafind.App.Platform;

/// <summary>
/// macOS application activation helpers (no-ops on other systems). Calls made off the main thread are posted to
/// Avalonia's UI thread.
/// </summary>
public static class MacActivation
{
    private const nint NSApplicationActivationPolicyAccessory = 1;

    /// <summary>
    /// Runs as an accessory app: no Dock icon and no menu bar, but windows can still be shown and focused.
    /// Call once at startup, after Avalonia has initialised.
    /// </summary>
    public static void UseAccessoryActivationPolicy()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        PostSafely(SetAccessoryPolicy);
    }

    /// <summary>Brings the app to the front even though another app is active (e.g. after the global hotkey).</summary>
    public static void ActivateIgnoringOtherApps()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        PostSafely(Activate);
    }

    [SupportedOSPlatform("macos")]
    private static void SetAccessoryPolicy()
    {
        var app = ObjC.SharedApplication();
        if (app != 0)
            _ = ObjC.SendBoolPtr(app, ObjC.sel_registerName("setActivationPolicy:"), NSApplicationActivationPolicyAccessory);
    }

    [SupportedOSPlatform("macos")]
    private static void Activate()
    {
        var app = ObjC.SharedApplication();
        if (app == 0)
            return;

        // macOS 14 replaced activation with the cooperative -[NSApplication activate]; the legacy call still
        // forces activation for an accessory app invoked by a hotkey, so issue both where available.
        if (OperatingSystem.IsMacOSVersionAtLeast(14) && ObjC.RespondsTo(app, "activate"))
            ObjC.SendVoid(app, ObjC.sel_registerName("activate"));
        ObjC.SendVoidBool(app, ObjC.sel_registerName("activateIgnoringOtherApps:"), true);
    }

    [SupportedOSPlatform("macos")]
    private static void PostSafely(Action action)
    {
        try
        {
            MacMainThread.Post(action);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Valafind: macOS activation call failed: {ex.Message}");
        }
    }
}
