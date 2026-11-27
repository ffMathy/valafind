using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Valafind.App.Services;
using Valafind.App.Tests.Headless;
using Valafind.App.ViewModels;
using Valafind.App.Views;
using Xunit;

namespace Valafind.App.Tests;

/// <summary>How the app under test starts: its design, its language, and whether the model is in place.</summary>
public sealed record AppOptions
{
    /// <summary>The design: Linux (the default), Windows or Mac, as VALAFIND_UI chooses on any system.</summary>
    public UiFlavor Flavor { get; init; } = UiFlavor.Linux;

    /// <summary>The system's display language, as a culture name such as "ar" or "de"; English when null.</summary>
    public string? Language { get; init; }

    /// <summary>A build without the model, started with an empty data folder: the launcher shows how to install it.</summary>
    public bool WithoutModel { get; init; }
}

/// <summary>
/// The real Valafind on Avalonia's headless platform, rendered with Skia: the real app host, index, model, search,
/// launcher and settings window, in a home folder and a data folder of its own. Only the desktop is missing: no window
/// manager, tray, global hotkey (taken as registered) or file manager (files and links Valafind opens are recorded
/// instead, in <see cref="Opened"/>). A check prepares files in <see cref="Home"/>, starts the app, and uses it as a user would.
/// </summary>
/// <remarks>
/// Start it with <see cref="RunAsync"/>, which runs the check on the shared headless UI thread; await everything there.
/// The model comes from VALAFIND_TEST_MODEL_DIR, or the repository's .model/embeddinggemma-2; a check that needs it is
/// skipped where neither has it, as the other model tests are (CI runs them with the model in its model job).
/// </remarks>
public sealed class AppHarness : IAsyncDisposable
{
    /// <summary>How long a check waits for something the app does on its own, such as indexing new files.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private static readonly string Repository = FindRepository();
    private static readonly string Fixtures = Path.Combine(Repository, "tests", "Valafind.Core.Tests", "Fixtures", "Media");
    private readonly Dictionary<string, string?> savedEnvironment = [];
    private AppHost? host;

    private AppHarness(AppOptions options)
    {
        Options = options;
        Root = Path.Combine(Path.GetTempPath(), "valafind-ui-" + Guid.NewGuid().ToString("N"));
        Home = Path.Combine(Root, "home");
        Data = Path.Combine(Root, "data");
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Path.Combine(Data, "models"));
    }

    public AppOptions Options { get; }

    /// <summary>A folder of its own for this check, removed afterwards.</summary>
    public string Root { get; }

    /// <summary>The user's home folder: what Valafind indexes. Save files here, before or after starting.</summary>
    public string Home { get; }

    /// <summary>Valafind's data folder: settings, index, logs and models.</summary>
    public string Data { get; }

    /// <summary>The files, folders and links Valafind asked the system to open, one per line.</summary>
    public IReadOnlyList<string> Opened => File.Exists(OpenedLog) ? File.ReadAllLines(OpenedLog) : [];

    /// <summary>The launcher's view model: what it shows, the results, the filters.</summary>
    public LauncherViewModel Launcher => Host.Launcher;

    /// <summary>The launcher's window, once the app started.</summary>
    public Window LauncherWindow => Host.LauncherWindow ?? throw new InvalidOperationException("Start the app first.");

    /// <summary>The settings window while it is open.</summary>
    public Window? SettingsWindow => Host.SettingsWindow;

    private AppHost Host => host ?? throw new InvalidOperationException("Start the app first: await app.StartAsync().");

    private string OpenedLog => Path.Combine(Root, "opened.txt");

    /// <summary>Where the model is, or null when this machine has none.</summary>
    public static string? ModelDirectory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("VALAFIND_TEST_MODEL_DIR");
            var candidate = string.IsNullOrEmpty(configured) ? Path.Combine(Repository, ".model", "embeddinggemma-2") : configured;
            return File.Exists(Path.Combine(candidate, "onnx", "model_quantized.onnx")) ? candidate : null;
        }
    }

    /// <summary>Runs <paramref name="test"/> on the headless UI thread with a fresh app, set up as <paramref name="options"/> say.</summary>
    public static Task RunAsync(HeadlessFixture fixture, Func<AppHarness, Task> test, AppOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(test);
        options ??= new AppOptions();
        if (!options.WithoutModel && ModelDirectory is null)
            Assert.Skip("Needs the model: set VALAFIND_TEST_MODEL_DIR, or run tools/fetch-model.sh .model/embeddinggemma-2.");
        return fixture.Session.Dispatch(() =>
        {
            // The check awaits on the UI thread: keep its dispatcher running until the check is done.
            var run = RunCheckAsync(options, test);
            var frame = new DispatcherFrame();
            _ = run.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
            run.GetAwaiter().GetResult();
        }, TestContext.Current.CancellationToken);
    }

    private static async Task RunCheckAsync(AppOptions options, Func<AppHarness, Task> test)
    {
        await using var app = new AppHarness(options);
        await test(app).ConfigureAwait(true);
    }

    /// <summary>Starts Valafind, as at login with its launcher shown, and waits until the launcher is up.</summary>
    public async Task StartAsync()
    {
        if (!Options.WithoutModel)
            File.CreateSymbolicLink(Path.Combine(Data, "models", "embeddinggemma-2"), ModelDirectory!);
        SetEnvironment("HOME", Home);
        SetEnvironment("XDG_DATA_HOME", Path.Combine(Home, ".local", "share"));
        SetEnvironment("XDG_CONFIG_HOME", Path.Combine(Home, ".config"));
        SetEnvironment("VALAFIND_DATA_DIR", Data);
        SetEnvironment("VALAFIND_NO_INSTALL", "1");
        SetEnvironment("VALAFIND_NO_UPDATE", "1");
        SetEnvironment(UiFlavors.OverrideVariable, Options.Flavor.ToString().ToLowerInvariant());
        // A stand-in for the file manager and the browser: what Valafind opens is written to a line of OpenedLog.
        var bin = Path.Combine(Root, "bin");
        Directory.CreateDirectory(bin);
        var opener = Path.Combine(bin, "xdg-open");
        File.WriteAllText(opener, $"#!/bin/sh\nprintf '%s\\n' \"$@\" >> '{OpenedLog}'\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(opener, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        SetEnvironment("PATH", bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        if (Options.Language is { } language)
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo(language);
            System.Globalization.CultureInfo.CurrentUICulture = culture;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        }

        host = AppHost.Create(null, "show", NullLoggerFactory.Instance);
        host.Start(Application.Current!, new ClassicDesktopStyleApplicationLifetime());
        await WaitUntilAsync(() => Host.LauncherWindow?.IsVisible == true, "the launcher opened").ConfigureAwait(true);

        // There is no desktop session here, so the global hotkey cannot be registered: take it as registered, as on a
        // desktop, rather than show the warning a session without a display gets.
        await Host.HotkeyRegistration.ConfigureAwait(true);
        Launcher.SetHotkey(Host.Settings.EffectiveHotkey, null);
        await SettleAsync().ConfigureAwait(true);
    }

    /// <summary>Waits until every file in the home folder is indexed: the launcher's status is "Up to date".</summary>
    public async Task WaitUntilIndexedAsync()
    {
        await WaitUntilAsync(() => Launcher.Status.IsUpToDate, "every file was indexed").ConfigureAwait(true);
        await SettleAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Types <paramref name="query"/> into the launcher's search box and waits for its results. Files saved shortly
    /// before may not be indexed yet: use <see cref="SearchUntilAsync"/> to search again until they are.
    /// </summary>
    public async Task SearchAsync(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ShowLauncher();
        Launcher.Query = query;
        await WaitUntilAsync(() => Launcher.LastQuery == query.Trim() && !Launcher.IsSearching, "the search answered").ConfigureAwait(true);
        await SettleAsync().ConfigureAwait(true);
    }

    /// <summary>Searches for <paramref name="query"/> again and again, as the index catches up, until <paramref name="done"/> holds.</summary>
    public async Task SearchUntilAsync(string query, Func<bool> done)
    {
        ArgumentNullException.ThrowIfNull(done);
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            Launcher.Query = string.Empty;
            await SettleAsync().ConfigureAwait(true);
            await SearchAsync(query).ConfigureAwait(true);
            if (done())
                return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Searching for \"{query}\" never showed what was expected. It showed: {string.Join(", ", ResultNames)}");
            await Task.Delay(2000).ConfigureAwait(true);
        }
    }

    /// <summary>The names of the files the launcher lists, top first.</summary>
    public IReadOnlyList<string> ResultNames => Launcher.Results.Select(result => result.FileName).ToArray();

    /// <summary>Opens the launcher, as the tray icon or the hotkey does.</summary>
    public void ShowLauncher()
    {
        if (!LauncherWindow.IsVisible)
            Host.ShowLauncher();
    }

    /// <summary>Presses <paramref name="key"/> in the launcher (with <paramref name="modifiers"/>), as on the keyboard.</summary>
    public async Task PressAsync(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        LauncherWindow.KeyPress(key, modifiers, PhysicalKey.None, null);
        LauncherWindow.KeyRelease(key, modifiers, PhysicalKey.None, null);
        await SettleAsync().ConfigureAwait(true);
    }

    /// <summary>Opens the settings window, as the tray's or the launcher's "Settings…" does (the same window when it is open).</summary>
    public async Task<Window> OpenSettingsAsync()
    {
        Host.ShowSettings();
        await WaitUntilAsync(() => Host.SettingsWindow?.IsVisible == true, "the settings window opened").ConfigureAwait(true);
        await SettleAsync().ConfigureAwait(true);
        return Host.SettingsWindow!;
    }

    /// <summary>Pauses (or resumes) indexing, as "Pause indexing" in the tray or the launcher's menu does.</summary>
    public async Task SetPausedAsync(bool paused)
    {
        Launcher.Actions!.SetUserPaused(paused);
        await SettleAsync().ConfigureAwait(true);
    }

    /// <summary>Saves a text file at <paramref name="relativePath"/> in the home folder, changed at <paramref name="modified"/> (now by default).</summary>
    public string SaveText(string relativePath, string text, DateTime? modified = null)
    {
        var path = HomePath(relativePath);
        File.WriteAllText(path, text);
        if (modified is { } when)
            File.SetLastWriteTime(path, when);
        return path;
    }

    /// <summary>Saves a file of these bytes at <paramref name="relativePath"/> in the home folder.</summary>
    public string SaveBytes(string relativePath, byte[] bytes)
    {
        var path = HomePath(relativePath);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Saves a file of <paramref name="length"/> random bytes, as a file no reader understands.</summary>
    public string SaveRandomBytes(string relativePath, int length = 4096) => SaveBytes(relativePath, RandomBytes(length));

    /// <summary>A picture drawn with ImageMagick, in the format its extension names: a red circle, a blue square, or a word.</summary>
    public string SavePicture(string relativePath, Picture what, string? word = null)
    {
        var draw = what switch
        {
            Picture.RedCircle => new[] { "-size", "320x240", "xc:white", "-fill", "red", "-draw", "circle 160,120 160,40" },
            Picture.BlueSquare => ["-size", "320x240", "xc:white", "-fill", "blue", "-draw", "rectangle 90,50 230,190"],
            _ => ["-size", "640x200", "xc:white", "-font", "DejaVu-Sans-Bold", "-pointsize", "90", "-fill", "black", "-gravity", "center", "-annotate", "+0+0", word ?? throw new ArgumentNullException(nameof(word))],
        };
        var path = HomePath(relativePath);
        Run("convert", [.. draw, path]);
        return path;
    }

    /// <summary>
    /// Copies a test fixture into the home folder: speech.wav and speech.mp3 read out a weather forecast; the others are
    /// in tests/Valafind.Core.Tests/Fixtures/Media.
    /// </summary>
    public string SaveFixture(string fixture, string relativePath)
    {
        var path = HomePath(relativePath);
        File.Copy(Path.Combine(Fixtures, fixture), path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Runs ffmpeg with <paramref name="arguments"/>, writing to <paramref name="relativePath"/> in the home folder. The
    /// arguments may be written as on a command line, several in one string split at spaces. An argument "fixture:x"
    /// names a test fixture, "home:x" a file in the home folder.
    /// </summary>
    public string SaveWithFfmpeg(string relativePath, params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var path = HomePath(relativePath);
        var resolved = arguments.SelectMany(part => part.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Select(argument => argument.StartsWith("fixture:", StringComparison.Ordinal)
            ? Path.Combine(Fixtures, argument[8..])
            : argument.StartsWith("home:", StringComparison.Ordinal) ? Path.Combine(Home, argument[5..]) : argument);
        Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", .. resolved, path]);
        return path;
    }

    /// <summary>Saves a document (docx, xlsx, pptx, odt, epub, rtf, pdf, eml, mbox, ipynb) holding these paragraphs, as its app would.</summary>
    public string SaveDocument(string relativePath, string format, params string[] paragraphs)
    {
        var path = HomePath(relativePath);
        Run("python3", ["-I", Path.Combine(Repository, "checks", "e2e", "kinds", "make-file.py"), format, path, System.Text.Json.JsonSerializer.Serialize(paragraphs)]);
        return path;
    }

    /// <summary>A short random code, to make a name or a phrase that nothing else contains.</summary>
    public static string Unique() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Every piece of text <paramref name="root"/> shows, in reading order, as rendered.</summary>
    public static IReadOnlyList<string> Texts(Visual root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return root.GetVisualDescendants()
            .Where(visual => visual.IsEffectivelyVisible)
            .Select(visual => visual switch
            {
                TextBlock block => block.Text ?? string.Concat(block.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(run => run.Text) ?? []),
                Avalonia.Controls.Presenters.TextPresenter presenter => presenter.Text,
                _ => null,
            })
            .Where(text => !string.IsNullOrEmpty(text))
            .Select(text => text!)
            .ToArray();
    }

    /// <summary>The visible controls of type <typeparamref name="T"/> inside <paramref name="root"/>.</summary>
    public static IReadOnlyList<T> Find<T>(Visual root)
        where T : Visual
    {
        ArgumentNullException.ThrowIfNull(root);
        return root.GetVisualDescendants().OfType<T>().Where(visual => visual.IsEffectivelyVisible).ToArray();
    }

    /// <summary>
    /// Saves what <paramref name="window"/> shows as a PNG, when the environment variable AIL_SHOTS_DIR is set (ail sets
    /// it to collect each scenario's final screen): into that folder, as <paramref name="name"/> (the file name ail gives,
    /// such as "the-launcher-at-rest.png"; a scenario's title is turned into it). Does nothing otherwise.
    /// </summary>
    public static void SaveScreen(Window window, string name)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(name);
        var folder = Environment.GetEnvironmentVariable("AIL_SHOTS_DIR");
        if (string.IsNullOrEmpty(folder))
            return;
        var file = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? name : ShotName(name);
        TextOptions.SetTextRenderingMode(window, TextRenderingMode.Antialias);
        RenderOptions.SetBitmapInterpolationMode(window, BitmapInterpolationMode.HighQuality);
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The window rendered nothing.");
        Directory.CreateDirectory(folder);
        frame.Save(Path.Combine(folder, file), new PngBitmapEncoderOptions());
    }

    /// <summary>The file name ail gives a scenario's screen: its title in lower case, each run of other characters one "-".</summary>
    public static string ShotName(string scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var slug = Regex.Replace(scenario.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return (slug.Length > 60 ? slug[..60] : slug is "" ? "test" : slug) + ".png";
    }

    /// <summary>Waits until <paramref name="condition"/> holds, letting the UI thread work meanwhile.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var deadline = DateTime.UtcNow + (timeout ?? Timeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Timed out waiting until " + what);
            await Task.Delay(50).ConfigureAwait(true);
        }
    }

    /// <summary>Lets the app finish what it started: layout, rendering, posted work.</summary>
    public static async Task SettleAsync()
    {
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(25).ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (host is not null)
        {
            Host.LauncherWindow?.CloseForShutdown();
            Host.SettingsWindow?.Close();
            await host.DisposeAsync().ConfigureAwait(true);
        }

        foreach (var (name, value) in savedEnvironment)
            Environment.SetEnvironmentVariable(name, value);
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void SetEnvironment(string name, string value)
    {
        savedEnvironment.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    private string HomePath(string relativePath)
    {
        var path = Path.Combine(Home, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private static void Run(string program, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException(program + " did not start.");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{program} failed: {error}");
    }

    private static string FindRepository()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Valafind.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("The repository was not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>What <see cref="AppHarness.SavePicture"/> draws.</summary>
public enum Picture
{
    RedCircle,
    BlueSquare,
    Word,
}
