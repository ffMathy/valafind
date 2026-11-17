using Avalonia.Controls;
using Valafind.App.Platform;
using Valafind.App.Views;
using Xunit;

namespace Valafind.App.Tests.Platform;

/// <summary>Order: Windows' own Mica → Avalonia's Mica → acrylic / blur → transparency → opaque.</summary>
public sealed class WindowsBackdropPolicyTests
{
    [Theory]
    [InlineData(19045, false)] // Windows 10 22H2
    [InlineData(22000, false)] // Windows 11 21H2: no DWMWA_SYSTEMBACKDROP_TYPE yet
    [InlineData(22621, true)]  // Windows 11 22H2
    [InlineData(26100, true)]  // Windows 11 24H2
    public void Windows_own_Mica_is_tried_first_from_22H2(int build, bool expected) =>
        Assert.Equal(expected, WindowsBackdropPolicy.PrefersSystemMica(build));

    [Fact]
    public void Avalonia_chain_starts_with_its_Mica_and_ends_with_plain_transparency()
    {
        Assert.Equal(
            [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent],
            WindowsBackdropPolicy.AvaloniaChain);
        Assert.Equal([WindowTransparencyLevel.Transparent], WindowsBackdropPolicy.SystemMicaHint);
    }

    [Fact]
    public void System_Mica_is_used_when_Windows_accepts_it()
    {
        var calls = 0;
        var (kind, fallBack) = WindowsBackdropPolicy.Decide(true, WindowTransparencyLevel.Transparent, hasHandle: true, () => { calls++; return true; });
        Assert.Equal((BackdropKind.SystemMica, false), (kind, fallBack));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Refused_system_Mica_falls_back_to_the_Avalonia_chain()
    {
        Assert.Equal((BackdropKind.Transparent, true),
            WindowsBackdropPolicy.Decide(true, WindowTransparencyLevel.Transparent, hasHandle: true, () => false));
        // No per-pixel transparency at all: there is nothing for DWM to show through, so do not even try.
        var tried = false;
        Assert.Equal((BackdropKind.None, true),
            WindowsBackdropPolicy.Decide(true, WindowTransparencyLevel.None, hasHandle: true, () => tried = true));
        Assert.False(tried);
    }

    [Fact]
    public void Nothing_is_decided_before_the_window_exists()
    {
        var tried = false;
        Assert.Equal((BackdropKind.None, false),
            WindowsBackdropPolicy.Decide(true, WindowTransparencyLevel.Transparent, hasHandle: false, () => tried = true));
        Assert.False(tried);
    }

    [Fact]
    public void Avalonia_levels_map_to_backdrops_and_surfaces()
    {
        Assert.Equal(BackdropKind.AvaloniaMica, WindowsBackdropPolicy.Decide(false, WindowTransparencyLevel.Mica, true, () => true).Kind);
        Assert.Equal(BackdropKind.Blur, WindowsBackdropPolicy.FromAvalonia(WindowTransparencyLevel.AcrylicBlur));
        Assert.Equal(BackdropKind.Blur, WindowsBackdropPolicy.FromAvalonia(WindowTransparencyLevel.Blur));
        Assert.Equal(BackdropKind.Transparent, WindowsBackdropPolicy.FromAvalonia(WindowTransparencyLevel.Transparent));
        Assert.Equal(BackdropKind.None, WindowsBackdropPolicy.FromAvalonia(WindowTransparencyLevel.None));

        Assert.Equal(LauncherSurface.Mica, LauncherWindow.SurfaceFor(BackdropKind.SystemMica));
        Assert.Equal(LauncherSurface.Mica, LauncherWindow.SurfaceFor(BackdropKind.AvaloniaMica));
        Assert.Equal(LauncherSurface.Blurred, LauncherWindow.SurfaceFor(BackdropKind.Blur));
        Assert.Equal(LauncherSurface.Floating, LauncherWindow.SurfaceFor(BackdropKind.Transparent));
        Assert.Equal(LauncherSurface.Opaque, LauncherWindow.SurfaceFor(BackdropKind.None));
    }
}
