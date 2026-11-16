using System.Diagnostics;
using System.IO.Enumeration;
using Valafind.Core.Abstractions;
using Valafind.Core.Configuration;
using Valafind.Core.Platform;

namespace Valafind.Core.Indexing;

/// <summary>
/// Finds user files to index. Phase 1 (<see cref="EnumerateFrequentFiles"/>) merges the OS "recent documents"
/// sources with recently touched files in the usual document folders and ranks them by frequency and recency.
/// Phase 2 (<see cref="EnumerateAllFiles"/>) walks every root breadth-first so shallow, more relevant folders come
/// first. Symlinks/junctions are never followed and cloud placeholders that would trigger a download are skipped.
/// </summary>
public sealed class UserFileDiscovery : IFileDiscovery
{
    internal const int MaxFrequentFiles = 10_000;
    private const int RecentFolderDepth = 3;
    private const int RecentFolderMaxEntries = 50_000;
    private static readonly TimeSpan RecentWindow = TimeSpan.FromDays(90);
    private static readonly TimeSpan MdfindTimeout = TimeSpan.FromSeconds(30);

    // Windows cloud-file attributes (not in the FileAttributes enum).
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    private readonly ExclusionRules _rules;
    private readonly string _home;
    private readonly TimeProvider _time;
    private readonly EnumerationOptions _enumerationOptions;
    private readonly Func<string, string?> _environment;

    /// <summary>Creates discovery for <see cref="ValafindSettings.Roots"/> (or the home directory when empty),
    /// honouring <see cref="ValafindSettings.Excludes"/>.</summary>
    public UserFileDiscovery(ValafindSettings settings)
        : this(settings, AppPaths.Home, AppPaths.DataDirectory, TimeProvider.System, Environment.GetEnvironmentVariable)
    {
    }

    internal UserFileDiscovery(ValafindSettings settings, string home, string? dataDirectory, TimeProvider time,
        Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _home = Path.GetFullPath(home);
        _time = time;
        _environment = environment;
        var roots = NormalizeRoots(settings.Roots.Count > 0 ? settings.Roots : [_home], _home);
        Roots = roots;
        _rules = new ExclusionRules(CurrentFlavor, _home, roots,
            string.IsNullOrWhiteSpace(dataDirectory) ? null : Path.GetFullPath(dataDirectory),
            settings.Excludes, FileSystemExclusionProbe.Instance);

        var skip = FileAttributes.System | FileAttributes.Offline | FileAttributes.Device;
        if (OperatingSystem.IsWindows())
            skip |= RecallOnDataAccess | RecallOnOpen; // reparse points are inspected individually (cloud files are reparse points)
        else
            skip |= FileAttributes.ReparsePoint; // symlinks
        _enumerationOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = skip,
            ReturnSpecialDirectories = false,
            MatchType = MatchType.Simple,
        };
    }

    internal static PathFlavor CurrentFlavor =>
        OperatingSystem.IsWindows() ? PathFlavor.Windows : OperatingSystem.IsMacOS() ? PathFlavor.MacOS : PathFlavor.Linux;

    /// <inheritdoc />
    public IReadOnlyList<string> Roots { get; }

    internal ExclusionRules Rules => _rules;

    /// <inheritdoc />
    public bool IsExcluded(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
        bool isDirectory;
        try
        {
            isDirectory = Directory.Exists(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            isDirectory = false;
        }
        return _rules.IsExcluded(full, isDirectory);
    }

    /// <summary>Filesystem-free pre-filter for watcher events (see <see cref="ExclusionRules.IsExcludedByNameOnly"/>).</summary>
    internal bool IsExcludedByNameOnly(string path)
    {
        try
        {
            return _rules.IsExcludedByNameOnly(Path.GetFullPath(path));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    // ------------------------------------------------------------------ Phase 2: everything

    /// <inheritdoc />
    public IEnumerable<string> EnumerateAllFiles(CancellationToken cancellationToken) =>
        Walk(Roots, int.MaxValue, cancellationToken).Select(e => e.Path);

    /// <summary>Breadth-first walk of one directory inside the roots (used for watcher-created directories and rescans).</summary>
    internal IEnumerable<string> EnumerateUnder(string directory, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(directory);
        if (_rules.IsExcluded(full, isDirectory: true))
            return [];
        return Walk([full], int.MaxValue, cancellationToken).Select(e => e.Path);
    }

    internal readonly record struct WalkEntry(string Path, long Length, DateTime LastWriteUtc, DateTime LastAccessUtc);

    private IEnumerable<WalkEntry> Walk(IEnumerable<string> starts, int maxDepth, CancellationToken cancellationToken)
    {
        var queue = new Queue<(string Path, int Depth)>();
        foreach (var start in starts)
            queue.Enqueue((_rules.Normalize(start), 0));

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = queue.Dequeue();
            if (OperatingSystem.IsMacOS() && MacFileFlags.IsDataless(directory))
                continue; // listing it would materialise it from the cloud

            var (files, subdirectories) = ListDirectory(directory);
            foreach (var file in files)
                yield return file;

            if (depth + 1 >= maxDepth)
                continue;
            foreach (var sub in subdirectories)
            {
                if (!_rules.IsExcludedDirectory(sub, directory))
                {
                    queue.Enqueue((sub, depth + 1));
                    continue;
                }
                foreach (var allowed in _rules.AllowedPrefixes)
                {
                    if (_rules.IsUnder(allowed, sub) && !string.Equals(allowed, sub, _rules.Comparison) && DirectoryExists(allowed))
                        queue.Enqueue((allowed, depth + 1));
                }
            }
        }
    }

    /// <summary>Lists one directory fully before yielding so no directory handle stays open while the caller works.</summary>
    private (List<WalkEntry> Files, List<string> Directories) ListDirectory(string directory)
    {
        var files = new List<WalkEntry>();
        var directories = new List<string>();
        try
        {
            var enumerable = new FileSystemEnumerable<(string Path, string Name, bool IsDirectory, long Length, FileAttributes Attributes, DateTime Write, DateTime Access)>(
                directory,
                static (ref FileSystemEntry e) => (e.ToFullPath(), e.FileName.ToString(), e.IsDirectory, e.IsDirectory ? 0 : e.Length,
                    e.Attributes, e.LastWriteTimeUtc.UtcDateTime, e.LastAccessTimeUtc.UtcDateTime),
                _enumerationOptions);
            foreach (var entry in enumerable)
            {
                if (OperatingSystem.IsWindows() && (entry.Attributes & FileAttributes.ReparsePoint) != 0 && IsLink(entry.Path, entry.IsDirectory))
                    continue; // symlink or junction: never follow
                if (entry.IsDirectory)
                {
                    directories.Add(entry.Path);
                }
                else if (entry.Length > 0 && !_rules.IsExcludedFileName(entry.Name) &&
                         !(OperatingSystem.IsMacOS() && MacFileFlags.IsDataless(entry.Path)))
                {
                    files.Add(new WalkEntry(entry.Path, entry.Length, entry.Write, entry.Access));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            // Unreadable / vanished directory: keep whatever was listed.
        }
        files.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        directories.Sort(StringComparer.Ordinal);
        return (files, directories);
    }

    private static bool IsLink(string path, bool isDirectory)
    {
        try
        {
            FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            // LinkTarget is non-null for symbolic links and junctions only; cloud-file reparse points return null.
            return info.LinkTarget is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool DirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ Phase 1: frequent / recent

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFrequentFiles(CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, Candidate>(
            CurrentFlavor == PathFlavor.Linux ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

        void Add(string? path, int count, DateTimeOffset? lastUsed)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            string full;
            try
            {
                full = _rules.Normalize(Path.GetFullPath(path));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }
            if (candidates.TryGetValue(full, out var existing))
            {
                candidates[full] = new Candidate(existing.Count + count,
                    existing.LastUsed is null ? lastUsed : lastUsed is null ? existing.LastUsed : existing.LastUsed > lastUsed ? existing.LastUsed : lastUsed);
            }
            else
            {
                candidates[full] = new Candidate(count, lastUsed);
            }
        }

        try
        {
            if (OperatingSystem.IsWindows())
                CollectWindowsRecent(Add, cancellationToken);
            else if (OperatingSystem.IsMacOS())
                CollectMacRecent(Add, cancellationToken);
            else
                CollectLinuxRecent(Add, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // OS history unavailable; folder scan below still contributes.
        }

        var cutoff = _time.GetUtcNow() - RecentWindow;
        foreach (var folder in RecentFolders())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_rules.FindRoot(folder) is null || _rules.IsExcluded(folder, isDirectory: true) || !DirectoryExists(folder))
                continue;
            var seen = 0;
            foreach (var entry in Walk([folder], RecentFolderDepth, cancellationToken))
            {
                if (++seen > RecentFolderMaxEntries)
                    break;
                var last = entry.LastWriteUtc > entry.LastAccessUtc ? entry.LastWriteUtc : entry.LastAccessUtc;
                if (last >= cutoff.UtcDateTime)
                    Add(entry.Path, 0, new DateTimeOffset(last, TimeSpan.Zero));
            }
        }

        var now = _time.GetUtcNow();
        var ranked = new List<(string Path, double Score)>(candidates.Count);
        foreach (var (path, candidate) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_rules.IsExcluded(path, isDirectory: false))
                continue;
            if (!TryStatFile(path, out var length, out var write, out var access) || length <= 0)
                continue;
            if (OperatingSystem.IsMacOS() && MacFileFlags.IsDataless(path))
                continue;
            var lastUsed = candidate.LastUsed ?? new DateTimeOffset(write > access ? write : access, TimeSpan.Zero);
            ranked.Add((path, Score(candidate.Count, lastUsed, now)));
        }

        ranked.Sort(static (a, b) => b.Score.CompareTo(a.Score) is var c && c != 0 ? c : string.CompareOrdinal(a.Path, b.Path));
        return ranked.Take(MaxFrequentFiles).Select(r => r.Path).ToList();
    }

    private readonly record struct Candidate(int Count, DateTimeOffset? LastUsed);

    /// <summary>Frequency + recency: (1 + ln(1 + count)) × 2^(−age / 21 days).</summary>
    internal static double Score(int count, DateTimeOffset lastUsed, DateTimeOffset now)
    {
        var ageDays = Math.Max(0, (now - lastUsed).TotalDays);
        return (1 + Math.Log(1 + Math.Max(0, count))) * Math.Pow(2, -ageDays / 21.0);
    }

    private static bool TryStatFile(string path, out long length, out DateTime write, out DateTime access)
    {
        length = 0;
        write = access = default;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return false;
            var attributes = info.Attributes;
            if ((attributes & (FileAttributes.Directory | FileAttributes.Offline | FileAttributes.Device | FileAttributes.System)) != 0)
                return false;
            if (OperatingSystem.IsWindows() && (attributes & (RecallOnDataAccess | RecallOnOpen)) != 0)
                return false;
            if ((attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null)
                return false;
            length = info.Length;
            write = info.LastWriteTimeUtc;
            access = info.LastAccessTimeUtc;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private IEnumerable<string> RecentFolders()
    {
        if (CurrentFlavor == PathFlavor.Linux)
        {
            var configHome = _environment("XDG_CONFIG_HOME");
            var file = Path.Combine(string.IsNullOrWhiteSpace(configHome) ? Path.Combine(_home, ".config") : configHome, "user-dirs.dirs");
            Dictionary<string, string>? dirs = null;
            if (SafeFile.TryReadAllText(file) is { } text)
                dirs = UserDirsParser.Parse(text, _home);
            foreach (var (key, fallback) in new[] { ("XDG_DESKTOP_DIR", "Desktop"), ("XDG_DOCUMENTS_DIR", "Documents"), ("XDG_DOWNLOAD_DIR", "Downloads") })
            {
                var path = dirs is not null && dirs.TryGetValue(key, out var p) ? p : Path.Combine(_home, fallback);
                if (!string.Equals(_rules.Normalize(path), _home, _rules.Comparison))
                    yield return path; // a disabled XDG dir points at $HOME itself
            }
            yield break;
        }

        yield return Path.Combine(_home, "Desktop");
        yield return Path.Combine(_home, "Documents");
        yield return Path.Combine(_home, "Downloads");
        if (OperatingSystem.IsWindows())
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrEmpty(documents))
                yield return documents;
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrEmpty(desktop))
                yield return desktop;
            var oneDrives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            {
                if (_environment(variable) is { Length: > 0 } od)
                    oneDrives.Add(od);
            }
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(_home, "OneDrive*", _enumerationOptions))
                    oneDrives.Add(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            foreach (var od in oneDrives)
                yield return od;
        }
    }

    private static void CollectWindowsRecent(Action<string?, int, DateTimeOffset?> add, CancellationToken cancellationToken)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
            return;
        var recent = Path.Combine(appData, "Microsoft", "Windows", "Recent");
        if (!DirectoryExists(recent))
            return;
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };
        foreach (var link in Directory.EnumerateFiles(recent, "*.lnk", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(link);
                if (info.Length > 1024 * 1024)
                    continue;
                var target = ShellLinkParser.TryGetLocalTarget(File.ReadAllBytes(link));
                add(target, 1, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void CollectLinuxRecent(Action<string?, int, DateTimeOffset?> add, CancellationToken cancellationToken)
    {
        var dataHome = _environment("XDG_DATA_HOME");
        var share = string.IsNullOrWhiteSpace(dataHome) ? Path.Combine(_home, ".local", "share") : dataHome;

        var xbel = Path.Combine(share, "recently-used.xbel");
        try
        {
            if (File.Exists(xbel))
            {
                using var reader = new StreamReader(xbel);
                foreach (var entry in XbelParser.Parse(reader))
                    add(entry.Path, entry.Count, entry.LastUsedUtc);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        var kde = Path.Combine(share, "RecentDocuments");
        if (!DirectoryExists(kde))
            return;
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };
        foreach (var desktop in Directory.EnumerateFiles(kde, "*.desktop", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SafeFile.TryReadAllText(desktop) is not { } text)
                continue;
            DateTimeOffset? when = null;
            try
            {
                when = new DateTimeOffset(File.GetLastWriteTimeUtc(desktop), TimeSpan.Zero);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            add(KdeRecentParser.GetPath(text, _home), 1, when);
        }
    }

    private void CollectMacRecent(Action<string?, int, DateTimeOffset?> add, CancellationToken cancellationToken)
    {
        const string mdfind = "/usr/bin/mdfind";
        if (!File.Exists(mdfind))
            return;
        foreach (var root in Roots)
        {
            // Spotlight queries the local metadata index only; no network access.
            foreach (var path in RunLines(mdfind, ["-onlyin", root, "kMDItemLastUsedDate >= $time.today(-90)"], MdfindTimeout, cancellationToken))
                add(path, 1, null);
        }
    }

    /// <summary>Runs a local tool and returns its stdout lines; tolerates absence, failure and timeouts.</summary>
    internal static List<string> RunLines(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null)
                return lines;
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginErrorReadLine();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var readTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false)) is not null)
                {
                    if (line.Length > 0)
                        lines.Add(line);
                }
            }, CancellationToken.None);
            try
            {
                readTask.Wait(timeoutCts.Token);
                process.WaitForExit((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds));
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (AggregateException)
            {
                TryKill(process);
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            // Tool missing or failed to start.
        }
        finally
        {
            process?.Dispose();
        }
        lock (lines)
            return [.. lines];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    private static string[] NormalizeRoots(IEnumerable<string> roots, string home)
    {
        var flavor = CurrentFlavor;
        var comparison = flavor == PathFlavor.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var normalizer = new ExclusionRules(flavor, home, [], null, [], null);
        var list = new List<string>();
        foreach (var raw in roots)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var value = raw.Trim();
            if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
                value = home + value[1..];
            string full;
            try
            {
                full = normalizer.Normalize(Path.GetFullPath(value));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (!list.Any(r => string.Equals(r, full, comparison)))
                list.Add(full);
        }
        // Drop roots nested inside other roots (walking / watching them twice would duplicate work).
        return list.Where(r => !list.Any(o => !string.Equals(o, r, comparison) && normalizer.IsUnder(r, o))).ToArray();
    }
}
