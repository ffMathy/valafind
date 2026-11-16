using Avalonia.Controls;
using Avalonia.Platform.Storage;
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
        DataContext = vm;
        vm.PickFolderAsync = PickFolderAsync;
        vm.CloseRequested += (_, _) => Close();
        try
        {
            Icon = TrayIconController.LoadIcon("valafind.png");
        }
        catch (Exception e) when (e is IOException or FileNotFoundException)
        {
            // Icon is cosmetic.
        }
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
