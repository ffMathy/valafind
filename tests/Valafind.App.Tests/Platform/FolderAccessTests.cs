using Microsoft.Extensions.Logging.Abstractions;
using Valafind.App.Platform;
using Valafind.Core.Configuration;
using Xunit;

namespace Valafind.App.Tests.Platform;

public sealed class FolderAccessTests
{
    [Fact]
    public void Restore_KeepsFoldersWhoseBookmarksResolve_AndReportsNoChange()
    {
        var settings = new ValafindSettings
        {
            Roots = ["/Users/me/Documents", "/Volumes/Photos"],
            RootBookmarks = new() { ["/Users/me/Documents"] = "docs", ["/Volumes/Photos"] = "photos" },
        };
        var bookmarks = new FakeBookmarks { ["docs"] = new("/Users/me/Documents", null), ["photos"] = new("/Volumes/Photos", null) };

        Assert.False(FolderAccess.Restore(settings, bookmarks, NullLogger.Instance));

        Assert.Equal(["/Users/me/Documents", "/Volumes/Photos"], settings.Roots);
        Assert.Equal(["docs", "photos"], bookmarks.Resolved);
    }

    [Fact]
    public void Restore_DropsFoldersThatAreGoneOrWereNeverRemembered()
    {
        var settings = new ValafindSettings
        {
            Roots = ["/Users/me/Documents", "/Users/me/Deleted", "/Users/me/Unremembered"],
            RootBookmarks = new() { ["/Users/me/Documents"] = "docs", ["/Users/me/Deleted"] = "deleted", ["/Users/me/Stray"] = "stray" },
        };
        var bookmarks = new FakeBookmarks { ["docs"] = new("/Users/me/Documents", null) };

        Assert.True(FolderAccess.Restore(settings, bookmarks, NullLogger.Instance));

        Assert.Equal(["/Users/me/Documents"], settings.Roots);
        Assert.Equal(new Dictionary<string, string> { ["/Users/me/Documents"] = "docs" }, settings.RootBookmarks);
    }

    [Fact]
    public void Restore_FollowsMovedFoldersAndStoresRefreshedBookmarks()
    {
        var settings = new ValafindSettings
        {
            Roots = ["/Users/me/Notes", "/Users/me/Work"],
            RootBookmarks = new() { ["/Users/me/Notes"] = "notes", ["/Users/me/Work"] = "work" },
        };
        var bookmarks = new FakeBookmarks
        {
            ["notes"] = new("/Users/me/Archive/Notes", null),
            ["work"] = new("/Users/me/Work", "work-refreshed"),
        };

        Assert.True(FolderAccess.Restore(settings, bookmarks, NullLogger.Instance));

        Assert.Equal(["/Users/me/Archive/Notes", "/Users/me/Work"], settings.Roots);
        Assert.Equal("notes", settings.RootBookmarks["/Users/me/Archive/Notes"]);
        Assert.Equal("work-refreshed", settings.RootBookmarks["/Users/me/Work"]);
    }

    [Fact]
    public void Remember_ReturnsTheBookmarkOrNullWhenMacOsRefuses()
    {
        var bookmarks = new FakeBookmarks { Created = { ["/Users/me/Documents"] = "docs" } };

        Assert.Equal("docs", FolderAccess.Remember("/Users/me/Documents", bookmarks, NullLogger.Instance));
        Assert.Null(FolderAccess.Remember("/Users/me/Elsewhere", bookmarks, NullLogger.Instance));
    }

    [Fact]
    public void NothingIsRequiredOutsideTheAppSandbox()
    {
        Assert.False(FolderAccess.IsRequired); // Tests never run in the macOS App Sandbox.
        var settings = new ValafindSettings { Roots = ["/home/u/Documents"] };
        Assert.False(FolderAccess.Restore(settings, NullLogger.Instance));
        Assert.Equal(["/home/u/Documents"], settings.Roots);
        Assert.Null(FolderAccess.Remember("/home/u/Documents", NullLogger.Instance));
    }

    private sealed class FakeBookmarks : Dictionary<string, ResolvedBookmark>, IFolderBookmarks
    {
        public Dictionary<string, string> Created { get; } = [];

        public List<string> Resolved { get; } = [];

        public string? Create(string path) => Created.GetValueOrDefault(path);

        public ResolvedBookmark? Resolve(string bookmark)
        {
            Resolved.Add(bookmark);
            return TryGetValue(bookmark, out var resolved) ? resolved : null;
        }
    }
}
