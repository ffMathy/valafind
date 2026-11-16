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
}
