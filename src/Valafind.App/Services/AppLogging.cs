using Microsoft.Extensions.Logging;
using Valafind.Core.Configuration;

namespace Valafind.App.Services;

/// <summary>
/// Process-wide logging: console (useful when started from a terminal) plus a rolling file in
/// <see cref="AppPaths.LogDirectory"/>, which is what users of the GUI build can actually look at.
/// <c>VALAFIND_LOG_LEVEL=Debug</c> raises the detail (Debug messages may contain file paths).
/// </summary>
internal static partial class AppLogging
{
    /// <summary>Environment variable that sets the minimum level (Trace, Debug, Information, Warning, Error).</summary>
    public const string LevelVariable = "VALAFIND_LOG_LEVEL";

    /// <summary>Creates the logger factory. Never throws: logging problems must not stop the app.</summary>
    public static ILoggerFactory Create()
    {
        var level = ParseLevel(Environment.GetEnvironmentVariable(LevelVariable)) ?? LogLevel.Information;
        RollingFileLoggerProvider? file = null;
        try
        {
            file = new RollingFileLoggerProvider(AppPaths.LogDirectory, level);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // No usable data directory: console only.
        }
        return LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(level);
            b.AddConsole();
            if (file is not null)
                b.AddProvider(file);
        });
    }

    /// <summary>Parses a level name; null when empty or unknown.</summary>
    internal static LogLevel? ParseLevel(string? value) =>
        Enum.TryParse<LogLevel>(value?.Trim(), ignoreCase: true, out var level) && Enum.IsDefined(level) ? level : null;

    /// <summary>Logs exceptions that would otherwise end the process silently.</summary>
    public static void HookUnhandledExceptions(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                Unhandled(log, e.IsTerminating, ex);
            else
                UnhandledObject(log, e.IsTerminating, e.ExceptionObject?.GetType().FullName ?? "null");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            UnobservedTask(log, e.Exception);
            e.SetObserved();
        };
    }

    [LoggerMessage(100, LogLevel.Critical, "Unhandled exception (terminating: {Terminating})")]
    private static partial void Unhandled(ILogger logger, bool terminating, Exception exception);

    [LoggerMessage(101, LogLevel.Critical, "Unhandled non-exception object {Type} (terminating: {Terminating})")]
    private static partial void UnhandledObject(ILogger logger, bool terminating, string type);

    [LoggerMessage(102, LogLevel.Warning, "Unobserved task exception")]
    private static partial void UnobservedTask(ILogger logger, Exception exception);
}
