using Avalonia;
using Avalonia.Controls;
using Valafind.App.Platform;
using Valafind.App.Services;

namespace Valafind.App;

/// <summary>Entry point: single-instance handling, then the Avalonia desktop lifetime.</summary>
public static class Program
{
    /// <summary>Starts Valafind, or forwards <c>--toggle</c> / <c>--show</c> / <c>--quit</c> to the running instance.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        var command = ParseCommand(args);

        // Forward an explicit command to a running instance (e.g. a Wayland shortcut bound to "valafind --toggle").
        if (command is not null && SignalExisting(command))
            return 0;
        if (command == "quit")
            return 0; // Nothing to quit.

        var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            // Another instance owns the lock: bring it to front instead of starting a second copy.
            SignalExisting("show");
            return 0;
        }

        try
        {
            App.Host = AppHost.Create(instance, command);
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            App.Host = null;
            instance.Dispose();
        }
    }

    /// <summary>Avalonia configuration (also used by the visual designer).</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>Maps "--toggle"/"--show"/"--quit" (also "-t", "toggle", …) to a command; null if none.</summary>
    internal static string? ParseCommand(IEnumerable<string> args)
    {
        foreach (var raw in args)
        {
            var arg = raw.Trim().TrimStart('-', '/').ToLowerInvariant();
            switch (arg)
            {
                case "toggle" or "t":
                    return "toggle";
                case "show" or "s":
                    return "show";
                case "quit" or "q" or "exit":
                    return "quit";
            }
        }
        return null;
    }

    private static bool SignalExisting(string command)
    {
        try
        {
            return SingleInstance.SignalExistingAsync(command).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            return false;
        }
    }
}
