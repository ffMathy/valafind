using Avalonia.Styling;
using Valafind.App.Platform;
using Xunit;

namespace Valafind.App.Tests.Platform;

public sealed class WindowsAppThemeTests
{
    [Fact]
    public void AppsUseLightTheme_maps_to_the_theme_variant()
    {
        // HKCU\...\Themes\Personalize\AppsUseLightTheme is a DWORD: 0 = dark app mode, 1 = light.
        Assert.Equal(ThemeVariant.Dark, WindowsAppTheme.FromRegistryValue(0));
        Assert.Equal(ThemeVariant.Light, WindowsAppTheme.FromRegistryValue(1));
        Assert.Equal(ThemeVariant.Dark, WindowsAppTheme.FromRegistryValue(0L));
        // Missing or unexpected values leave Avalonia's own detection in charge.
        Assert.Null(WindowsAppTheme.FromRegistryValue(null));
        Assert.Null(WindowsAppTheme.FromRegistryValue("0"));
    }

    [Fact]
    public void Off_windows_nothing_is_read_or_changed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Reads the real registry on Windows.");
        Assert.Null(WindowsAppTheme.Read());
        WindowsAppTheme.Apply(null); // Must not throw.
    }
}
