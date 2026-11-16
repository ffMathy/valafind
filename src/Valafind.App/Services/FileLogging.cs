using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Valafind.App.Services;

/// <summary>
/// Minimal rolling file logger: one file per day (<c>valafind-YYYYMMDD.log</c>, then <c>valafind-YYYYMMDD-1.log</c>, …
/// once a file reaches <see cref="MaxFileBytes"/>), keeping the newest <see cref="RetainedFiles"/> files. Every line is
/// flushed immediately so nothing is lost when the process dies. Thread-safe.
/// </summary>
/// <remarks>Privacy: callers must never log file contents or snippets, and log file paths at Debug level only.</remarks>
internal sealed class RollingFileLoggerProvider : ILoggerProvider
{
    internal const string FilePrefix = "valafind-";
    internal const string FileExtension = ".log";

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private FileStream? _stream;
    private string? _currentDay;
    private int _currentIndex;
    private bool _disposed;
    private bool _broken;

    /// <summary>Creates a provider writing below <paramref name="directory"/>.</summary>
    public RollingFileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information,
        long maxFileBytes = 5L * 1024 * 1024, int retainedFiles = 7, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFileBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedFiles, 1);
        Directory = directory;
        MinimumLevel = minimumLevel;
        MaxFileBytes = maxFileBytes;
        RetainedFiles = retainedFiles;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Folder holding the log files.</summary>
    public string Directory { get; }

    /// <summary>Messages below this level are dropped.</summary>
    public LogLevel MinimumLevel { get; }

    /// <summary>Size at which the next file of the day is started.</summary>
    public long MaxFileBytes { get; }

    /// <summary>How many log files are kept.</summary>
    public int RetainedFiles { get; }

    /// <summary>The file currently written to (null before the first message).</summary>
    public string? CurrentFile
    {
        get
        {
            lock (_gate)
                return _stream?.Name;
        }
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var now = _time.GetUtcNow();
        var sb = new StringBuilder(128);
        sb.Append(now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
            .Append(' ').Append(LevelName(level))
            .Append(' ').Append(category)
            .Append(": ").Append(message);
        if (exception is not null)
            sb.AppendLine().Append(exception);
        sb.AppendLine();
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());

        lock (_gate)
        {
            if (_disposed || _broken)
                return;
            try
            {
                var stream = EnsureStream(now, bytes.Length);
                stream.Write(bytes);
                stream.Flush();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down; stop trying after the first failure.
                _broken = true;
                _stream?.Dispose();
                _stream = null;
            }
        }
    }

    private FileStream EnsureStream(DateTimeOffset now, int pendingBytes)
    {
        var day = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (_stream is not null && _currentDay == day && _stream.Length + pendingBytes <= MaxFileBytes)
            return _stream;

        _stream?.Dispose();
        _stream = null;
        System.IO.Directory.CreateDirectory(Directory);
        if (_currentDay != day)
        {
            _currentDay = day;
            _currentIndex = 0;
        }

        // Continue today's newest file when it still has room (e.g. after a restart), otherwise start the next one.
        while (true)
        {
            var path = Path.Combine(Directory, FileName(day, _currentIndex));
            var info = new FileInfo(path);
            if (!info.Exists || info.Length + pendingBytes <= MaxFileBytes || info.Length == 0)
            {
                _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                break;
            }
            _currentIndex++;
        }
        PruneOldFiles(_stream.Name);
        return _stream;
    }

    internal static string FileName(string day, int index) =>
        FilePrefix + day + (index == 0 ? string.Empty : "-" + index.ToString(CultureInfo.InvariantCulture)) + FileExtension;

    private void PruneOldFiles(string keep)
    {
        try
        {
            var files = new DirectoryInfo(Directory).GetFiles(FilePrefix + "*" + FileExtension)
                .OrderByDescending(f => SortKey(f.Name), StringComparer.Ordinal)
                .ToList();
            foreach (var old in files.Skip(RetainedFiles))
            {
                if (string.Equals(old.FullName, keep, StringComparison.Ordinal))
                    continue;
                try
                {
                    old.Delete();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Locked by another process (e.g. an editor); try again on the next roll.
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>"valafind-20261116-3.log" → "20261116-0000000003", so day and index sort correctly as text.</summary>
    internal static string SortKey(string fileName)
    {
        var core = fileName[FilePrefix.Length..^FileExtension.Length];
        var dash = core.IndexOf('-', StringComparison.Ordinal);
        var day = dash < 0 ? core : core[..dash];
        var index = dash < 0 ? 0 : int.TryParse(core.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return day + "-" + index.ToString("D10", CultureInfo.InvariantCulture);
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "FATAL",
        _ => "     ",
    };

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _stream?.Dispose();
            _stream = null;
        }
    }

    private sealed class FileLogger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider.MinimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel))
                return;
            provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
