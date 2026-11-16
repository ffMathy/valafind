using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Valafind.App.Services;

namespace Valafind.App;

/// <summary>The Avalonia application. Starts the <see cref="AppHost"/> when running as a desktop app.</summary>
public partial class App : Application
{
    /// <summary>Host created by <see cref="Program"/>; null in the designer and in headless tests.</summary>
    internal static AppHost? Host { get; set; }

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Host is { } host)
            host.Start(this, desktop);
        base.OnFrameworkInitializationCompleted();
    }
}
