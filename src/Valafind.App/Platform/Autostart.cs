using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Microsoft.Win32;
using Valafind.App.Updates;
using Valafind.Core.Configuration;

namespace Valafind.App.Platform;

/// <summary>
/// Starts Valafind with <c>--background</c> when the user logs in. Windows: the HKCU Run key. macOS: a per-user
/// LaunchAgent (<c>~/Library/LaunchAgents/app.valafind.plist</c>). Linux: an XDG autostart entry
/// (<c>$XDG_CONFIG_HOME/autostart/valafind.desktop</c>). Nothing requires elevation. Store copies differ: the Mac App
/// Store copy is a login item (SMAppService), and Windows runs the Microsoft Store package's startup task.
/// </summary>
public static class Autostart
{
    internal const string BackgroundArgument = "--background";
    internal const string WindowsValueName = "Valafind";
    internal const string MacLabel = "app.valafind";
    private const string WindowsRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// True when Windows, not Valafind, starts Valafind at login: the Microsoft Store package declares a startup task
    /// (packaging/store/windows/AppxManifest.xml), and a package's writes to the Run key stay inside the package. Users
    /// switch it in Settings › Apps › Startup.
    /// </summary>
    public static bool IsManagedBySystem => OperatingSystem.IsWindows() && StoreInstall.IsActive;

    /// <summary>True when an autostart entry for Valafind exists and is not disabled.</summary>
    public static bool IsEnabled()
    {
        try
        {
            if (IsManagedBySystem)
                return false;
            if (OperatingSystem.IsMacOSVersionAtLeast(13) && AppPaths.InAppSandbox)
                return MacLoginItem.IsEnabled();
            if (OperatingSystem.IsWindows())
                return WindowsIsEnabled();
            if (OperatingSystem.IsMacOS())
                return File.Exists(MacPlistPath);
            return LinuxIsEnabled(LinuxDesktopFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException or DllNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Creates or removes the autostart entry for the current executable.</summary>
    /// <exception cref="IOException">The entry could not be written or removed.</exception>
    /// <exception cref="UnauthorizedAccessException">Permission denied.</exception>
    /// <exception cref="InvalidOperationException">The executable path cannot be determined.</exception>
    public static void SetEnabled(bool enabled)
    {
        if (IsManagedBySystem)
            return;
        if (OperatingSystem.IsMacOSVersionAtLeast(13) && AppPaths.InAppSandbox)
        {
            // A sandboxed app cannot write ~/Library/LaunchAgents; it becomes a login item instead.
            MacLoginItem.SetEnabled(enabled);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            WindowsSetEnabled(enabled);
            return;
        }

        var path = OperatingSystem.IsMacOS() ? MacPlistPath : LinuxDesktopFilePath;
        if (!enabled)
        {
            File.Delete(path); // No-op when absent.
            return;
        }

        var command = LaunchCommand();
        var content = OperatingSystem.IsMacOS() ? BuildLaunchAgentPlist(command) : BuildDesktopEntry(command);
        WriteAtomically(path, content);
    }

    internal static string MacPlistPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", MacLabel + ".plist");

    internal static string LinuxDesktopFilePath
    {
        get
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(config) || !Path.IsPathRooted(config))
                config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(config, "autostart", "valafind.desktop");
        }
    }

    /// <summary>
    /// The argv that relaunches this app in the background: the native executable (or, when running as
    /// <c>dotnet Valafind.dll</c>, the host plus the entry assembly) followed by <c>--background</c>.
    /// </summary>
    internal static IReadOnlyList<string> LaunchCommand()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
            throw new InvalidOperationException("Cannot determine the Valafind executable path.");

        if (OperatingSystem.IsMacOS() && ResolveBundleExecutable(processPath) is { } bundleExe)
            processPath = bundleExe;

        var hostName = Path.GetFileNameWithoutExtension(processPath);
        if (string.Equals(hostName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var entry = AppExecutable.EntryAssemblyPath
                ?? throw new InvalidOperationException("Cannot determine the Valafind assembly path.");
            return [processPath, entry, BackgroundArgument];
        }

        return [processPath, BackgroundArgument];
    }

    /// <summary>
    /// When the process runs from inside "X.app/Contents/MacOS/", returns the bundle's main executable as declared by
    /// CFBundleExecutable (falling back to the running binary), so login launches the app as a proper bundle.
    /// </summary>
    internal static string? ResolveBundleExecutable(string processPath)
    {
        const string marker = ".app/Contents/MacOS/";
        var index = processPath.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return null;

        var macOsDir = processPath[..(index + marker.Length - 1)];
        var infoPlist = Path.Combine(Path.GetDirectoryName(macOsDir)!, "Info.plist");
        try
        {
            if (File.Exists(infoPlist))
            {
                // DTD ignored and no resolver: never touch the network for Apple's DOCTYPE URL.
                var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null };
                using var reader = System.Xml.XmlReader.Create(infoPlist, settings);
                var doc = System.Xml.Linq.XDocument.Load(reader);
                var keys = doc.Descendants("key").ToList();
                var key = keys.FirstOrDefault(k => k.Value == "CFBundleExecutable");
                if (key?.ElementsAfterSelf().FirstOrDefault() is { Name.LocalName: "string" } value
                    && !string.IsNullOrWhiteSpace(value.Value)
                    && value.Value.IndexOfAny(['/', '\\']) < 0)
                {
                    var candidate = Path.Combine(macOsDir, value.Value);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // Binary or unreadable Info.plist: the running binary is still inside the bundle, use it.
        }

        return processPath;
    }

    internal static string BuildLaunchAgentPlist(IReadOnlyList<string> command)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key>
              <string>
            """).Append(MacLabel).Append("</string>\n");
        sb.Append("  <key>ProgramArguments</key>\n  <array>\n");
        foreach (var arg in command)
            sb.Append("    <string>").Append(SecurityElement.Escape(arg)).Append("</string>\n");
        sb.Append("""
              </array>
              <key>RunAtLoad</key>
              <true/>
              <key>ProcessType</key>
              <string>Interactive</string>
              <key>LimitLoadToSessionType</key>
              <string>Aqua</string>
            </dict>
            </plist>

            """);
        return sb.ToString();
    }

    internal static string BuildDesktopEntry(IReadOnlyList<string> command)
    {
        var exec = string.Join(' ', command.Select(QuoteExecArgument));
        return $"""
            [Desktop Entry]
            Type=Application
            Name=Valafind
            Comment=Semantic local file search
            Exec={exec}
            Terminal=false
            NoDisplay=true
            X-GNOME-Autostart-enabled=true
            X-KDE-autostart-after=panel

            """;
    }

    /// <summary>
    /// Quotes one argument for a Desktop Entry Exec key: wrap in double quotes, backslash-escape <c>" ` $ \</c>
    /// (quoting rules), double '%' (field codes), then escape backslashes again (string-value escaping).
    /// </summary>
    internal static string QuoteExecArgument(string arg)
    {
        var quoted = new StringBuilder(arg.Length + 2).Append('"');
        foreach (var c in arg)
        {
            if (c is '"' or '`' or '$' or '\\')
                quoted.Append('\\');
            quoted.Append(c);
        }

        quoted.Append('"');
        return quoted.ToString().Replace("%", "%%", StringComparison.Ordinal).Replace("\\", "\\\\", StringComparison.Ordinal);
    }

    internal static bool LinuxIsEnabled(string desktopFile)
    {
        if (!File.Exists(desktopFile))
            return false;

        var inEntry = false;
        foreach (var raw in File.ReadLines(desktopFile))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inEntry = line == "[Desktop Entry]";
                continue;
            }

            if (!inEntry)
                continue;
            if (line.Equals("Hidden=true", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("X-GNOME-Autostart-enabled=false", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static void WriteAtomically(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool WindowsIsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKey, writable: false);
        return key?.GetValue(WindowsValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    [SupportedOSPlatform("windows")]
    private static void WindowsSetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(WindowsRunKey, writable: true)
            ?? throw new IOException(@"Cannot open HKCU\" + WindowsRunKey + ".");
        if (!enabled)
        {
            key.DeleteValue(WindowsValueName, throwOnMissingValue: false);
            return;
        }

        // The Run key holds a command line; Windows paths cannot contain '"', so plain quoting is unambiguous.
        var value = string.Join(' ', LaunchCommand().Select(a => a == BackgroundArgument ? a : "\"" + a + "\""));
        key.SetValue(WindowsValueName, value, RegistryValueKind.String);
    }
}
