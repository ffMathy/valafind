using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Styling;
using Microsoft.Win32;

namespace Valafind.App.Platform;

/// <summary>
/// Follows Windows' "Choose your app mode" (Settings → Personalization → Colors) for the whole app.
/// </summary>
/// <remarks>
/// Avalonia derives the system theme only from WinRT <c>UISettings</c>; when that type cannot be activated for an
/// unpackaged desktop app it silently reports Light forever. The setting itself lives in the registry value
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme</c> (0 = dark, 1 = light),
/// which is always readable, so Valafind reads it directly and re-checks whenever one of its windows opens.
/// Avalonia's own Mica/acrylic backdrops and the DWM dark title-bar attribute follow the resulting theme variant.
/// </remarks>
internal static class WindowsAppTheme
{
    internal const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    internal const string AppsUseLightThemeValue = "AppsUseLightTheme";

    /// <summary>Maps the registry value to a theme: 0 → Dark, any other number → Light, missing/unreadable → null.</summary>
    internal static ThemeVariant? FromRegistryValue(object? value) => value switch
    {
        int i => i == 0 ? ThemeVariant.Dark : ThemeVariant.Light,
        long l => l == 0 ? ThemeVariant.Dark : ThemeVariant.Light,
        _ => null,
    };

    /// <summary>The user's app mode on Windows; null elsewhere or when it cannot be read.</summary>
    public static ThemeVariant? Read()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        return ReadWindows();
    }

    /// <summary>
    /// Sets <paramref name="app"/>'s theme to the Windows app mode. Leaves Avalonia's own detection in charge
    /// (<see cref="ThemeVariant.Default"/>) when the value cannot be read, and on other operating systems.
    /// </summary>
    public static void Apply(Application? app)
    {
        if (app is null || Read() is not { } variant)
            return;
        if (app.RequestedThemeVariant != variant)
            app.RequestedThemeVariant = variant;
    }

    [SupportedOSPlatform("windows")]
    private static ThemeVariant? ReadWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return FromRegistryValue(key?.GetValue(AppsUseLightThemeValue));
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
