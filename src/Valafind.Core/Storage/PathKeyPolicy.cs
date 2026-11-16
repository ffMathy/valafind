using System.Security;
using System.Text;

namespace Valafind.Core.Storage;

/// <summary>
/// How file paths are normalised for storage and turned into the unique <c>path_key</c> column.
/// Windows and macOS file systems are case-insensitive by default, so their keys are lower-cased
/// (macOS additionally folds to Unicode NFC because APFS/HFS+ are normalisation-insensitive and
/// different APIs hand out NFC or NFD spellings). Linux keys are the exact path.
/// </summary>
internal sealed record PathKeyPolicy(bool CaseInsensitive, bool NormalizeUnicode, char Separator, char AltSeparator)
{
    public static PathKeyPolicy Current { get; } = new(
        CaseInsensitive: OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(),
        NormalizeUnicode: OperatingSystem.IsMacOS(),
        Separator: Path.DirectorySeparatorChar,
        AltSeparator: Path.AltDirectorySeparatorChar);

    // Separators always follow the host: NormalizePath uses the host's Path.GetFullPath, so a policy with
    // foreign separators would build directory ranges that never match the stored keys.

    /// <summary>Linux semantics: exact, case-sensitive keys (host separators).</summary>
    public static PathKeyPolicy CaseSensitive { get; } =
        new(false, false, Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Case-insensitive, NFC-folded keys (macOS semantics, host separators); used by tests on any OS.</summary>
    public static PathKeyPolicy CaseInsensitiveNfc { get; } =
        new(true, true, Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Path as stored and reported back: the full path (relative segments resolved).</summary>
    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return path;
        }
    }

    /// <summary>Uniqueness / lookup key for an already normalised path.</summary>
    public string KeyOf(string normalizedPath)
    {
        var key = normalizedPath;
        if (NormalizeUnicode)
        {
            try
            {
                key = key.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                // Lone surrogates cannot be normalised; keep the raw spelling.
            }
        }
        return CaseInsensitive ? key.ToLowerInvariant() : key;
    }

    public string KeyForPath(string path) => KeyOf(NormalizePath(path));

    /// <summary>
    /// Key range covering everything strictly below <paramref name="directory"/> plus the key of the directory itself.
    /// Keys under the directory satisfy <c>lower &lt;= key &lt; upper</c> under SQLite's BINARY (memcmp on UTF-8) collation,
    /// because every such key continues with the separator, and the separator + 1 is an ASCII character.
    /// </summary>
    public (string Exact, string Lower, string Upper) DirectoryRange(string directory)
    {
        var full = NormalizePath(directory);
        var root = SafeGetPathRoot(full);
        string trimmed;
        string withSeparator;
        if (!string.IsNullOrEmpty(root) && full.Length <= root.Length)
        {
            // A volume / file-system root: "/" or "C:\" already ends with a separator.
            trimmed = full;
            withSeparator = EndsWithSeparator(full) ? full : full + Separator;
        }
        else
        {
            trimmed = full.TrimEnd(Separator, AltSeparator);
            if (trimmed.Length == 0)
            {
                trimmed = Separator.ToString();
                withSeparator = trimmed;
            }
            else
            {
                withSeparator = trimmed + Separator;
            }
        }

        var exact = KeyOf(trimmed);
        var lower = KeyOf(withSeparator);
        // The key's final character is the separator (an ASCII char unaffected by case folding / NFC).
        var upper = string.Concat(lower.AsSpan(0, lower.Length - 1), ((char)(lower[^1] + 1)).ToString());
        return (exact, lower, upper);
    }

    private bool EndsWithSeparator(string path) =>
        path.Length > 0 && (path[^1] == Separator || path[^1] == AltSeparator);

    private static string? SafeGetPathRoot(string path)
    {
        try
        {
            return Path.GetPathRoot(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
