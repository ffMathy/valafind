using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Valafind.App.Platform;
using Valafind.App.ViewModels;
using Valafind.App.Views;
using Valafind.Core.Abstractions;
using Valafind.Core.Configuration;

namespace Valafind.App.Services;

/// <summary>
/// Composition root and application controller: settings, Core services (loaded in the background), launcher window,
/// tray icon, global hotkey and single-instance commands.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly ValafindSettings _settings;
    private readonly ILogger _log;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LauncherViewModel _launcherVm;
    private readonly SingleInstance? _instance;
    private readonly string? _startupCommand;
    private readonly bool _firstRun;

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private LauncherWindow? _launcher;
    private SettingsWindow? _settingsWindow;
    private TrayIconController? _tray;
    private IGlobalHotkeyService? _hotkeys;

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
    }

    /// <summary>Builds the host. <paramref name="instance"/> is this process's single-instance lock (may be null when
    /// the platform has none); <paramref name="startupCommand"/> is "show"/"toggle" when launched to show the UI.</summary>
    public static AppHost Create(SingleInstance? instance, string? startupCommand)
    {
        var firstRun = !File.Exists(AppPaths.SettingsFile);
        var settings = ValafindSettings.Load();
        var flavor = UiFlavors.Detect();
        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information))
            .AddSingleton(settings)
            .AddSingleton(sp => new LauncherViewModel(flavor, sp.GetRequiredService<ValafindSettings>()))
            .BuildServiceProvider();
        return new AppHost(services, instance, startupCommand, firstRun);
    }

    /// <summary>The launcher view model (for tests and diagnostics).</summary>
    public LauncherViewModel Launcher => _launcherVm;

    /// <summary>Wires everything up. Called once from <see cref="App.OnFrameworkInitializationCompleted"/>.</summary>
    public void Start(Application app, IClassicDesktopStyleApplicationLifetime desktop)
    {
        ArgumentNullException.ThrowIfNull(app);
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.ShutdownRequested += OnShutdownRequested;

        TryPlatform(MacActivation.UseAccessoryActivationPolicy, "switch to accessory activation policy");

        _launcher = new LauncherWindow(_launcherVm, _launcherVm.Flavor);
        _tray = new TrayIconController(app, ShowLauncher, TogglePause, ShowSettings, Quit);
        _launcherVm.Status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IndexStatusViewModel.StateText) or nameof(IndexStatusViewModel.PauseMenuText))
                _tray?.SetStatus(_launcherVm.Status.StateText, _launcherVm.Status.PauseMenuText);
        };
        _tray.SetStatus(_launcherVm.Status.StateText, _launcherVm.Status.PauseMenuText);

        RegisterHotkey();

        if (_instance is not null)
            _instance.CommandReceived += OnInstanceCommand;

        _coreTask = LoadCoreAsync(_coreCts.Token);

        if (_startupCommand is "show" or "toggle" || _firstRun)
            Dispatcher.UIThread.Post(ShowLauncher, DispatcherPriority.Background);
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
        _launcher.ShowLauncher();
    }

    /// <summary>Opens (or focuses) the settings window.</summary>
    public void ShowSettings()
    {
        if (_quitting)
            return;
        _launcher?.HideLauncher();
        TryPlatform(MacActivation.ActivateIgnoringOtherApps, "activate the app");
        if (_settingsWindow is { } existing)
        {
            existing.Activate();
            return;
        }
        var vm = new SettingsViewModel(_settings, new SettingsHost(this));
        var window = new SettingsWindow(vm);
        window.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = window;
        window.Show();
        window.Activate();
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

    // ------------------------------------------------------------------ hotkey

    /// <summary>(Re-)registers the configured hotkey; returns the error shown to the user, or null.</summary>
    private string? RegisterHotkey()
    {
        var chord = _settings.EffectiveHotkey;
        string? error;
        try
        {
            _hotkeys ??= GlobalHotkeyFactory.Create();
            _hotkeys.Unregister();
            if (_hotkeys.TryRegister(chord, OnHotkeyPressed, out error))
                error = null;
            else
                error = string.IsNullOrWhiteSpace(error)
                    ? $"Could not register {chord} — bind a system shortcut to: valafind --toggle"
                    : error;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.HotkeyRegistrationFailed(_log, e);
            error = "Global hotkey unavailable — bind a system shortcut to: valafind --toggle";
        }
        if (error is not null)
            Log.HotkeyUnavailable(_log, chord, error);
        _launcherVm.SetHotkey(chord, error);
        _tray?.SetHotkey(chord, error is null);
        return error;
    }

    // The platform may call this on its own thread.
    private void OnHotkeyPressed() => Dispatcher.UIThread.Post(ToggleLauncher);

    // ------------------------------------------------------------------ core services

    private async Task LoadCoreAsync(CancellationToken ct)
    {
        _launcherVm.SetModelLoading();
        _launcherVm.UpdateStatus(IndexStatus.Initial);
        string? modelDir;
        try
        {
            modelDir = await Task.Run(() => CoreServicesFactory.LocateModel(_settings.ModelDirectory), ct).ConfigureAwait(true);
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

        if (modelDir is null)
        {
            _launcherVm.SetModelMissing(CoreServicesFactory.ExpectedModelDirectory(_settings),
                customTarget: !string.IsNullOrWhiteSpace(_settings.ModelDirectory));
            _launcherVm.UpdateStatus(IndexStatus.Initial with { State = IndexingState.ModelUnavailable });
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

    private async Task StopCoreAsync()
    {
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

    /// <summary>Restarts the Core services (model/index settings changed).</summary>
    private async Task RestartCoreAsync()
    {
        await StopCoreAsync().ConfigureAwait(true);
        if (_quitting)
            return;
        _coreCts.Dispose();
        _coreCts = new CancellationTokenSource();
        _coreTask = LoadCoreAsync(_coreCts.Token);
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

    private void SetUserPaused(bool paused)
    {
        _settings.UserPaused = paused;
        TrySaveSettings();
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
        // OS session end: release resources synchronously enough to not corrupt the index.
        _quitting = true;
        try
        {
            DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex)
        {
            Log.ShutdownFailed(_log, ex);
        }
    }

    private bool _disposed;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _quitting = true;
        if (_instance is not null)
            _instance.CommandReceived -= OnInstanceCommand;
        try
        {
            _hotkeys?.Dispose();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.PlatformCallFailed(_log, "dispose hotkey", e);
        }
        _tray?.Dispose();
        _settingsWindow?.Close();
        _launcher?.CloseForShutdown();
        _launcherVm.Dispose();
        await StopCoreAsync().ConfigureAwait(true);
        _coreCts.Dispose();
        _instance?.Dispose();
        await _services.DisposeAsync().ConfigureAwait(true);
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
                Log.ShellFailed(host._log, "quick-look", path, e);
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
    }

    private void Shell(Action<string> action, string path, string verb)
    {
        try
        {
            action(path);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.ShellFailed(_log, verb, path, e);
        }
    }

    private sealed class SettingsHost(AppHost host) : ISettingsHost
    {
        public string? DetectModel(string? configuredDirectory) => CoreServicesFactory.LocateModel(configuredDirectory);

        public string DefaultModelDirectory => CoreServicesFactory.DefaultModelDirectory;

        public bool IsAutostartEnabled() => Autostart.IsEnabled();

        public void SetAutostartEnabled(bool enabled) => Autostart.SetEnabled(enabled);

        public string? Apply(SettingsSnapshot before, ValafindSettings after)
        {
            string? hotkeyError = null;
            if (!string.Equals(before.Hotkey, after.EffectiveHotkey, StringComparison.OrdinalIgnoreCase) || host._launcherVm.HasHotkeyError)
                hotkeyError = host.RegisterHotkey();
            if (before.RequiresCoreRestart(after))
                _ = host.RestartCoreAsync();
            else
                host._launcherVm.UpdateStatus(host._core?.Indexing.Status ?? host._launcherVm.Status.Raw);
            return hotkeyError;
        }
    }
}
