namespace Valafind.App.ViewModels;

/// <summary>Which platform-specific launcher design is shown.</summary>
public enum UiFlavor
{
    /// <summary>Windows A · Command Launcher (Fluent).</summary>
    Windows,
    /// <summary>macOS A · Floating Bar (Spotlight-like, with preview pane).</summary>
    Mac,
    /// <summary>Linux A · GNOME-style dialog (libadwaita look).</summary>
    Linux,
}

/// <summary>Chooses the <see cref="UiFlavor"/> for the running OS; <c>VALAFIND_UI=windows|mac|linux</c> overrides it.</summary>
public static class UiFlavors
{
    /// <summary>Environment variable that forces a flavor (useful for testing the other designs).</summary>
    public const string OverrideVariable = "VALAFIND_UI";

    /// <summary>Detects the flavor from <see cref="OverrideVariable"/> or the current OS.</summary>
    public static UiFlavor Detect() => Parse(Environment.GetEnvironmentVariable(OverrideVariable)) ?? ForCurrentOs();

    /// <summary>The native flavor of the OS this process runs on.</summary>
    public static UiFlavor ForCurrentOs() =>
        OperatingSystem.IsWindows() ? UiFlavor.Windows :
        OperatingSystem.IsMacOS() ? UiFlavor.Mac :
        UiFlavor.Linux;

    /// <summary>Parses "windows"/"win", "mac"/"macos"/"osx", "linux"/"gnome"; null for anything else.</summary>
    public static UiFlavor? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "windows" or "win" => UiFlavor.Windows,
        "mac" or "macos" or "osx" => UiFlavor.Mac,
        "linux" or "gnome" => UiFlavor.Linux,
        _ => null,
    };
}
