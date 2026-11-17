using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Valafind.App.Platform;
using Valafind.App.Services;
using Valafind.App.ViewModels;
using Xunit;

namespace Valafind.App.Tests.Services;

public sealed class ProgramTests
{
    [Theory]
    [InlineData("--toggle", "toggle")]
    [InlineData("-s", "show")]
    [InlineData("--quit", "quit")]
    [InlineData("--background", "background")] // what the autostart entry passes
    [InlineData("--unknown", null)]
    public void ParsesCommands(string arg, string? expected) =>
        Assert.Equal(expected, Program.ParseCommand([arg]));

    [Fact]
    public void UiStartFailureBecomesAClearMessageAndExitCode1()
    {
        // Regression (no-display-crash): "XOpenDisplay failed" escaped Main and aborted the process (exit 134).
        var stderr = new StringWriter();
        var log = new ListLogger();
        var code = Program.RunGuarded(() => throw new InvalidOperationException("XOpenDisplay failed"), log, stderr, () => false);

        Assert.Equal(1, code);
        Assert.Contains("could not start its user interface", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("XOpenDisplay failed", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("log folder", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(LogLevel.Critical, Assert.Single(log.Entries).Level);
    }

    [Fact]
    public void CrashAfterStartIsReportedDifferently()
    {
        var stderr = new StringWriter();
        var code = Program.RunGuarded(() => throw new InvalidOperationException("late"), NullLogger.Instance, stderr, () => true);
        Assert.Equal(1, code);
        Assert.Contains("stopped after an unexpected error", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NormalExitCodeIsPassedThrough() =>
        Assert.Equal(0, Program.RunGuarded(() => 0, NullLogger.Instance, TextWriter.Null, () => true));

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}

public sealed class PlatformGuidanceTests
{
    [Theory]
    [InlineData("/opt/Valafind/Valafind", null, "/opt/Valafind/Valafind --toggle")]
    [InlineData("/home/u/My Apps/Valafind", null, "\"/home/u/My Apps/Valafind\" --toggle")]
    [InlineData("/usr/lib/dotnet/dotnet", "/src/bin/Valafind.dll", "/usr/lib/dotnet/dotnet /src/bin/Valafind.dll --toggle")]
    [InlineData(null, null, "Valafind --toggle")]
    public void ToggleCommandNamesTheRealExecutable(string? processPath, string? entry, string expected) =>
        // Regression (wayland-fallback-command-nonexistent): the guidance said "valafind --toggle", which nothing installs.
        Assert.Equal(expected, GlobalHotkeyFactory.BuildToggleCommand(processPath, entry));

    [Fact]
    public void WaylandMessageUsesTheProcessPath()
    {
        Assert.Contains(GlobalHotkeyFactory.ToggleCommand, GlobalHotkeyFactory.WaylandUnavailableMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("run: valafind --toggle", GlobalHotkeyFactory.WaylandUnavailableMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(":0", null, "x11", true)]
    [InlineData(":0", null, null, true)]
    [InlineData(":0", "wayland-0", "wayland", false)] // XWayland: stale pointer, use the fallback
    [InlineData(":0", "wayland-0", null, false)]
    [InlineData(null, null, null, false)]
    public void PointerLookupOnlyInRealX11Sessions(string? display, string? wayland, string? sessionType, bool expected)
    {
        var env = new Dictionary<string, string?>
        {
            ["DISPLAY"] = display,
            ["WAYLAND_DISPLAY"] = wayland,
            ["XDG_SESSION_TYPE"] = sessionType,
        };
        Assert.Equal(expected, PointerLocator.UseX11(name => env.GetValueOrDefault(name)));
    }

    [Theory]
    [InlineData("Alt+Space", true)]
    [InlineData("Alt+Shift+K", true)]
    [InlineData("Ctrl+Alt+Space", false)]
    [InlineData("Cmd+Alt+Space", false)]
    [InlineData("Ctrl+Space", false)]
    public void MacRefusesOptionOnlyChords(string chord, bool refused)
    {
        Assert.True(HotkeyChord.TryParse(chord, out var parsed, out _));
        Assert.Equal(refused, HotkeyChord.MacRestriction(parsed) is not null);
    }
}

public sealed class ModelInstallHelpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "valafind-model-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void FetchScriptsShipNextToTheExecutable()
    {
        // Regression (first-run-model-instructions-unusable): the screen pointed at tools/fetch-model.* which the
        // build output did not contain. The App's Content items are copied into every output that references it.
        var tools = Path.Combine(AppContext.BaseDirectory, ModelInstallHelp.ToolsFolder);
        Assert.True(File.Exists(Path.Combine(tools, "fetch-model.sh")), tools);
        Assert.True(File.Exists(Path.Combine(tools, "fetch-model.ps1")), tools);
        var windows = OperatingSystem.IsWindows();
        Assert.True(File.Exists(ModelInstallHelp.ScriptPath(windows, AppContext.BaseDirectory)));
    }

    [Theory]
    [InlineData(false, "/Applications/Valafind.app/Contents/MacOS", null, false,
        "bash '/Applications/Valafind.app/Contents/MacOS/tools/fetch-model.sh'")]
    [InlineData(false, "/opt/it's here/", "/data/my models", true,
        "bash '/opt/it'\\''s here/tools/fetch-model.sh' '/data/my models' --force")]
    [InlineData(true, @"C:\Program Files\Valafind", null, false,
        "powershell -ExecutionPolicy Bypass -File \"C:\\Program Files\\Valafind\\tools\\fetch-model.ps1\"")]
    [InlineData(true, @"C:\Valafind\", @"D:\models", true,
        "powershell -ExecutionPolicy Bypass -File \"C:\\Valafind\\tools\\fetch-model.ps1\" \"D:\\models\" -Force")]
    public void BuildsAbsoluteQuotedCommands(bool windows, string appDir, string? target, bool force, string expected) =>
        Assert.Equal(expected, ModelInstallHelp.FetchCommand(windows, appDir, target, force));

    [Fact]
    public void ModelSignatureAppearsWhenTheModelIsCompleteAndChangesWithItsFiles()
    {
        var onnx = Path.Combine(_dir, "onnx");
        Directory.CreateDirectory(onnx);
        Assert.Null(CoreServicesFactory.SignatureOf(_dir));

        File.WriteAllText(Path.Combine(_dir, "tokenizer.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{"model_type": "embedding_gemma2"}""");
        File.WriteAllBytes(Path.Combine(onnx, "model_quantized.onnx"), new byte[1024]);
        // Small graph without its external weights yet (the fetch script downloads them last): not installed.
        Assert.Null(CoreServicesFactory.SignatureOf(_dir));

        File.WriteAllBytes(Path.Combine(onnx, "model_quantized.onnx_data"), new byte[2048]);
        var first = CoreServicesFactory.SignatureOf(_dir);
        Assert.NotNull(first);
        Assert.Equal(first, CoreServicesFactory.SignatureOf(_dir));

        File.WriteAllBytes(Path.Combine(onnx, "model_quantized.onnx_data"), new byte[4096]);
        Assert.NotEqual(first, CoreServicesFactory.SignatureOf(_dir));
    }
}
