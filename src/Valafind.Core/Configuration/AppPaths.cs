namespace Valafind.Core.Configuration;

/// <summary>Per-OS locations. Honours VALAFIND_DATA_DIR for tests and portable installs.</summary>
public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("VALAFIND_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(overridden))
                return overridden;

            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valafind");
            if (OperatingSystem.IsMacOS())
                return Path.Combine(Home, "Library", "Application Support", "Valafind");

            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            return Path.Combine(string.IsNullOrWhiteSpace(xdg) ? Path.Combine(Home, ".local", "share") : xdg, "valafind");
        }
    }

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string IndexDatabase => Path.Combine(DataDirectory, "index.db");

    /// <summary>Default place the model is installed to by tools/fetch-model.*.</summary>
    public static string ModelsDirectory => Path.Combine(DataDirectory, "models");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Owner-only permissions for directories holding private data (index excerpts, settings, logs).</summary>
    public const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Owner-only permissions for files holding private data.</summary>
    public const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode GroupOtherBits = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>
    /// Creates <paramref name="directory"/> (and parents) if needed. On Unix the directory is created 0700 and an existing
    /// one is tightened to owner-only, so other local users cannot list or read Valafind's data. On Windows the per-user
    /// profile ACLs already restrict it. Failing to tighten an existing directory is ignored (never stops the app).
    /// </summary>
    public static void EnsurePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }

        Directory.CreateDirectory(directory, PrivateDirectoryMode);
        TightenToOwner(directory);
    }

    /// <summary>Options that create a new file with owner-only permissions on Unix (no effect on Windows).</summary>
    public static FileStreamOptions PrivateFileOptions(FileMode mode, FileAccess access, FileShare share)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = share };
        if (!OperatingSystem.IsWindows() && mode is FileMode.Create or FileMode.CreateNew or FileMode.OpenOrCreate or FileMode.Append)
            options.UnixCreateMode = PrivateFileMode;
        return options;
    }

    /// <summary>Strips group/other permission bits from an existing file or directory (Unix only, best effort).</summary>
    public static void TightenToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & GroupOtherBits) != 0)
                File.SetUnixFileMode(path, mode & ~GroupOtherBits);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best effort: a path we cannot chmod (not ours, read-only file system) is left as it is.
        }
    }
}
