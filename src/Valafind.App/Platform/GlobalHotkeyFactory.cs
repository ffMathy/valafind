using Valafind.App.Localization;

namespace Valafind.App.Platform;

/// <summary>Creates the <see cref="IGlobalHotkeyService"/> implementation for the current OS and session.</summary>
public static class GlobalHotkeyFactory
{
    /// <summary>Message shown when a Wayland session offers no way to register a global shortcut. Names the real
    /// executable: nothing installs a "valafind" command on PATH, so a bare name would fail silently.</summary>
    internal static string WaylandUnavailableMessage =>
        Loc.Format(Strings.HotkeyWaylandUnavailable, ToggleCommand);

    /// <summary>The command that toggles the running launcher, e.g. <c>/opt/Valafind/Valafind --toggle</c>.</summary>
    internal static string ToggleCommand => BuildToggleCommand(Environment.ProcessPath, AppExecutable.EntryAssemblyPath);

    /// <summary>Builds the toggle command for an executable path (quoted when it contains spaces). Under
    /// <c>dotnet Valafind.dll</c> the host is "dotnet", so the assembly is passed to it.</summary>
    internal static string BuildToggleCommand(string? processPath, string? entryAssemblyPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
            return "Valafind --toggle";
        var host = Path.GetFileNameWithoutExtension(processPath);
        if (string.Equals(host, "dotnet", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(entryAssemblyPath))
            return Quote(processPath) + " " + Quote(entryAssemblyPath) + " --toggle";
        return Quote(processPath) + " --toggle";

        static string Quote(string path) => path.Any(char.IsWhiteSpace) ? "\"" + path + "\"" : path;
    }

    /// <summary>
    /// Windows: RegisterHotKey on a message-only window thread. macOS: Carbon RegisterEventHotKey.
    /// Linux/X11: XGrabKey on a dedicated display connection. Linux/Wayland: the XDG Desktop Portal
    /// GlobalShortcuts interface when available, otherwise a service whose registration fails with guidance.
    /// Never throws; the returned service reports problems through <see cref="IGlobalHotkeyService.TryRegister"/>.
    /// </summary>
    public static IGlobalHotkeyService Create()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsHotkeyService();
        if (OperatingSystem.IsMacOS())
            return new MacHotkeyService();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            return DetectLinuxSession(Environment.GetEnvironmentVariable) switch
            {
                LinuxSession.X11 => new X11HotkeyService(),
                LinuxSession.Wayland => new PortalHotkeyService(),
                _ => new UnsupportedHotkeyService(
                    Strings.HotkeyNoGraphicalSession),
            };
        }

        return new UnsupportedHotkeyService(Strings.HotkeyUnsupportedOs);
    }

    internal enum LinuxSession
    {
        None,
        X11,
        Wayland,
    }

    /// <summary>
    /// X11 when XDG_SESSION_TYPE=x11, or DISPLAY is set without WAYLAND_DISPLAY. Under Wayland, XWayland's DISPLAY is
    /// ignored on purpose: X key grabs there only fire while an X11 client has focus, which would be misleading.
    /// </summary>
    internal static LinuxSession DetectLinuxSession(Func<string, string?> env)
    {
        var sessionType = env("XDG_SESSION_TYPE");
        var hasDisplay = !string.IsNullOrEmpty(env("DISPLAY"));
        var hasWayland = !string.IsNullOrEmpty(env("WAYLAND_DISPLAY"));

        if (string.Equals(sessionType, "x11", StringComparison.OrdinalIgnoreCase) && hasDisplay)
            return LinuxSession.X11;
        if (hasWayland || string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase))
            return LinuxSession.Wayland;
        if (hasDisplay)
            return LinuxSession.X11;
        return LinuxSession.None;
    }
}

/// <summary>Fallback service whose registration always fails with a fixed explanation.</summary>
internal sealed class UnsupportedHotkeyService(string reason) : IGlobalHotkeyService
{
    public string? ActiveChord => null;

    public bool TryRegister(string chord, Action onPressed, out string? error)
    {
        if (!HotkeyChord.TryParse(chord, out _, out error))
            return false;
        error = reason;
        return false;
    }

    public void Unregister() { }

    public void Dispose() { }
}

/// <summary>Helpers shared by the hotkey implementations.</summary>
internal static class HotkeyDispatch
{
    /// <summary>Runs the user callback on the thread pool so a slow handler never stalls a native message loop.</summary>
    public static void Raise(Action? callback)
    {
        if (callback is null)
            return;
        ThreadPool.UnsafeQueueUserWorkItem(static cb =>
        {
            try
            {
                cb();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Valafind: global hotkey handler threw: {ex}");
            }
        }, callback, preferLocal: false);
    }

    public static string InUseMessage(HotkeyChord chord) =>
        Loc.Format(Strings.HotkeyInUse, chord.DisplayString());
}
