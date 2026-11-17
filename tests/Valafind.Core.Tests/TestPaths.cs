namespace Valafind.Core.Tests;

/// <summary>Turns a Unix-style absolute test path into an absolute path on the current OS.</summary>
internal static class TestPaths
{
    /// <summary>"/home/a/x.txt" → "/home/a/x.txt" on Unix, "C:\home\a\x.txt" on Windows.</summary>
    public static string P(string unixPath) =>
        OperatingSystem.IsWindows() ? "C:" + unixPath.Replace('/', '\\') : unixPath;
}
