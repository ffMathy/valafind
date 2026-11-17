using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using Valafind.App.Platform;
using Valafind.App.Services;

namespace Valafind.App;

/// <summary>Entry point: single-instance handling, then the Avalonia desktop lifetime.</summary>
public static class Program
{
    /// <summary>Command for a start at login: run in the tray without showing the launcher.</summary>
    internal const string BackgroundCommand = "background";

    /// <summary>Starts Valafind, or forwards <c>--toggle</c> / <c>--show</c> / <c>--quit</c> to the running instance.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // Started from a download or anywhere else: install for this user and start the installed copy instead.
        var installation = Installation.OnStartup(args);
        if (installation.Exit)
            return 0;
        args = installation.Arguments;

        var command = ParseCommand(args);

        // Forward an explicit command to a running instance (e.g. a Wayland shortcut bound to "<path> --toggle").
        if (command is not null and not BackgroundCommand && SignalExisting(command))
            return 0;
        if (command == "quit")
            return 0; // Nothing to quit.

        var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            // Another instance owns the lock: bring it to front instead of starting a second copy (unless this is a
            // start at login, which must stay invisible).
            if (command != BackgroundCommand)
                SignalExisting("show");
            return 0;
        }

        using var loggerFactory = AppLogging.Create();
        var log = loggerFactory.CreateLogger(typeof(Program));
        AppLogging.HookUnhandledExceptions(log);
        try
        {
            return RunGuarded(() =>
            {
                App.Host = AppHost.Create(instance, command, loggerFactory);
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
            }, log, Console.Error, static () => App.Host?.IsStarted == true);
        }
        finally
        {
            App.Host = null;
            instance.Dispose();
        }
    }

    /// <summary>
    /// Runs the UI and turns an escaping exception into a clear message on stderr, a log entry and exit code 1,
    /// instead of an abort with a stack trace (typical cause: no graphical session, e.g. over SSH or an autostart
    /// entry that fires before the display is ready).
    /// </summary>
    internal static int RunGuarded(Func<int> run, ILogger log, TextWriter stderr, Func<bool> uiStarted)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(uiStarted);
        try
        {
            return run();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (uiStarted())
            {
                Log.StoppedUnexpectedly(log, e);
                stderr.WriteLine($"Valafind stopped after an unexpected error: {e.Message}");
            }
            else
            {
                Log.UiStartFailed(log, e);
                stderr.WriteLine($"Valafind could not start its user interface (no graphical session?): {e.Message}");
            }
            stderr.WriteLine($"Details are in the log folder: {Valafind.Core.Configuration.AppPaths.LogDirectory}");
            return 1;
        }
    }

    /// <summary>Avalonia configuration (also used by the visual designer).</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>Maps "--toggle"/"--show"/"--quit"/"--background" (also "-t", "toggle", …) to a command; null if none.</summary>
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
                case "background" or "b":
                    return BackgroundCommand;
            }
        }
        return null;
    }

    private static bool SignalExisting(string command)
    {
        // The running instance shows its launcher in answer to this message, but Windows only lets the process that
        // received the user's input (this one) move a window to the foreground: hand that right on first.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _ = Win32.AllowSetForegroundWindow(Win32.ASFW_ANY);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
            }
        }
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
