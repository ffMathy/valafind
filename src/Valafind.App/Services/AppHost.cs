using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Valafind.App.Platform;
using Valafind.App.Updates;
using Valafind.App.ViewModels;
using Valafind.App.Views;
using Valafind.Core.Abstractions;
using Valafind.Core.Configuration;
using Valafind.App.Localization;

namespace Valafind.App.Services;

/// <summary>
/// Composition root and application controller: settings, Core services (loaded in the background), launcher window,
/// tray icon, global hotkey and single-instance commands.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    /// <summary>The first update check waits this long, so it does not compete with starting up.</summary>
    private static readonly TimeSpan FirstUpdateCheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

    private readonly ServiceProvider _services;
    private readonly ValafindSettings _settings;
    private readonly ILogger _log;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LauncherViewModel _launcherVm;
    private readonly SingleInstance? _instance;
    private readonly string? _startupCommand;
    private readonly bool _firstRun;
    // Every stop/restart/shutdown of the Core runs here, one at a time: a second caller waits for the first instead
    // of returning while the old Core is still being disposed.
    private readonly SerialTaskQueue _lifecycle = new();
    private readonly ModelInstallWatcher _modelWatcher;

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private LauncherWindow? _launcher;
    private SettingsWindow? _settingsWindow;
    private TrayIconController? _tray;
    private HttpClient? _updateHttp;
    private Updater? _updater;
    private DispatcherTimer? _restartTimer;
    private volatile IGlobalHotkeyService? _hotkeys;
    private Task<string?> _hotkeyTask = Task.FromResult<string?>(null);

    private CoreServices? _core;
    private CancellationTokenSource _coreCts = new();
    private Task _coreTask = Task.CompletedTask;
    private IndexStatus? _pendingStatus;
    private int _statusPostQueued;
    private bool _quitting;

    private AppHost(ServiceProvider services, SingleInstance? instance, string? startupCommand, bool firstRun)
    {
        _services = services;
        _instance = instance;
        _startupCommand = startupCommand;
        _firstRun = firstRun;
        _settings = services.GetRequiredService<ValafindSettings>();
        _loggerFactory = services.GetRequiredService<ILoggerFactory>();
        _log = _loggerFactory.CreateLogger<AppHost>();
        _launcherVm = services.GetRequiredService<LauncherViewModel>();
        _launcherVm.Actions = new LauncherActions(this);
        _modelWatcher = new ModelInstallWatcher(
            () => CoreServicesFactory.ModelSignature(_settings.ModelDirectory),
            () => Dispatcher.UIThread.Post(OnModelFilesChanged));
    }

    /// <summary>Builds the host. <paramref name="instance"/> is this process's single-instance lock (may be null when
    /// the platform has none); <paramref name="startupCommand"/> is "show"/"toggle" when launched to show the UI and
    /// "background" for a start at login. <paramref name="loggerFactory"/> is owned by the caller.</summary>
    public static AppHost Create(SingleInstance? instance, string? startupCommand, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var firstRun = !File.Exists(AppPaths.SettingsFile);
        var settings = ValafindSettings.Load();
        Localization.Loc.Apply(settings.Language);
        var flavor = UiFlavors.Detect();
        var services = new ServiceCollection()
            .AddSingleton(loggerFactory)
            .AddSingleton(typeof(ILogger<>), typeof(Logger<>))
            .AddSingleton(settings)
            .AddSingleton(sp => new LauncherViewModel(flavor, sp.GetRequiredService<ValafindSettings>())
            {
                IsModelBundled = CoreServicesFactory.IsModelBundled,
            })
            .BuildServiceProvider();
        return new AppHost(services, instance, startupCommand, firstRun);
    }

    /// <summary>The launcher view model (for tests and diagnostics).</summary>
    public LauncherViewModel Launcher => _launcherVm;

    /// <summary>True once <see cref="Start"/> ran (the UI is up).</summary>
    public bool IsStarted { get; private set; }

    /// <summary>Wires everything up. Called once from <see cref="App.OnFrameworkInitializationCompleted"/>.</summary>
    public void Start(Application app, IClassicDesktopStyleApplicationLifetime desktop)
    {
        ArgumentNullException.ThrowIfNull(app);
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        IsStarted = true;
        Log.Starting(_log, typeof(AppHost).Assembly.GetName().Version?.ToString() ?? "?",
            System.Runtime.InteropServices.RuntimeInformation.OSDescription, _launcherVm.Flavor.ToString());
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.ShutdownRequested += OnShutdownRequested;

        TryPlatform(MacActivation.UseAccessoryActivationPolicy, "switch to accessory activation policy");
        WindowsAppTheme.Apply(app);
        // Avalonia raises this when Windows broadcasts a colour/theme change; re-read the authoritative app mode then.
        if (app.PlatformSettings is { } platformSettings)
            platformSettings.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(() => WindowsAppTheme.Apply(Application.Current));

        _launcher = new LauncherWindow(_launcherVm, _launcherVm.Flavor);
        _tray = new TrayIconController(app, ShowLauncher, TogglePause, ShowSettings, CheckForUpdates, Quit);
        _launcherVm.Status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IndexStatusViewModel.StateText) or nameof(IndexStatusViewModel.PauseMenuText))
                _tray?.SetStatus(_launcherVm.Status.StateText, _launcherVm.Status.PauseMenuText);
        };
        _launcherVm.PropertyChanged += OnLauncherPropertyChanged;
        _tray.SetStatus(_launcherVm.Status.StateText, _launcherVm.Status.PauseMenuText);

        _ = RegisterHotkeyAsync();

        if (_instance is not null)
            _instance.CommandReceived += OnInstanceCommand;

        _coreTask = LoadCoreAsync(_coreCts.Token);
        ConfigureInstalledCopy();
        StartUpdater();

        var background = _startupCommand == Program.BackgroundCommand;
        if (_startupCommand is "show" or "toggle" || (_firstRun && !background))
            Dispatcher.UIThread.Post(ShowLauncher, DispatcherPriority.Background);
        if (_firstRun)
        {
            // Persist the defaults so the welcome (first-run launcher) appears once, not on every start.
            TrySaveSettings();
        }
    }

    // ------------------------------------------------------------------ launcher / windows

    /// <summary>Shows the launcher, or hides it if it is visible.</summary>
    public void ToggleLauncher()
    {
        if (_launcher is null || _quitting)
            return;
        if (_launcher.IsVisible)
            _launcher.HideLauncher();
        else
            ShowLauncher();
    }

    /// <summary>Shows and focuses the launcher on the screen under the pointer.</summary>
    public void ShowLauncher()
    {
        if (_launcher is null || _quitting)
            return;
        TryPlatform(MacActivation.ActivateIgnoringOtherApps, "activate the app");
        WindowsAppTheme.Apply(Application.Current); // Cheap; catches an app-mode switch made while we were hidden.
        _launcher.ShowLauncher();
        // Re-opening Valafind is the natural thing to try after installing the model: look right away.
        if (_launcherVm.ModelState is ModelState.Missing or ModelState.Failed)
            _ = Task.Run(_modelWatcher.Poll);
    }

    /// <summary>Opens (or focuses) the settings window.</summary>
    public void ShowSettings()
    {
        if (_quitting)
            return;
        _launcher?.HideLauncher();
        TryPlatform(MacActivation.ActivateIgnoringOtherApps, "activate the app");
        WindowsAppTheme.Apply(Application.Current);
        if (_settingsWindow is { } existing)
        {
            existing.Activate();
            return;
        }
        var vm = new SettingsViewModel(_settings, new SettingsHost(this)) { Updater = _updater };
        var window = new SettingsWindow(vm);
        window.Closed += (_, _) =>
        {
            vm.Detach();
            _settingsWindow = null;
        };
        _settingsWindow = window;
        window.Show();
        window.Activate();
    }

    // ------------------------------------------------------------------ installation and updates

    /// <summary>
    /// The installed copy starts at login: switched on when Valafind was just installed, and pointed at the installed
    /// executable when it was switched on for a copy elsewhere.
    /// </summary>
    private void ConfigureInstalledCopy()
    {
        if (Installation.Startup is not { IsInstalledCopy: true } installed)
            return;
        try
        {
            if (installed.FirstInstall || Autostart.IsEnabled())
                Autostart.SetEnabled(true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            Log.PlatformCallFailed(_log, "set up start at login", e);
        }
    }

    private void StartUpdater()
    {
        _updateHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _updateHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Valafind/" + ReleaseVersion.Current());
        _updater = new Updater(
            Installation.App,
            RunningApp.Current(),
            Installation.Layout(),
            _updateHttp,
            new Installation.UpdateLog(_loggerFactory.CreateLogger<Updater>()),
            () => _settings.AutomaticUpdates);
        _updater.UpdateReady += (_, _) => Dispatcher.UIThread.Post(RestartWhenIdle);
        _updater.Start(FirstUpdateCheck, UpdateCheckInterval);
    }

    /// <summary>Checks for an update now and shows the result in the settings window.</summary>
    public void CheckForUpdates()
    {
        if (_updater is not { IsSupported: true })
        {
            Shell(p => ShellIntegration.OpenUrl(new Uri(p)), Installation.App.ReleasesPage.AbsoluteUri, "open the releases page");
            return;
        }
        ShowSettings();
        _ = _updater.CheckNowAsync();
    }

    /// <summary>Restarts into the new version once the launcher and the settings window are closed.</summary>
    private void RestartWhenIdle()
    {
        if (_updater is not { State: UpdateState.ReadyToRestart } || _quitting)
            return;
        if (_launcher?.IsVisible == true || _settingsWindow is not null)
        {
            _restartTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => RestartWhenIdle());
            _restartTimer.Start();
            return;
        }

        _restartTimer?.Stop();
        try
        {
            // The new copy waits for this process to exit, then runs in the background like a start at login.
            _updater.Restart([Autostart.BackgroundArgument]);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log.PlatformCallFailed(_log, "start the updated Valafind", e);
            return;
        }
        Quit();
    }

    private void OnInstanceCommand(object? sender, string command) =>
        Dispatcher.UIThread.Post(() =>
        {
            switch (command.Trim().TrimStart('-').ToLowerInvariant())
            {
                case "toggle":
                    ToggleLauncher();
                    break;
                case "show":
                    ShowLauncher();
                    break;
                case "quit":
                    Quit();
                    break;
                default:
                    Log.UnknownCommand(_log, command);
                    break;
            }
        });

    private void OnLauncherPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LauncherViewModel.IsLauncherVisible))
            SetInteractive(_core, _launcherVm.IsLauncherVisible);
    }

    // While the launcher is on screen, indexing starts no new model batches so query embedding never waits behind it.
    private void SetInteractive(CoreServices? core, bool interactive)
    {
        if (core is null)
            return;
        try
        {
            core.Indexing.SetInteractive(interactive);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.SetInteractiveFailed(_log, interactive, e);
        }
    }

    // ------------------------------------------------------------------ hotkey

    /// <summary>
    /// (Re-)registers the configured hotkey off the UI thread (the Wayland portal can take seconds to answer) and shows
    /// the outcome. Registrations run one at a time, in order. Completes with the error shown to the user, or null.
    /// </summary>
    private Task<string?> RegisterHotkeyAsync()
    {
        var chord = _settings.EffectiveHotkey;
        var task = RegisterAfterAsync(_hotkeyTask, chord);
        _hotkeyTask = task;
        return task;
    }

    private async Task<string?> RegisterAfterAsync(Task previous, string chord)
    {
        try
        {
            await previous.ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Reported by that registration.
        }
        if (_quitting)
            return null;

        var error = await Task.Run(() => RegisterOnWorker(chord)).ConfigureAwait(true);
        if (error is not null)
            Log.HotkeyUnavailable(_log, chord, error);
        if (!_quitting)
        {
            _launcherVm.SetHotkey(chord, error);
            _tray?.SetHotkey(chord, error is null);
        }
        return error;
    }

    private string? RegisterOnWorker(string chord)
    {
        try
        {
            var hotkeys = _hotkeys ??= GlobalHotkeyFactory.Create();
            // TryRegister replaces the previous chord and keeps it when the new one fails; no Unregister first, so
            // re-registering the same chord takes the services' fast path (no new Wayland portal session/dialog).
            if (hotkeys.TryRegister(chord, OnHotkeyPressed, out var error))
                return null;
            return string.IsNullOrWhiteSpace(error)
                ? Loc.Format(Strings.HotkeyCouldNotRegisterBind, chord, GlobalHotkeyFactory.ToggleCommand)
                : error;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.HotkeyRegistrationFailed(_log, e);
            return Loc.Format(Strings.HotkeyUnavailableBind, GlobalHotkeyFactory.ToggleCommand);
        }
    }

    // The platform may call this on its own thread.
    private void OnHotkeyPressed() => Dispatcher.UIThread.Post(ToggleLauncher);

    // ------------------------------------------------------------------ core services

    private async Task LoadCoreAsync(CancellationToken ct)
    {
        _launcherVm.SetModelLoading();
        _launcherVm.UpdateStatus(IndexStatus.Initial);
        string? modelDir;
        string? signature = null;
        Exception? unpackError = null;
        try
        {
            (modelDir, signature) = await Task.Run(() =>
            {
                try
                {
                    var written = CoreServicesFactory.InstallBundledModel(_settings.ModelDirectory, ct);
                    if (written > 0)
                        Log.BundledModelInstalled(_log, written, CoreServicesFactory.DefaultModelDirectory);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.BundledModelInstallFailed(_log, CoreServicesFactory.DefaultModelDirectory, e);
                    unpackError = e;
                }
                var dir = CoreServicesFactory.LocateModel(_settings.ModelDirectory);
                return (dir, dir is null ? null : CoreServicesFactory.ModelSignature(_settings.ModelDirectory));
            }, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.ModelLookupFailed(_log, e);
            modelDir = null;
        }

        if (modelDir is null && unpackError is not null)
        {
            _launcherVm.SetModelFailed(Loc.Format(Strings.ModelUnpackFailed, CoreServicesFactory.DefaultModelDirectory, unpackError.Message));
            _launcherVm.UpdateStatus(IndexStatus.Initial with { State = IndexingState.ModelUnavailable });
            if (!ct.IsCancellationRequested && !_quitting)
                _modelWatcher.Start(baseline: null);
            return;
        }

        if (modelDir is null)
        {
            Log.ModelMissing(_log);
            _launcherVm.SetModelMissing(CoreServicesFactory.ExpectedModelDirectory(_settings),
                customTarget: !string.IsNullOrWhiteSpace(_settings.ModelDirectory)
                              || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VALAFIND_DATA_DIR")));
            _launcherVm.UpdateStatus(IndexStatus.Initial with { State = IndexingState.ModelUnavailable });
            if (!ct.IsCancellationRequested && !_quitting)
                _modelWatcher.Start(baseline: null);
            return;
        }

        CoreServices core;
        try
        {
            var sw = Stopwatch.StartNew();
            core = await Task.Run(() => CoreServicesFactory.Create(modelDir, _settings, _loggerFactory), ct).ConfigureAwait(true);
            Log.CoreReady(_log, sw.ElapsedMilliseconds, core.Model.ModelId);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            Log.CoreFailed(_log, e);
            _launcherVm.SetModelFailed(e.Message);
            _launcherVm.UpdateStatus(IndexStatus.Initial with { State = IndexingState.ModelUnavailable });
            // Retry only once the files change (re-run fetch script, another folder), not every few seconds.
            if (!ct.IsCancellationRequested && !_quitting)
                _modelWatcher.Start(baseline: signature);
            return;
        }

        if (ct.IsCancellationRequested || _quitting)
        {
            await core.DisposeAsync().ConfigureAwait(true);
            return;
        }

        _core = core;
        core.Indexing.StatusChanged += OnIndexStatusChanged;
        _launcherVm.AttachSearch(core.Search);
        _launcherVm.UpdateStatus(core.Indexing.Status);
        if (_settings.UserPaused)
            core.Indexing.SetUserPaused(true);
        if (_launcherVm.IsLauncherVisible)
            SetInteractive(core, true);

        try
        {
            await core.Indexing.StartAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Shutting down or restarting.
        }
        catch (Exception e)
        {
            Log.IndexingFailed(_log, e);
        }
    }

    // Runs only inside _lifecycle.
    private async Task StopCoreCoreAsync()
    {
        _modelWatcher.Stop();
        if (!_quitting)
        {
            // Before anything is disposed: stop routing searches to the old Core and drop its in-flight results.
            _launcherVm.SetModelLoading();
        }
        await _coreCts.CancelAsync().ConfigureAwait(true);
        try
        {
            await _coreTask.ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.CoreTaskFailed(_log, e);
        }
        var core = _core;
        _core = null;
        if (core is null)
            return;
        core.Indexing.StatusChanged -= OnIndexStatusChanged;
        try
        {
            await core.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.CoreDisposeFailed(_log, e);
        }
    }

    /// <summary>Restarts the Core services (model/index settings changed, model installed). Serialized with every other
    /// stop/restart, so overlapping calls never leave two Cores running.</summary>
    private Task RestartCoreAsync() => _lifecycle.Enqueue(async () =>
    {
        if (_quitting)
            return;
        await StopCoreCoreAsync().ConfigureAwait(true);
        if (_quitting)
            return;
        var old = _coreCts;
        _coreCts = new CancellationTokenSource();
        old.Dispose();
        _coreTask = LoadCoreAsync(_coreCts.Token);
    });

    private void OnModelFilesChanged()
    {
        if (_quitting || _launcherVm.ModelState is not (ModelState.Missing or ModelState.Failed))
            return;
        Log.ModelChanged(_log);
        _ = RestartCoreAsync();
    }

    // "Check again": an immediate probe; when nothing changed the model state stays as it is.
    private void CheckModel()
    {
        if (_quitting)
            return;
        if (_launcherVm.ModelState is ModelState.Missing)
        {
            // Cheap and always worth it: a full reload shows "Loading…" and then the current state.
            _ = RestartCoreAsync();
            return;
        }
        if (_launcherVm.ModelState is ModelState.Failed)
        {
            // Built-in model: a reload re-unpacks any damaged file (cheap when the files are fine).
            if (_launcherVm.IsModelBundled)
                _ = RestartCoreAsync();
            else
                _ = Task.Run(_modelWatcher.Poll);
        }
    }

    // Raised on a background thread, possibly very often: coalesce into one UI update at a time.
    private void OnIndexStatusChanged(object? sender, IndexStatus status)
    {
        Volatile.Write(ref _pendingStatus, status);
        if (Interlocked.Exchange(ref _statusPostQueued, 1) == 1)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _statusPostQueued, 0);
            if (Volatile.Read(ref _pendingStatus) is { } latest && !_quitting)
                _launcherVm.UpdateStatus(latest);
        }, DispatcherPriority.Background);
    }

    private void TogglePause() => SetUserPaused(!_settings.UserPaused);

    // The persisted setting is the single source of truth for the tray and the launcher toggles.
    private void SetUserPaused(bool paused)
    {
        _settings.UserPaused = paused;
        TrySaveSettings();
        _launcherVm.SetUserPaused(paused);
        _core?.Indexing.SetUserPaused(paused);
    }

    private void ResumeNow() => _core?.Indexing.OverrideThrottle(LauncherViewModel.ResumeNowDuration);

    private void TrySaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.SettingsSaveFailed(_log, e);
        }
    }

    // ------------------------------------------------------------------ shutdown

    /// <summary>Stops indexing, removes the tray icon and exits.</summary>
    public void Quit()
    {
        if (_quitting)
            return;
        _quitting = true;
        _ = QuitAsync();
    }

    private async Task QuitAsync()
    {
        try
        {
            await DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.ShutdownFailed(_log, e);
        }
        finally
        {
            _desktop?.Shutdown();
        }
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        // OS session end / app termination: release the index and model before the process goes away. The teardown
        // resumes on the UI thread, so wait in a nested dispatcher frame — a plain Task.Wait here would deadlock until
        // the timeout and leave the Core undisposed.
        _quitting = true;
        var task = DisposeAsync().AsTask();
        if (!DispatcherWait.WaitPumping(task, ShutdownTimeout))
            Log.ShutdownTimedOut(_log, (long)ShutdownTimeout.TotalMilliseconds);
        else if (task.Exception is { } ex)
            Log.ShutdownFailed(_log, ex);
    }

    private bool _disposed;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            // A second caller (Quit while the OS ends the session) waits for the teardown already in progress.
            await _lifecycle.Idle.ConfigureAwait(true);
            return;
        }
        _disposed = true;
        _quitting = true;
        Log.ShuttingDown(_log);
        if (_instance is not null)
            _instance.CommandReceived -= OnInstanceCommand;
        _launcherVm.PropertyChanged -= OnLauncherPropertyChanged;
        _modelWatcher.Dispose();
        try
        {
            _hotkeys?.Dispose();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.PlatformCallFailed(_log, "dispose hotkey", e);
        }
        _tray?.Dispose();
        _restartTimer?.Stop();
        _updater?.Dispose();
        _updateHttp?.Dispose();
        _settingsWindow?.Close();
        _launcher?.CloseForShutdown();
        _launcherVm.Dispose();
        await _lifecycle.Enqueue(async () =>
        {
            await StopCoreCoreAsync().ConfigureAwait(true);
            _coreCts.Dispose();
            _instance?.Dispose();
            await _services.DisposeAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private void TryPlatform(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.PlatformCallFailed(_log, what, e);
        }
    }

    // ------------------------------------------------------------------ adapters

    private sealed class LauncherActions(AppHost host) : ILauncherActions
    {
        public void Open(string path) => host.Shell(ShellIntegration.Open, path, "open");

        public void Reveal(string path) => host.Shell(ShellIntegration.Reveal, path, "reveal");

        public void QuickLook(string path)
        {
            if (!OperatingSystem.IsMacOS())
                return;
            try
            {
                var psi = new ProcessStartInfo("/usr/bin/qlmanage") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(path);
                using var process = Process.Start(psi);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.ShellFailed(host._log, "quick-look", e);
                Log.ShellFailedPath(host._log, "quick-look", path);
            }
        }

        public async Task CopyTextAsync(string text)
        {
            if (host._launcher?.Clipboard is not { } clipboard)
                return;
            try
            {
                await clipboard.SetTextAsync(text).ConfigureAwait(true);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.ClipboardFailed(host._log, e);
            }
        }

        public void HideLauncher() => host._launcher?.HideLauncher();

        public void ShowSettings() => host.ShowSettings();

        public void Quit() => host.Quit();

        public void SetUserPaused(bool paused) => host.SetUserPaused(paused);

        public void ResumeNow() => host.ResumeNow();

        public void CheckModel() => host.CheckModel();
    }

    private void Shell(Action<string> action, string path, string verb)
    {
        try
        {
            action(path);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.ShellFailed(_log, verb, e);
            Log.ShellFailedPath(_log, verb, path);
        }
    }

    private sealed class SettingsHost(AppHost host) : ISettingsHost
    {
        public string? DetectModel(string? configuredDirectory) => CoreServicesFactory.LocateModel(configuredDirectory);

        public string DefaultModelDirectory => CoreServicesFactory.DefaultModelDirectory;

        public string AppDirectory => AppContext.BaseDirectory;

        public string LogDirectory => AppPaths.LogDirectory;

        public void OpenLogFolder()
        {
            try
            {
                AppPaths.EnsurePrivateDirectory(AppPaths.LogDirectory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.PlatformCallFailed(host._log, "create the log folder", e);
            }
            host.Shell(ShellIntegration.Open, AppPaths.LogDirectory, "open the log folder");
        }

        public bool IsAutostartEnabled() => Autostart.IsEnabled();

        public void SetAutostartEnabled(bool enabled) => Autostart.SetEnabled(enabled);

        public async Task<string?> ApplyAsync(SettingsSnapshot before, ValafindSettings after)
        {
            host._launcherVm.RefreshIndexScope();
            // Saving while the model is missing/failed is also how users say "I fixed it": always reload then.
            if (before.RequiresCoreRestart(after) || host._launcherVm.ModelState is ModelState.Missing or ModelState.Failed)
                _ = host.RestartCoreAsync();
            else
                host._launcherVm.UpdateStatus(host._core?.Indexing.Status ?? host._launcherVm.Status.Raw);

            if (!string.Equals(before.Hotkey, after.EffectiveHotkey, StringComparison.OrdinalIgnoreCase) || host._launcherVm.HasHotkeyError)
                return await host.RegisterHotkeyAsync().ConfigureAwait(true);
            return null;
        }
    }
}
