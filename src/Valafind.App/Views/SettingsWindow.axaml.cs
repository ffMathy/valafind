using Avalonia.Controls;
using Avalonia.Media;
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

        if (OperatingSystem.IsWindows())
        {
            // Mica backdrop like the Windows Settings app; the Fluent cards on it are translucent by design.
            TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur];
            PropertyChanged += (_, e) =>
            {
                if (e.Property == ActualTransparencyLevelProperty)
                    ApplyBackdrop();
            };
            ApplyBackdrop();
        }
        try
        {
            Icon = TrayIconController.LoadIcon("valafind.png");
        }
        catch (Exception e) when (e is IOException or FileNotFoundException)
        {
            // Icon is cosmetic.
        }
    }

    /// <summary>Lets the Mica/acrylic material show through; keeps the theme's solid background without it.</summary>
    private void ApplyBackdrop() =>
        Background = ActualTransparencyLevel is { } level && level != WindowTransparencyLevel.None && level != WindowTransparencyLevel.Transparent
            ? Brushes.Transparent
            : null;

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
