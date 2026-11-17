namespace Valafind.Core.Indexing;

/// <summary>Path conventions the exclusion rules are evaluated with (lets tests exercise every OS on any host).</summary>
internal enum PathFlavor
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>Filesystem queries needed by the context-dependent rules (bin/obj next to a project file, CACHEDIR.TAG, …).</summary>
internal interface IExclusionProbe
{
    bool FileExists(string path);

    /// <summary>True when <paramref name="directory"/> directly contains a file with one of the given extensions
    /// (".csproj") or exact names ("package.json").</summary>
    bool ContainsFile(string directory, IReadOnlyList<string> namesOrExtensions);
}

/// <summary>Real filesystem probe; IO errors mean "no".</summary>
internal sealed class FileSystemExclusionProbe : IExclusionProbe
{
    public static readonly FileSystemExclusionProbe Instance = new();

    public bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public bool ContainsFile(string directory, IReadOnlyList<string> namesOrExtensions)
    {
        try
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0 };
            foreach (var file in Directory.EnumerateFiles(directory, "*", options))
            {
                var name = Path.GetFileName(file.AsSpan());
                foreach (var candidate in namesOrExtensions)
                {
                    if (candidate.StartsWith('.')
                            ? name.EndsWith(candidate, StringComparison.OrdinalIgnoreCase)
                            : name.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        return false;
    }
}

/// <summary>
/// Built-in and user exclusion rules. Name-based rules apply to path components below the containing root, so a
/// root the user picked explicitly is never excluded by its own name. Paths outside every root are excluded.
/// </summary>
internal sealed class ExclusionRules
{
    private static readonly string[] AlwaysExcludedDirectoryNames =
    [
        "node_modules", "bower_components", "venv", "__pycache__", "$RECYCLE.BIN", "System Volume Information",
        "lost+found",
    ];

    /// <summary>macOS packages that look like directories but are opaque application/library bundles.</summary>
    private static readonly string[] MacBundleExtensions =
    [
        ".app", ".framework", ".bundle", ".kext", ".photoslibrary", ".musiclibrary", ".tvlibrary", ".photolibrary",
        ".xcarchive", ".dSYM", ".plugin", ".appex",
    ];

    private static readonly string[] DotNetProjectMarkers = [".csproj", ".fsproj", ".vbproj", ".sln", ".slnx"];
    private static readonly string[] NodeProjectMarkers = ["package.json"];
    private static readonly string[] RustProjectMarkers = ["Cargo.toml"];

    private readonly PathFlavor _flavor;
    private readonly IExclusionProbe? _probe;
    private readonly string _home;
    private readonly string[] _roots;
    private readonly List<string> _excludedPrefixes = [];
    private readonly List<string> _systemPrefixes = [];
    private readonly HashSet<string> _excludedNames;
    private readonly string[] _allowedPrefixes;

    public ExclusionRules(PathFlavor flavor, string home, IEnumerable<string> roots, string? dataDirectory,
        IEnumerable<string> userExcludes, IExclusionProbe? probe)
    {
        _flavor = flavor;
        _probe = probe;
        Comparison = flavor == PathFlavor.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        _excludedNames = new HashSet<string>(flavor == PathFlavor.Linux ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        _home = Normalize(home);
        _roots = roots.Select(Normalize).ToArray();

        if (!string.IsNullOrWhiteSpace(dataDirectory))
            _excludedPrefixes.Add(Normalize(dataDirectory));

        switch (flavor)
        {
            case PathFlavor.Windows:
                _systemPrefixes.Add(Combine(_home, "AppData"));
                _allowedPrefixes = [];
                break;
            case PathFlavor.MacOS:
                _systemPrefixes.Add(Combine(_home, "Library"));
                // iCloud Drive and the File Provider home of Dropbox, OneDrive, Google Drive, Box, … (macOS 12.3+).
                // Online-only (dataless) files there are skipped individually, so walking them downloads nothing.
                _allowedPrefixes =
                [
                    Combine(Combine(_home, "Library"), "Mobile Documents"),
                    Combine(Combine(_home, "Library"), "CloudStorage"),
                ];
                break;
            default:
                _allowedPrefixes = [];
                break;
        }

        foreach (var raw in userExcludes)
        {
            var value = raw?.Trim();
            if (string.IsNullOrEmpty(value))
                continue;
            if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
                value = _home + value[1..];
            if (IsRooted(value))
                _excludedPrefixes.Add(Normalize(value));
            else if (value.IndexOfAny(Separators) >= 0)
                _excludedPrefixes.Add(Normalize(Combine(_home, value)));
            else
                _excludedNames.Add(value);
        }
    }

    public StringComparison Comparison { get; }

    public char Separator => _flavor == PathFlavor.Windows ? '\\' : '/';

    private char[] Separators => _flavor == PathFlavor.Windows ? ['\\', '/'] : ['/'];

    public IReadOnlyList<string> Roots => _roots;

    /// <summary>Directories inside excluded ones that must still be walked (macOS ~/Library/Mobile Documents and
    /// ~/Library/CloudStorage).</summary>
    public IReadOnlyList<string> AllowedPrefixes => _allowedPrefixes;

    public string Normalize(string path)
    {
        if (_flavor == PathFlavor.Windows)
            path = path.Replace('/', '\\');
        var minLength = RootLength(path);
        var end = path.Length;
        while (end > minLength && end > 1 && path[end - 1] == Separator)
            end--;
        return path[..end];
    }

    public string Combine(string directory, string name) =>
        directory.Length > 0 && directory[^1] == Separator ? directory + name : directory + Separator + name;

    private bool IsRooted(string path) => RootLength(path) > 0;

    private int RootLength(string path)
    {
        if (_flavor != PathFlavor.Windows)
            return path.StartsWith('/') ? 1 : 0;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return 2;
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
            return 3;
        return 0;
    }

    /// <summary>True when <paramref name="path"/> equals <paramref name="prefix"/> or lies below it.</summary>
    public bool IsUnder(string path, string prefix)
    {
        if (!path.StartsWith(prefix, Comparison))
            return false;
        return path.Length == prefix.Length || prefix[^1] == Separator || path[prefix.Length] == Separator;
    }

    /// <summary>The (outermost) root containing <paramref name="path"/>, or null.</summary>
    public string? FindRoot(string path)
    {
        foreach (var root in _roots)
        {
            if (IsUnder(path, root))
                return root;
        }
        return null;
    }

    private bool IsAllowed(string path)
    {
        foreach (var allowed in _allowedPrefixes)
        {
            if (IsUnder(path, allowed))
                return true;
        }
        return false;
    }

    /// <summary>Directory check used by the walker: only the directory itself is examined (its ancestors were
    /// already accepted). <paramref name="parent"/> is the containing directory.</summary>
    public bool IsExcludedDirectory(string directory, string parent)
    {
        var name = LastComponent(directory);
        return IsExcludedDirectoryName(name) || IsExcludedByPrefix(directory, directory) || IsExcludedByContext(directory, name, parent);
    }

    /// <summary>File-name check used by the walker.</summary>
    public bool IsExcludedFileName(string name)
    {
        if (name.Length == 0 || name[0] == '.')
            return true;
        if (name.StartsWith("~$", StringComparison.Ordinal))
            return true; // Office owner/lock files
        if (_flavor == PathFlavor.MacOS && name.EndsWith(".icloud", StringComparison.OrdinalIgnoreCase))
            return true; // iCloud placeholder stubs
        return false;
    }

    /// <summary>Full check of an arbitrary path (every component below its root).</summary>
    public bool IsExcluded(string path, bool isDirectory)
    {
        path = Normalize(path);
        var root = FindRoot(path);
        if (root is null)
            return true;
        if (IsExcludedByPrefix(path, path))
            return true;
        if (path.Length == root.Length)
            return false;

        // Walk components below the root: cheap name rules first, context rules (filesystem) afterwards.
        var relative = path.AsSpan(root[^1] == Separator ? root.Length : root.Length + 1);
        var components = relative.ToString().Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var directoryCount = isDirectory ? components.Length : components.Length - 1;
        for (var i = 0; i < directoryCount; i++)
        {
            if (IsExcludedDirectoryName(components[i]))
                return true;
        }
        if (!isDirectory && IsExcludedFileName(components[^1]))
            return true;

        var parent = root;
        for (var i = 0; i < directoryCount; i++)
        {
            var current = Combine(parent, components[i]);
            if (IsExcludedByPrefix(current, path) || IsExcludedByContext(current, components[i], parent))
                return true;
            parent = current;
        }
        return false;
    }

    /// <summary>Cheap check without filesystem access (used to pre-filter watcher events): roots, prefixes and
    /// name rules only. The last component is tested against both directory and file name rules.</summary>
    public bool IsExcludedByNameOnly(string path)
    {
        path = Normalize(path);
        var root = FindRoot(path);
        if (root is null)
            return true;
        if (IsExcludedByPrefix(path, path))
            return true;
        if (path.Length == root.Length)
            return false;
        var relative = path.AsSpan(root[^1] == Separator ? root.Length : root.Length + 1);
        var components = relative.ToString().Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < components.Length - 1; i++)
        {
            if (IsExcludedDirectoryName(components[i]))
                return true;
        }
        return components.Length > 0 && (IsExcludedDirectoryName(components[^1]) || IsExcludedFileName(components[^1]));
    }

    private bool IsExcludedDirectoryName(string name)
    {
        if (name.Length == 0 || name[0] == '.')
            return true; // .git, .cache, .config, .local, .Trash, .venv, … and every other dot-directory
        foreach (var excluded in AlwaysExcludedDirectoryNames)
        {
            if (name.Equals(excluded, Comparison))
                return true;
        }
        if (_excludedNames.Contains(name))
            return true;
        if (_flavor == PathFlavor.MacOS)
        {
            foreach (var ext in MacBundleExtensions)
            {
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <param name="candidate">The directory (or path) being tested.</param>
    /// <param name="fullPath">The path ultimately being evaluated; built-in OS folders (AppData, Library) are not
    /// excluded when it lies inside an allowed prefix such as ~/Library/Mobile Documents.</param>
    private bool IsExcludedByPrefix(string candidate, string fullPath)
    {
        foreach (var prefix in _excludedPrefixes)
        {
            if (IsUnder(candidate, prefix))
                return true;
        }
        foreach (var prefix in _systemPrefixes)
        {
            if (!IsUnder(candidate, prefix) || IsAllowed(fullPath))
                continue;
            // A root the user explicitly placed inside e.g. ~/Library is honoured.
            if (FindRoot(fullPath) is { } root && IsUnder(root, prefix))
                continue;
            return true;
        }
        return false;
    }

    private bool IsExcludedByContext(string directory, string name, string parent)
    {
        if (_probe is null)
            return false;
        if (_probe.FileExists(Combine(directory, "CACHEDIR.TAG")) || _probe.FileExists(Combine(directory, "pyvenv.cfg")))
            return true;
        if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("obj", StringComparison.OrdinalIgnoreCase))
            return _probe.ContainsFile(parent, DotNetProjectMarkers);
        if (name.Equals("dist", StringComparison.OrdinalIgnoreCase) || name.Equals("build", StringComparison.OrdinalIgnoreCase))
            return _probe.ContainsFile(parent, NodeProjectMarkers);
        if (name.Equals("target", StringComparison.OrdinalIgnoreCase))
            return _probe.ContainsFile(parent, RustProjectMarkers);
        return false;
    }

    private string LastComponent(string path)
    {
        var trimmed = Normalize(path);
        var index = trimmed.LastIndexOfAny(Separators);
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }
}
