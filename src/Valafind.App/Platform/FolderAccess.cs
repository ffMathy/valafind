using Microsoft.Extensions.Logging;
using Valafind.Core.Configuration;

namespace Valafind.App.Platform;

/// <summary>
/// Access to the indexed folders in the macOS App Sandbox (Mac App Store copies). The sandbox lets the app read a
/// folder once the user picks it in an open panel, but only until the app quits. A security-scoped bookmark, made right
/// after the pick and kept in <see cref="ValafindSettings.RootBookmarks"/>, brings that access back on every start.
/// Everywhere else this does nothing: the app reads what its user can.
/// </summary>
internal static partial class FolderAccess
{
    /// <summary>True when folders must be granted (and remembered) to be indexed.</summary>
    public static bool IsRequired => AppPaths.InAppSandbox;

    private static IFolderBookmarks? Bookmarks => IsRequired && OperatingSystem.IsMacOS() ? new MacFolderBookmarks() : null;

    /// <summary>
    /// Regains access to every folder in <see cref="ValafindSettings.Roots"/>. Run once at startup, before indexing.
    /// Folders that cannot be regained (deleted, or never remembered) are dropped from the settings, so the user sees
    /// what is really indexed and can add them again. Returns true when the settings changed and should be saved.
    /// </summary>
    public static bool Restore(ValafindSettings settings, ILogger logger) =>
        Bookmarks is { } bookmarks && Restore(settings, bookmarks, logger);

    /// <summary>A bookmark for a folder the user just picked, or null when none is needed (or macOS refused one).</summary>
    public static string? Remember(string path, ILogger logger) =>
        Bookmarks is { } bookmarks ? Remember(path, bookmarks, logger) : null;

    internal static bool Restore(ValafindSettings settings, IFolderBookmarks bookmarks, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var roots = new List<string>();
        var remembered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in settings.Roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            if (!settings.RootBookmarks.TryGetValue(root, out var bookmark) || string.IsNullOrEmpty(bookmark))
            {
                LogNotRemembered(logger, root);
                continue;
            }

            ResolvedBookmark? resolved;
            try
            {
                resolved = bookmarks.Resolve(bookmark);
            }
            catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
            {
                LogFailed(logger, root, e);
                resolved = null;
            }

            if (resolved is null)
            {
                LogLost(logger, root);
                continue;
            }

            if (roots.Contains(resolved.Path, StringComparer.Ordinal))
                continue;
            if (!string.Equals(resolved.Path, root, StringComparison.Ordinal))
                LogMoved(logger, root, resolved.Path);
            roots.Add(resolved.Path);
            remembered[resolved.Path] = resolved.RefreshedBookmark ?? bookmark;
        }

        var changed = !roots.SequenceEqual(settings.Roots, StringComparer.Ordinal)
            || remembered.Count != settings.RootBookmarks.Count
            || remembered.Any(pair => !settings.RootBookmarks.TryGetValue(pair.Key, out var old) || old != pair.Value);
        settings.Roots = roots;
        settings.RootBookmarks = remembered;
        return changed;
    }

    internal static string? Remember(string path, IFolderBookmarks bookmarks, ILogger logger)
    {
        try
        {
            if (bookmarks.Create(path) is { } bookmark)
                return bookmark;
            LogNotRememberable(logger, path, null);
        }
        catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            LogNotRememberable(logger, path, e);
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No access to the indexed folder {Folder} is remembered; it was dropped from the indexed folders")]
    private static partial void LogNotRemembered(ILogger logger, string folder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Access to the indexed folder {Folder} is gone (deleted, or refused by macOS); it was dropped from the indexed folders")]
    private static partial void LogLost(ILogger logger, string folder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not regain access to the indexed folder {Folder}")]
    private static partial void LogFailed(ILogger logger, string folder, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "The indexed folder {Folder} moved to {NewFolder}")]
    private static partial void LogMoved(ILogger logger, string folder, string newFolder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "macOS made no bookmark for {Folder}; it stays indexed until Valafind quits")]
    private static partial void LogNotRememberable(ILogger logger, string folder, Exception? exception);
}
