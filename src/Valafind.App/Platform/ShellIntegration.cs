using System.ComponentModel;
using System.Diagnostics;

namespace Valafind.App.Platform;

/// <summary>Opens files with their default application and reveals them in the system file manager.</summary>
/// <remarks>
/// Every helper launches processes with <see cref="ProcessStartInfo.ArgumentList"/> (never a shell), so paths with
/// spaces, quotes or shell metacharacters are passed through verbatim. The methods return once the helper process
/// has been started; they do not wait for the target application.
/// </remarks>
public static class ShellIntegration
{
    /// <summary>Opens <paramref name="path"/> (file or directory) with the user's default application.</summary>
    /// <exception cref="ArgumentException">The path is empty or not rooted after normalisation.</exception>
    /// <exception cref="FileNotFoundException">Nothing exists at the path.</exception>
    /// <exception cref="InvalidOperationException">No launcher could be started.</exception>
    public static void Open(string path)
    {
        var fullPath = NormalizeExisting(path);

        if (OperatingSystem.IsWindows())
        {
            StartOrThrow(new ProcessStartInfo(fullPath) { UseShellExecute = true });
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            StartOrThrow(Tool("/usr/bin/open", fullPath));
            return;
        }

        // Linux / BSD: xdg-open, then GIO, then KDE as last resorts.
        if (TryStart(Tool("xdg-open", fullPath)) || TryStart(Tool("gio", "open", fullPath)) || TryStart(Tool("kde-open", fullPath)))
            return;
        throw new InvalidOperationException("No program to open files was found (install xdg-utils).");
    }

    /// <summary>Opens an https:// address in the user's browser.</summary>
    /// <exception cref="ArgumentException">The address is not an absolute https:// URL.</exception>
    /// <exception cref="InvalidOperationException">No browser could be started.</exception>
    public static void OpenUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Only https:// addresses are opened.", nameof(url));

        if (OperatingSystem.IsWindows())
        {
            StartOrThrow(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            StartOrThrow(Tool("/usr/bin/open", url.AbsoluteUri));
            return;
        }

        if (TryStart(Tool("xdg-open", url.AbsoluteUri)) || TryStart(Tool("gio", "open", url.AbsoluteUri)))
            return;
        throw new InvalidOperationException("No program to open web pages was found (install xdg-utils).");
    }

    /// <summary>Shows <paramref name="path"/> selected in Explorer / Finder / the freedesktop file manager.</summary>
    /// <exception cref="ArgumentException">The path is empty or not rooted after normalisation.</exception>
    /// <exception cref="FileNotFoundException">Nothing exists at the path.</exception>
    /// <exception cref="InvalidOperationException">No file manager could be started.</exception>
    public static void Reveal(string path)
    {
        var fullPath = NormalizeExisting(path);

        if (OperatingSystem.IsWindows())
        {
            // Explorer parses its own command line: "/select,<path>" with the path quoted. Windows paths cannot
            // contain '"', so this quoting cannot be broken out of.
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            {
                UseShellExecute = false,
                Arguments = "/select,\"" + fullPath + "\"",
            };
            StartOrThrow(psi);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            StartOrThrow(Tool("/usr/bin/open", "-R", fullPath));
            return;
        }

        var parent = Path.GetDirectoryName(fullPath) ?? fullPath;
        var dbus = Tool(
            "dbus-send", "--session", "--print-reply", "--reply-timeout=3000",
            "--dest=org.freedesktop.FileManager1", "--type=method_call",
            "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
            "array:string:" + ToFileUri(fullPath), "string:");
        dbus.RedirectStandardOutput = true; // --print-reply output is not ours to print
        dbus.RedirectStandardError = true;

        Process? process;
        try
        {
            process = Process.Start(dbus);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            process = null;
        }

        if (process is null)
        {
            OpenParent(parent);
            return;
        }

        // Wait off the caller's thread; fall back to opening the folder if no FileManager1 service answered.
        _ = Task.Run(async () =>
        {
            using (process)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    var drainOut = process.StandardOutput.ReadToEndAsync(cts.Token);
                    var drainErr = process.StandardError.ReadToEndAsync(cts.Token);
                    await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                    await Task.WhenAll(drainOut, drainErr).ConfigureAwait(false);
                    if (process.ExitCode == 0)
                        return;
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                }

                try
                {
                    OpenParent(parent);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"Valafind: could not reveal {fullPath}: {ex.Message}");
                }
            }
        });
    }

    /// <summary>
    /// RFC 8089 file URI with every path segment percent-encoded (so '#', '%', '?', ',' and spaces are safe, also
    /// inside dbus-send's comma-separated array syntax).
    /// </summary>
    internal static string ToFileUri(string fullPath)
    {
        if (OperatingSystem.IsWindows())
            return new Uri(fullPath).AbsoluteUri;

        // "/a b/c" splits into ["", "a b", "c"], so joining the escaped segments yields "/a%20b/c".
        return "file://" + string.Join('/', fullPath.Split('/').Select(Uri.EscapeDataString));
    }

    private static void OpenParent(string directory)
    {
        if (TryStart(Tool("xdg-open", directory)) || TryStart(Tool("gio", "open", directory)))
            return;
        throw new InvalidOperationException("No file manager could be started (install xdg-utils).");
    }

    private static string NormalizeExisting(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!Path.IsPathRooted(fullPath))
            throw new ArgumentException($"'{path}' is not an absolute path.", nameof(path));
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            throw new FileNotFoundException("The file no longer exists.", fullPath);

        // Strip a trailing separator so "Reveal" selects the folder itself rather than opening it.
        if (fullPath.Length > Path.GetPathRoot(fullPath)!.Length)
            fullPath = Path.TrimEndingDirectorySeparator(fullPath);
        return fullPath;
    }

    private static ProcessStartInfo Tool(string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        return psi;
    }

    private static void StartOrThrow(ProcessStartInfo psi)
    {
        try
        {
            using var process = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Could not start {psi.FileName}: {ex.Message}", ex);
        }
    }

    private static bool TryStart(ProcessStartInfo psi)
    {
        try
        {
            using var process = Process.Start(psi);
            return process is not null;
        }
        catch (Win32Exception)
        {
            return false; // Not installed.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (InvalidOperationException)
        {
        }
    }
}
