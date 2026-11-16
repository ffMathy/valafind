using Microsoft.Extensions.Logging;

namespace Valafind.App.Services;

/// <summary>Source-generated log messages of the app host.</summary>
internal static partial class Log
{
    [LoggerMessage(1, LogLevel.Warning, "Ignoring unknown single-instance command {Command}")]
    public static partial void UnknownCommand(ILogger logger, string command);

    [LoggerMessage(2, LogLevel.Warning, "Global hotkey registration failed")]
    public static partial void HotkeyRegistrationFailed(ILogger logger, Exception exception);

    [LoggerMessage(3, LogLevel.Warning, "Hotkey {Chord}: {Error}")]
    public static partial void HotkeyUnavailable(ILogger logger, string chord, string error);

    [LoggerMessage(4, LogLevel.Warning, "Model lookup failed")]
    public static partial void ModelLookupFailed(ILogger logger, Exception exception);

    [LoggerMessage(5, LogLevel.Information, "Core services ready in {ElapsedMs} ms (model {ModelId})")]
    public static partial void CoreReady(ILogger logger, long elapsedMs, string modelId);

    [LoggerMessage(6, LogLevel.Error, "Failed to start Core services")]
    public static partial void CoreFailed(ILogger logger, Exception exception);

    [LoggerMessage(7, LogLevel.Error, "Indexing failed to start")]
    public static partial void IndexingFailed(ILogger logger, Exception exception);

    [LoggerMessage(8, LogLevel.Debug, "Core task ended with an error during shutdown")]
    public static partial void CoreTaskFailed(ILogger logger, Exception exception);

    [LoggerMessage(9, LogLevel.Warning, "Error while disposing Core services")]
    public static partial void CoreDisposeFailed(ILogger logger, Exception exception);

    [LoggerMessage(10, LogLevel.Warning, "Could not save settings")]
    public static partial void SettingsSaveFailed(ILogger logger, Exception exception);

    [LoggerMessage(11, LogLevel.Warning, "Error during shutdown")]
    public static partial void ShutdownFailed(ILogger logger, Exception exception);

    [LoggerMessage(12, LogLevel.Debug, "Platform call failed: {What}")]
    public static partial void PlatformCallFailed(ILogger logger, string what, Exception exception);

    // Paths are only logged at Debug level (see ShellFailedPath); the warning carries the verb and the error.
    [LoggerMessage(13, LogLevel.Warning, "Could not {Verb} the selected file")]
    public static partial void ShellFailed(ILogger logger, string verb, Exception exception);

    [LoggerMessage(14, LogLevel.Warning, "Clipboard write failed")]
    public static partial void ClipboardFailed(ILogger logger, Exception exception);

    [LoggerMessage(15, LogLevel.Debug, "Could not {Verb} {Path}")]
    public static partial void ShellFailedPath(ILogger logger, string verb, string path);

    [LoggerMessage(16, LogLevel.Critical, "Valafind could not start its user interface")]
    public static partial void UiStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(17, LogLevel.Critical, "Valafind stopped after an unexpected error")]
    public static partial void StoppedUnexpectedly(ILogger logger, Exception exception);

    [LoggerMessage(18, LogLevel.Information, "Valafind {Version} starting ({Os}, UI {Flavor})")]
    public static partial void Starting(ILogger logger, string version, string os, string flavor);

    [LoggerMessage(19, LogLevel.Information, "Model not installed; waiting for it to appear")]
    public static partial void ModelMissing(ILogger logger);

    [LoggerMessage(20, LogLevel.Information, "Model files changed; reloading")]
    public static partial void ModelChanged(ILogger logger);

    [LoggerMessage(21, LogLevel.Warning, "Could not switch indexing to interactive={Interactive}")]
    public static partial void SetInteractiveFailed(ILogger logger, bool interactive, Exception exception);

    [LoggerMessage(22, LogLevel.Information, "Shutting down")]
    public static partial void ShuttingDown(ILogger logger);

    [LoggerMessage(23, LogLevel.Warning, "Shutdown did not finish within {TimeoutMs} ms")]
    public static partial void ShutdownTimedOut(ILogger logger, long timeoutMs);
}
