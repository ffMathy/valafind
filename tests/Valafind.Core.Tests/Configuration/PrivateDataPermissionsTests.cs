using Valafind.Core.Configuration;
using Xunit;

namespace Valafind.Core.Tests.Configuration;

/// <summary>Valafind's data folder holds text excerpts of the user's files: other local users must not read it.</summary>
public sealed class PrivateDataPermissionsTests : IDisposable
{
    private const UnixFileMode GroupOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "valafind-perm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void EnsurePrivateDirectory_creates_owner_only_directories_and_tightens_existing_ones()
    {
        var fresh = Path.Combine(_root, "fresh", "nested");
        AppPaths.EnsurePrivateDirectory(fresh);
        Assert.True(Directory.Exists(fresh));
        if (OperatingSystem.IsWindows())
            return; // Per-user profile ACLs protect it; nothing more to check.

        Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(fresh) & GroupOther);

        var old = Path.Combine(_root, "old");
        Directory.CreateDirectory(old);
        File.SetUnixFileMode(old, AppPaths.PrivateDirectoryMode | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                  UnixFileMode.OtherRead | UnixFileMode.OtherExecute); // 0755, as created by a umask
        AppPaths.EnsurePrivateDirectory(old);
        Assert.Equal(AppPaths.PrivateDirectoryMode, File.GetUnixFileMode(old));
    }

    [Fact]
    public void Settings_are_saved_owner_only_and_round_trip()
    {
        var path = Path.Combine(_root, "data", "settings.json");
        var settings = new ValafindSettings { Hotkey = "Ctrl+Shift+K", MaxCpuPercent = 42, Roots = ["/x"] };
        settings.Save(path);

        var loaded = ValafindSettings.Load(path);
        Assert.Equal("Ctrl+Shift+K", loaded.Hotkey);
        Assert.Equal(42, loaded.MaxCpuPercent);
        Assert.Equal(["/x"], loaded.Roots);
        Assert.False(File.Exists(path + ".tmp"));
        if (OperatingSystem.IsWindows())
            return;

        Assert.Equal(AppPaths.PrivateFileMode, File.GetUnixFileMode(path));
        Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(Path.GetDirectoryName(path)!) & GroupOther);

        // A previously world-readable settings file is replaced by a private one on the next save.
        File.SetUnixFileMode(path, AppPaths.PrivateFileMode | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        settings.Save(path);
        Assert.Equal(AppPaths.PrivateFileMode, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Default_hotkey_avoids_os_reserved_chords()
    {
        // ⌥Space alone is refused by macOS 15+, Super+Space is GNOME's input switch, Alt+Space the Windows window menu.
        Assert.Equal("Ctrl+Alt+Space", ValafindSettings.DefaultHotkey);
        Assert.Equal("Ctrl+Alt+Space", new ValafindSettings().EffectiveHotkey);
        Assert.Equal("Cmd+Shift+Space", new ValafindSettings { Hotkey = "Cmd+Shift+Space" }.EffectiveHotkey);
    }
}
