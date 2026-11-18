using Valafind.Core.Configuration;
using Valafind.Core.Indexing;
using Xunit;

namespace Valafind.Core.Tests.Indexing;

internal sealed class TempTree : IDisposable
{
    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "valafind-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Root = Path.GetFullPath(Root);
    }

    public string Root { get; }

    public string P(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public string File(string relative, string content = "hello world", DateTime? modifiedUtc = null)
    {
        var path = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        if (modifiedUtc is { } m)
        {
            System.IO.File.SetLastWriteTimeUtc(path, m);
            System.IO.File.SetLastAccessTimeUtc(path, m);
        }
        return path;
    }

    public string Dir(string relative)
    {
        var path = P(relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public class UserFileDiscoveryTests
{
    private static UserFileDiscovery Create(TempTree tree, ValafindSettings? settings = null, Dictionary<string, string>? env = null,
        string homeRelative = "home")
    {
        var home = tree.Dir(homeRelative);
        return new UserFileDiscovery(settings ?? new ValafindSettings(), home, tree.P("data"), TimeProvider.System,
            name => env is not null && env.TryGetValue(name, out var v) ? v : null);
    }

    [Fact]
    public void RootsDefaultToHomeAndDropNestedDuplicates()
    {
        using var tree = new TempTree();
        Assert.Equal([tree.P("home")], Create(tree).Roots);

        var settings = new ValafindSettings { Roots = [tree.P("a"), tree.P("a/b"), tree.P("c") + Path.DirectorySeparatorChar, tree.P("a")] };
        Assert.Equal([tree.P("a"), tree.P("c")], Create(tree, settings).Roots);
    }

    [Fact]
    public void WithoutAHomeDefault_NoRootsMeansNothingIsIndexed()
    {
        // The macOS App Sandbox: only folders the user picked may be read, so an empty list indexes nothing.
        using var tree = new TempTree();
        tree.File("home/Documents/report.txt");
        var discovery = new UserFileDiscovery(new ValafindSettings(), tree.Dir("home"), tree.P("data"), TimeProvider.System,
            _ => null, indexHomeByDefault: false);

        Assert.Empty(discovery.Roots);
        Assert.Empty(discovery.EnumerateAllFiles(TestContext.Current.CancellationToken));
        Assert.True(discovery.IsExcluded(tree.P("home/Documents/report.txt")));
    }

    [Theory]
    [InlineData("/Users/me/Library/Containers/app.valafind.Valafind/Data", true, "/Users/me")]
    [InlineData("/Users/me/Library/Containers/app.valafind.Valafind/Data", false, "/Users/me/Library/Containers/app.valafind.Valafind/Data")]
    [InlineData("/Users/me", true, "/Users/me")]
    public void RealHomeLeavesTheSandboxContainer(string home, bool inAppSandbox, string expected) =>
        Assert.Equal(expected, AppPaths.RealHome(home, inAppSandbox));

    [Fact]
    public void WalkWithStatsReportsTheSameFilesWithTheirSizeAndMtime()
    {
        using var tree = new TempTree();
        var when = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        tree.File("home/a.txt", "alpha", when);
        tree.File("home/sub/b.md", "bravo bravo", when.AddDays(1));
        var discovery = Create(tree);

        var withStats = discovery.EnumerateAllFilesWithStats(TestContext.Current.CancellationToken).ToList();
        Assert.Equal(discovery.EnumerateAllFiles(TestContext.Current.CancellationToken), withStats.Select(f => f.Path));
        Assert.All(withStats, f =>
        {
            var info = new FileInfo(f.Path);
            Assert.Equal(info.Length, f.Size);
            Assert.Equal(new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), f.ModifiedUtc);
        });
        Assert.Equal(new DateTimeOffset(when, TimeSpan.Zero), withStats.Single(f => f.Path.EndsWith("a.txt", StringComparison.Ordinal)).ModifiedUtc);
    }

    [Fact]
    public void WalksBreadthFirstAndAppliesExclusions()
    {
        using var tree = new TempTree();
        tree.File("home/top.txt");
        tree.File("home/z/deep/deeper.txt");
        tree.File("home/z/mid.txt");
        tree.File("home/b/mid2.md");
        tree.File("home/b/empty.txt", content: "");
        tree.File("home/.config/app.json");
        tree.File("home/.hidden-file");
        tree.File("home/proj/node_modules/lib/index.js");
        tree.File("home/proj/.git/HEAD");
        tree.File("home/proj/App.csproj", "<Project/>");
        tree.File("home/proj/bin/Debug/out.txt");
        tree.File("home/proj/obj/x.json");
        tree.File("home/proj/Program.cs", "class P {}");
        tree.File("home/scripts/bin/tool.sh", "echo hi");
        tree.File("home/cache/CACHEDIR.TAG", "Signature: 8a477f597d28d172789f06886806bc55");
        tree.File("home/cache/blob.bin");
        tree.File("home/Documents/~$lock.docx");
        tree.File("data/index.db");

        var files = Create(tree).EnumerateAllFiles(TestContext.Current.CancellationToken).ToList();
        var rel = files.Select(f => Path.GetRelativePath(tree.P("home"), f).Replace('\\', '/')).ToList();

        Assert.Equal(
        [
            "top.txt",
            "b/mid2.md",
            "proj/App.csproj",
            "proj/Program.cs",
            "z/mid.txt",
            "scripts/bin/tool.sh",
            "z/deep/deeper.txt",
        ], rel);
    }

    [Fact]
    public void NeverFollowsSymlinks()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symlinks needs privileges on Windows");
        using var tree = new TempTree();
        tree.File("home/real/a.txt");
        tree.File("outside/secret.txt");
        Directory.CreateSymbolicLink(tree.P("home/link-dir"), tree.P("outside"));
        File.CreateSymbolicLink(tree.P("home/link-file.txt"), tree.P("outside/secret.txt"));
        Directory.CreateSymbolicLink(tree.P("home/real/loop"), tree.P("home"));

        var files = Create(tree).EnumerateAllFiles(TestContext.Current.CancellationToken).ToList();
        Assert.Equal([tree.P("home/real/a.txt")], files);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void UnreadableDirectoriesAreSkipped()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "POSIX permissions");
        Assert.SkipWhen(Environment.UserName == "root", "root bypasses permissions");
        using var tree = new TempTree();
        tree.File("home/ok.txt");
        tree.File("home/locked/x.txt");
        File.SetUnixFileMode(tree.P("home/locked"), UnixFileMode.None);
        try
        {
            var files = Create(tree).EnumerateAllFiles(TestContext.Current.CancellationToken).ToList();
            Assert.Equal([tree.P("home/ok.txt")], files);
        }
        finally
        {
            File.SetUnixFileMode(tree.P("home/locked"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void IsExcludedUsesFileSystemContext()
    {
        using var tree = new TempTree();
        tree.File("home/proj/App.csproj", "<Project/>");
        tree.File("home/proj/bin/a.dll");
        tree.File("home/notes/bin/a.txt");
        var discovery = Create(tree);
        Assert.True(discovery.IsExcluded(tree.P("home/proj/bin/a.dll")));
        Assert.True(discovery.IsExcluded(tree.P("home/proj/bin")));
        Assert.False(discovery.IsExcluded(tree.P("home/notes/bin/a.txt")));
        Assert.True(discovery.IsExcluded(tree.P("data/index.db")));
        Assert.True(discovery.IsExcluded(tree.P("elsewhere/a.txt")));
        Assert.True(discovery.IsExcluded(""));
        Assert.False(discovery.IsExcluded(tree.P("home/does-not-exist-yet.txt")));
    }

    [Fact]
    public void UserExcludesFromSettingsApply()
    {
        using var tree = new TempTree();
        tree.File("home/keep/a.txt");
        tree.File("home/Private/b.txt");
        tree.File("home/x/Archive/c.txt");
        var settings = new ValafindSettings { Excludes = [tree.P("home/Private"), "Archive"] };
        var files = Create(tree, settings).EnumerateAllFiles(TestContext.Current.CancellationToken).ToList();
        Assert.Equal([tree.P("home/keep/a.txt")], files);
    }

    [Fact]
    public void EnumerateUnderWalksOneSubtree()
    {
        using var tree = new TempTree();
        tree.File("home/a/1.txt");
        tree.File("home/a/b/2.txt");
        tree.File("home/c/3.txt");
        var discovery = Create(tree);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal([tree.P("home/a/1.txt"), tree.P("home/a/b/2.txt")], discovery.EnumerateUnder(tree.P("home/a"), ct).ToList());
        Assert.Empty(discovery.EnumerateUnder(tree.P("home/.cache"), ct));
    }

    [Fact]
    public void CancellationStopsTheWalk()
    {
        using var tree = new TempTree();
        tree.File("home/a/1.txt");
        tree.File("home/b/2.txt");
        using var cts = new CancellationTokenSource();
        var discovery = Create(tree);
        using var e = discovery.EnumerateAllFiles(cts.Token).GetEnumerator();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => e.MoveNext());
    }

    [Fact]
    public void FrequentFilesOnLinuxComeFromRecentlyUsedXbelKdeAndXdgDirsRanked()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux recent-file sources");
        using var tree = new TempTree();
        var home = tree.P("home");
        var now = DateTime.UtcNow;
        var hot = tree.File("home/work/hot.txt", modifiedUtc: now.AddDays(-200));
        var cold = tree.File("home/work/cold.txt", modifiedUtc: now.AddDays(-200));
        var kde = tree.File("home/work/kde.txt", modifiedUtc: now.AddDays(-200));
        var docsRecent = tree.File("home/Docs/recent.md", modifiedUtc: now.AddDays(-1));
        tree.File("home/Docs/old.md", modifiedUtc: now.AddDays(-200));
        tree.File("home/Docs/a/b/c/too-deep.md", modifiedUtc: now.AddDays(-1));
        tree.File("home/Downloads/dl.pdf", modifiedUtc: now.AddDays(-10));
        tree.File("home/Documents/not-the-xdg-dir.txt", modifiedUtc: now.AddDays(-1));
        tree.File("home/work/empty.txt", content: "");
        var outside = tree.File("outside/x.txt");

        string Iso(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", System.Globalization.CultureInfo.InvariantCulture);
        tree.File("home/.local/share/recently-used.xbel", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <xbel version="1.0" xmlns:bookmark="http://www.freedesktop.org/standards/desktop-bookmarks">
              <bookmark href="{new Uri(hot).AbsoluteUri}" added="{Iso(now.AddDays(-30))}" modified="{Iso(now.AddDays(-1))}" visited="{Iso(now.AddDays(-1))}">
                <info><metadata owner="http://freedesktop.org"><bookmark:applications>
                  <bookmark:application name="x" exec="x" modified="{Iso(now.AddDays(-1))}" count="25"/>
                </bookmark:applications></metadata></info>
              </bookmark>
              <bookmark href="{new Uri(cold).AbsoluteUri}" added="{Iso(now.AddDays(-80))}" modified="{Iso(now.AddDays(-80))}" visited="{Iso(now.AddDays(-80))}"/>
              <bookmark href="{new Uri(outside).AbsoluteUri}" added="{Iso(now)}" modified="{Iso(now)}" visited="{Iso(now)}"/>
              <bookmark href="{new Uri(tree.P("home/work/empty.txt")).AbsoluteUri}" added="{Iso(now)}" visited="{Iso(now)}"/>
              <bookmark href="{new Uri(tree.P("home/work/gone.txt")).AbsoluteUri}" added="{Iso(now)}" visited="{Iso(now)}"/>
            </xbel>
            """);
        tree.File("home/.local/share/RecentDocuments/kde.txt.desktop", $"[Desktop Entry]\nType=Link\nURL[$e]={new Uri(kde).AbsoluteUri}\n");
        tree.File("home/.config/user-dirs.dirs", "XDG_DOCUMENTS_DIR=\"$HOME/Docs\"\nXDG_DESKTOP_DIR=\"$HOME/\"\n");

        var discovery = Create(tree);
        var frequent = discovery.EnumerateFrequentFiles(TestContext.Current.CancellationToken).ToList();

        Assert.Equal(hot, frequent[0]); // high count, used yesterday
        Assert.Contains(docsRecent, frequent);
        Assert.Contains(kde, frequent);
        Assert.Contains(cold, frequent);
        Assert.Contains(tree.P("home/Downloads/dl.pdf"), frequent);
        Assert.DoesNotContain(tree.P("home/Docs/old.md"), frequent);
        Assert.DoesNotContain(tree.P("home/Docs/a/b/c/too-deep.md"), frequent);
        Assert.DoesNotContain(tree.P("home/Documents/not-the-xdg-dir.txt"), frequent);
        Assert.DoesNotContain(outside, frequent);
        Assert.DoesNotContain(tree.P("home/work/empty.txt"), frequent);
        Assert.Equal(frequent.Count, frequent.Distinct().Count());
        Assert.True(frequent.IndexOf(cold) > frequent.IndexOf(docsRecent), "an 80-day-old single use ranks below yesterday's edit");
    }

    [Fact]
    public void ScoreFavoursFrequencyAndRecency()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(UserFileDiscovery.Score(10, now.AddDays(-1), now) > UserFileDiscovery.Score(1, now.AddDays(-1), now));
        Assert.True(UserFileDiscovery.Score(1, now, now) > UserFileDiscovery.Score(1, now.AddDays(-30), now));
        Assert.Equal(UserFileDiscovery.Score(0, now, now) / 2, UserFileDiscovery.Score(0, now.AddDays(-21), now), 6);
        Assert.Equal(1, UserFileDiscovery.Score(0, now.AddDays(1), now), 6); // future timestamps clamp
    }

    [Fact]
    public void RunLinesToleratesMissingTool()
    {
        var lines = UserFileDiscovery.RunLines("/nonexistent/tool-" + Guid.NewGuid(), ["x"], TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.Empty(lines);
    }

    [Fact]
    public void RunLinesCollectsOutputAndHonoursTimeout()
    {
        Assert.SkipUnless(File.Exists("/bin/sh"), "needs /bin/sh");
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(["a", "b"], UserFileDiscovery.RunLines("/bin/sh", ["-c", "printf 'a\\n\\nb\\n'"], TimeSpan.FromSeconds(10), ct));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        UserFileDiscovery.RunLines("/bin/sh", ["-c", "echo first; sleep 30"], TimeSpan.FromMilliseconds(500), ct);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }
}
