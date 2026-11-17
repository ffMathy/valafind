using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Valafind.App.Platform;
using Valafind.App.Services;
using Valafind.App.ViewModels;

namespace Valafind.App.Views;

/// <summary>Settings: hotkey, folders, resource limits, embedding size, model folder, start at login.</summary>
public partial class SettingsWindow : Window
{
    /// <summary>Designer constructor.</summary>
    public SettingsWindow()
        : this(DesignData.Settings)
    {
    }

    /// <summary>Creates the window for <paramref name="vm"/>.</summary>
    public SettingsWindow(SettingsViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        InitializeComponent();
        FlowDirection = Localization.Loc.FlowDirection;
        DataContext = vm;
        vm.PickFolderAsync = PickFolderAsync;
        vm.CloseRequested += (_, _) => Close();

        if (OperatingSystem.IsWindows())
            AttachBackdrop();
        try
        {
            Icon = TrayIconController.LoadIcon("valafind.png");
        }
        catch (Exception e) when (e is IOException or FileNotFoundException)
        {
            // Icon is cosmetic.
        }
    }

    /// <summary>Windows' own Mica like the Settings app, else Avalonia's Mica / acrylic; the Fluent cards on it are
    /// translucent by design. Without any backdrop the theme's solid background stays.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void AttachBackdrop()
    {
        var backdrop = new WindowsBackdropController(this, Environment.OSVersion.Version.Build);
        backdrop.KindChanged += (_, _) => Background =
            backdrop.Kind is BackdropKind.SystemMica or BackdropKind.AvaloniaMica or BackdropKind.Blur ? Brushes.Transparent : null;
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        }).ConfigureAwait(true);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
