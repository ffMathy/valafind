namespace Valafind.Core.Platform;

/// <summary>Small IO helpers that turn expected filesystem errors into null.</summary>
internal static class SafeFile
{
    /// <summary>Reads a (small) text file; returns null when it is missing or unreadable.</summary>
    public static string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
