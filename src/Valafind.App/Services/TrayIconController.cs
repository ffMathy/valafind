using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Valafind.App.Localization;

namespace Valafind.App.Services;

/// <summary>Tray / menu-bar icon with the Open, Pause/Resume, Settings…, Check for updates… and Quit menu.</summary>
internal sealed class TrayIconController : IDisposable
{
    private readonly Application _app;
    private readonly TrayIcon _tray;
    private readonly NativeMenuItem _open;
    private readonly NativeMenuItem _pause;
    private readonly NativeMenuItem _updates;
    private bool _disposed;

    public TrayIconController(Application app, Action open, Action togglePause, Action settings, Action checkForUpdates, Action quit)
    {
        _app = app;
        _open = new NativeMenuItem(Strings.OpenValafind);
        _open.Click += (_, _) => open();
        _pause = new NativeMenuItem(Strings.PauseIndexing);
        _pause.Click += (_, _) => togglePause();
        var settingsItem = new NativeMenuItem(Strings.SettingsMenu);
        settingsItem.Click += (_, _) => settings();
        _updates = new NativeMenuItem(Strings.CheckForUpdatesMenu);
        _updates.Click += (_, _) => checkForUpdates();
        var quitItem = new NativeMenuItem(Strings.QuitValafind);
        quitItem.Click += (_, _) => quit();

        var menu = new NativeMenu();
        menu.Items.Add(_open);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_pause);
        menu.Items.Add(settingsItem);
        // A copy from an app store is updated by the store, so it offers no update check.
        if (!Updates.StoreInstall.IsActive)
            menu.Items.Add(_updates);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quitItem);

        var mac = OperatingSystem.IsMacOS();
        _tray = new TrayIcon
        {
            Icon = LoadIcon(mac ? "valafind-tray-template.png" : "valafind-tray.png"),
            ToolTipText = "Valafind",
            Menu = menu,
            IsVisible = true,
        };
        if (mac)
            MacOSProperties.SetIsTemplateIcon(_tray, true);
        // Left click opens the launcher where the platform reports clicks (Windows, most Linux trays).
        _tray.Clicked += (_, _) => open();

        TrayIcon.SetIcons(app, [_tray]);
    }

    /// <summary>Loads a bundled icon (Assets/).</summary>
    public static WindowIcon LoadIcon(string name)
    {
        using var stream = AssetLoader.Open(new Uri("avares://Valafind/Assets/" + name));
        return new WindowIcon(stream);
    }

    /// <summary>A newer version is downloaded: the updates item now restarts into it.</summary>
    public void SetUpdateReady() => _updates.Header = Strings.RestartToUpdateMenu;

    /// <summary>Shows the hotkey next to "Open Valafind".</summary>
    public void SetHotkey(string chord, bool registered)
    {
        _open.Gesture = null;
        _open.Header = Strings.OpenValafind;
        if (!registered)
            return;
        try
        {
            _open.Gesture = KeyGesture.Parse(chord.Replace("Super", "Meta", StringComparison.OrdinalIgnoreCase)
                .Replace("Cmd", "Meta", StringComparison.OrdinalIgnoreCase)
                .Replace("Win", "Meta", StringComparison.OrdinalIgnoreCase));
        }
        catch (ArgumentException)
        {
            _open.Header = Loc.Format(Strings.OpenValafindWithHotkey, chord);
        }
    }

    /// <summary>Updates tooltip and the pause item caption.</summary>
    public void SetStatus(string stateText, string pauseMenuText)
    {
        if (_disposed)
            return;
        _tray.ToolTipText = "Valafind — " + stateText;
        _pause.Header = pauseMenuText;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _tray.IsVisible = false;
        TrayIcon.SetIcons(_app, []);
        _tray.Dispose();
    }
}
