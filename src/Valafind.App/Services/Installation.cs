using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Valafind.App.Platform;
using Valafind.App.Updates;
using Valafind.Core.Configuration;

namespace Valafind.App.Services;

/// <summary>How Valafind installs and updates itself (see <see cref="SelfInstaller"/> and <see cref="Updater"/>).</summary>
internal static partial class Installation
{
    public static readonly AppIdentity App = new()
    {
        Name = "Valafind",
        LinuxExecutableName = "valafind",
        Description = "Find files by meaning, entirely on this computer",
        GitHubOwner = "ffMathy",
        GitHubRepository = "valafind",
        // Release assets, see .github/workflows/ci.yml: valafind-win-x64.exe, valafind-linux-arm64, valafind-osx-arm64.app.zip.
        ExecutableAssetName = rid => "valafind-" + rid + (rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : string.Empty),
        MacBundleAssetName = rid => "valafind-" + rid + ".app.zip",
        // The Wayland GlobalShortcuts portal identifies the app by this desktop entry name (packaging/linux).
        DesktopEntryId = "app.valafind.Valafind",
        DesktopEntryLines =
        [
            "GenericName=Semantic file search",
            "Categories=Utility;FileTools;",
            "Keywords=search;find;files;semantic;",
            "StartupNotify=false",
            "X-GNOME-UsesNotifications=false",
        ],
        LaunchArguments = ["--show"],
        BundleIdentifier = "app.valafind.Valafind",
        EnvironmentPrefix = "VALAFIND",
        // Embedded separately (see the project file): the installer runs before Avalonia can load its assets.
        OpenIconPng = () => typeof(Installation).Assembly.GetManifestResourceStream("Valafind.Application.png"),
    };

    /// <summary>What the installer found at startup; null when it did not run.</summary>
    public static StartupResult? Startup { get; private set; }

    public static InstallLayout Layout() => InstallLayout.ForCurrentUser(App, AppPaths.DataDirectory);

    /// <summary>Installs this copy when it was started from elsewhere. See <see cref="SelfInstaller.OnStartup"/>.</summary>
    public static StartupResult OnStartup(string[] args)
    {
        // Logging starts only if the installer has something to say: most starts are the installed copy, and a
        // "--toggle" sent to a running Valafind should not open the log file.
        using var log = new LazyLog();
        var installer = new SelfInstaller(App, Layout(), RunningApp.Current(), new Host(), log);
        Startup = installer.OnStartup(args);
        return Startup;
    }

    /// <summary>Writes the installer's and the updater's messages to a <see cref="ILogger"/>.</summary>
    internal sealed partial class UpdateLog(ILogger logger) : IUpdateLog
    {
        public void Information(string message) => Info(logger, message);

        public void Warning(string message, Exception? exception = null) => Warn(logger, message, exception);

        [LoggerMessage(200, LogLevel.Information, "{Message}")]
        private static partial void Info(ILogger logger, string message);

        [LoggerMessage(201, LogLevel.Warning, "{Message}")]
        private static partial void Warn(ILogger logger, string message, Exception? exception);
    }

    private sealed class LazyLog : IUpdateLog, IDisposable
    {
        private ILoggerFactory? _factory;
        private UpdateLog? _log;

        private UpdateLog Log
        {
            get
            {
                if (_log is null)
                {
                    _factory = AppLogging.Create();
                    _log = new UpdateLog(_factory.CreateLogger(typeof(Installation)));
                }
                return _log;
            }
        }

        public void Information(string message) => Log.Information(message);

        public void Warning(string message, Exception? exception = null) => Log.Warning(message, exception);

        public void Dispose() => _factory?.Dispose();
    }

    private sealed class Host : IInstallHost
    {
        private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(15);

        /// <summary>Asks a running Valafind to quit (it saves its index on the way out) and waits until it has.</summary>
        public void StopRunningInstance()
        {
            bool signalled;
            try
            {
                signalled = SingleInstance.SignalExistingAsync("quit").GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
            {
                signalled = false;
            }
            if (!signalled)
                return;

            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < QuitTimeout)
            {
                using var instance = SingleInstance.TryAcquire();
                if (instance is not null)
                    return;
                Thread.Sleep(200);
            }
            throw new InvalidOperationException("The running Valafind did not quit.");
        }

        public void RemoveAutostart() => Autostart.SetEnabled(false);
    }
}
