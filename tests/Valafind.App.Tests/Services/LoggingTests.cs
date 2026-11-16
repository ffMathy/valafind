using Microsoft.Extensions.Logging;
using Valafind.App.Services;
using Xunit;

namespace Valafind.App.Tests.Services;

public sealed class RollingFileLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "valafind-logs-" + Guid.NewGuid().ToString("N"));

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
    public void WritesDatedFileWithLevelCategoryAndException()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 11, 16, 14, 32, 5, TimeSpan.Zero));
        using (var provider = new RollingFileLoggerProvider(_dir, LogLevel.Information, time: time))
        {
            var log = provider.CreateLogger("Valafind.App.Services.AppHost");
            Write(log, LogLevel.Debug, "hidden detail");
            Write(log, LogLevel.Warning, "Hotkey unavailable");
            Write(log, LogLevel.Error, "Core failed", new InvalidOperationException("boom"));
            Assert.Equal(Path.Combine(_dir, "valafind-20261116.log"), provider.CurrentFile);
        }

        var text = File.ReadAllText(Path.Combine(_dir, "valafind-20261116.log"));
        Assert.DoesNotContain("hidden detail", text, StringComparison.Ordinal);
        Assert.Contains("2026-11-16T14:32:05.000Z WARN  Valafind.App.Services.AppHost: Hotkey unavailable", text, StringComparison.Ordinal);
        Assert.Contains("ERROR Valafind.App.Services.AppHost: Core failed", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: boom", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RollsWhenFullAndKeepsSevenFiles()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));
        using var provider = new RollingFileLoggerProvider(_dir, LogLevel.Information, maxFileBytes: 2048, retainedFiles: 7, time: time);
        var log = provider.CreateLogger("Test");
        var line = new string('x', 300);
        for (var day = 0; day < 10; day++)
        {
            for (var i = 0; i < 10; i++)
                Write(log, LogLevel.Information, line);
            time.Advance(TimeSpan.FromDays(1));
        }

        var files = Directory.GetFiles(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(7, files.Count);
        Assert.All(files, f => Assert.True(new FileInfo(Path.Combine(_dir, f!)).Length <= 2048, f));
        // Newest kept: the last day (Nov 10) with its roll-overs.
        Assert.Contains("valafind-20261110.log", files);
        Assert.Contains("valafind-20261110-1.log", files);
        Assert.DoesNotContain("valafind-20261101.log", files);
    }

    [Fact]
    public void SortKeyOrdersRollOversAfterTheirDay()
    {
        var names = new[] { "valafind-20261110-10.log", "valafind-20261110.log", "valafind-20261109-2.log", "valafind-20261110-2.log" };
        var ordered = names.OrderBy(RollingFileLoggerProvider.SortKey, StringComparer.Ordinal).ToList();
        Assert.Equal(["valafind-20261109-2.log", "valafind-20261110.log", "valafind-20261110-2.log", "valafind-20261110-10.log"], ordered);
    }

    [Fact]
    public void UnwritableFolderNeverThrows()
    {
        // A file where the folder should be: creating the directory fails.
        Directory.CreateDirectory(_dir);
        var blocked = Path.Combine(_dir, "not-a-dir");
        File.WriteAllText(blocked, "x");
        using var provider = new RollingFileLoggerProvider(blocked);
        Write(provider.CreateLogger("T"), LogLevel.Warning, "still fine");
        Assert.Null(provider.CurrentFile);
    }

    [Theory]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData(" Warning ", LogLevel.Warning)]
    [InlineData("nonsense", null)]
    [InlineData(null, null)]
    public void LevelVariableIsParsed(string? value, LogLevel? expected) =>
        Assert.Equal(expected, AppLogging.ParseLevel(value));

    private static void Write(ILogger log, LogLevel level, string message, Exception? exception = null) =>
        log.Log(level, default, message, exception, static (s, _) => s);

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
