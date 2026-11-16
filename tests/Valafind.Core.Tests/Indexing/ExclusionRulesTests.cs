using Valafind.Core.Indexing;
using Xunit;

namespace Valafind.Core.Tests.Indexing;

public class ExclusionRulesTests
{
    private sealed class FakeProbe(params string[] existingFiles) : IExclusionProbe
    {
        public bool FileExists(string path) => existingFiles.Contains(path);

        public bool ContainsFile(string directory, IReadOnlyList<string> namesOrExtensions) =>
            existingFiles.Any(f =>
            {
                var sep = Math.Max(f.LastIndexOf('/'), f.LastIndexOf('\\'));
                if (sep < 0 || !f[..sep].Equals(directory, StringComparison.Ordinal))
                    return false;
                var name = f[(sep + 1)..];
                return namesOrExtensions.Any(n => n.StartsWith('.') ? name.EndsWith(n, StringComparison.OrdinalIgnoreCase) : name == n);
            });
    }

    private static ExclusionRules Linux(IEnumerable<string>? excludes = null, IExclusionProbe? probe = null, string[]? roots = null) =>
        new(PathFlavor.Linux, "/home/u", roots ?? ["/home/u"], "/home/u/.local/share/valafind", excludes ?? [], probe);

    private static ExclusionRules Windows(IEnumerable<string>? excludes = null) =>
        new(PathFlavor.Windows, @"C:\Users\U", [@"C:\Users\U"], @"C:\Users\U\AppData\Local\Valafind", excludes ?? [], null);

    private static ExclusionRules Mac() =>
        new(PathFlavor.MacOS, "/Users/u", ["/Users/u"], "/Users/u/Library/Application Support/Valafind", [], null);

    [Theory]
    [InlineData("/home/u", true, false)]
    [InlineData("/home/u/Documents/a.txt", false, false)]
    [InlineData("/home/u/Documents", true, false)]
    [InlineData("/home/u/.config/app/settings.json", false, true)]
    [InlineData("/home/u/.local/share/notes.txt", false, true)]
    [InlineData("/home/u/.local/share/valafind/index.db", false, true)]
    [InlineData("/home/u/proj/node_modules/x/index.js", false, true)]
    [InlineData("/home/u/proj/.git/config", false, true)]
    [InlineData("/home/u/proj/.venv/lib/a.py", false, true)]
    [InlineData("/home/u/proj/venv/lib/a.py", false, true)]
    [InlineData("/home/u/proj/src/__pycache__/a.pyc", false, true)]
    [InlineData("/home/u/proj/bower_components/a.js", false, true)]
    [InlineData("/home/u/.cache", true, true)]
    [InlineData("/home/u/Documents/.hidden.txt", false, true)]
    [InlineData("/home/u/Documents/~$report.docx", false, true)]
    [InlineData("/home/u/Library/notes.txt", false, false)] // Library is only special on macOS
    [InlineData("/home/u/AppData/notes.txt", false, false)] // AppData is only special on Windows
    [InlineData("/home/u/Node_Modules/a.js", false, false)] // case-sensitive file system: different name
    [InlineData("/etc/passwd", false, true)] // outside every root
    [InlineData("/home/user2/a.txt", false, true)] // sibling prefix is not "under" /home/u
    public void LinuxRules(string path, bool isDirectory, bool excluded) =>
        Assert.Equal(excluded, Linux().IsExcluded(path, isDirectory));

    [Theory]
    [InlineData(@"C:\Users\U\Documents\a.docx", false, false)]
    [InlineData(@"c:/users/u/documents/A.TXT", false, false)]
    [InlineData(@"C:\Users\U\AppData\Roaming\x.txt", false, true)]
    [InlineData(@"C:\Users\U\appdata", true, true)]
    [InlineData(@"C:\Users\U\AppData\Local\Valafind\index.db", false, true)]
    [InlineData(@"C:\Users\U\Docs\NODE_MODULES\a.js", false, true)]
    [InlineData(@"C:\Users\U\$RECYCLE.BIN\x.txt", false, true)]
    [InlineData(@"C:\Users\U\System Volume Information\x", false, true)]
    [InlineData(@"C:\Users\U\Projects\AppData\notes.txt", false, false)] // only the AppData directly under home
    [InlineData(@"D:\data\a.txt", false, true)]
    [InlineData(@"C:\Users\Ux\a.txt", false, true)]
    [InlineData(@"C:\Users\U\Documents\.git\HEAD", false, true)]
    public void WindowsRules(string path, bool isDirectory, bool excluded) =>
        Assert.Equal(excluded, Windows().IsExcluded(path, isDirectory));

    [Theory]
    [InlineData("/Users/u/Documents/a.pdf", false, false)]
    [InlineData("/Users/u/Library", true, true)]
    [InlineData("/Users/u/Library/Preferences/com.apple.x.plist", false, true)]
    [InlineData("/Users/u/library/Caches/x", false, true)] // case-insensitive APFS
    [InlineData("/Users/u/Library/Mobile Documents/com~apple~CloudDocs/a.pdf", false, false)]
    [InlineData("/Users/u/Library/Mobile Documents/com~apple~CloudDocs/.b.pdf.icloud", false, true)]
    [InlineData("/Users/u/Library/Mobile Documents/com~apple~CloudDocs/b.icloud", false, true)]
    [InlineData("/Users/u/Library/Application Support/Valafind/index.db", false, true)]
    [InlineData("/Users/u/Applications/Foo.app/Contents/Info.plist", false, true)]
    [InlineData("/Users/u/Pictures/Photos Library.photoslibrary/database/x", false, true)]
    [InlineData("/Users/u/.Trash/old.pdf", false, true)]
    [InlineData("/Users/u/Documents/.DS_Store", false, true)]
    public void MacRules(string path, bool isDirectory, bool excluded) =>
        Assert.Equal(excluded, Mac().IsExcluded(path, isDirectory));

    [Theory]
    [InlineData("/home/u/a/Secret/b.txt", true)]
    [InlineData("/home/u/a/Secretive/b.txt", false)]
    [InlineData("/home/u/private/x.txt", true)]
    [InlineData("/home/u/private", true)]
    [InlineData("/home/u/privateer/x.txt", false)]
    [InlineData("/home/u/Work/tmp/x.txt", true)]
    [InlineData("/home/u/Work/tmp2/x.txt", false)]
    public void UserExcludes(string path, bool excluded)
    {
        var rules = Linux(["Secret", "/home/u/private/", "~/Work/tmp", "  "]);
        Assert.Equal(excluded, rules.IsExcluded(path, isDirectory: false));
    }

    [Fact]
    public void WindowsUserExcludesAreCaseInsensitive()
    {
        var rules = Windows([@"c:\users\u\Private", "Build Output"]);
        Assert.True(rules.IsExcluded(@"C:\Users\U\PRIVATE\a.txt", false));
        Assert.True(rules.IsExcluded(@"C:\Users\U\x\build output\a.txt", false));
        Assert.False(rules.IsExcluded(@"C:\Users\U\Public\a.txt", false));
    }

    [Fact]
    public void ExplicitRootInsideExcludedDirectoryIsHonoured()
    {
        var rules = Linux(roots: ["/home/u/.config/notes"]);
        Assert.False(rules.IsExcluded("/home/u/.config/notes/todo.md", false));
        Assert.True(rules.IsExcluded("/home/u/.config/notes/.git/HEAD", false));
        Assert.True(rules.IsExcluded("/home/u/Documents/a.txt", false)); // not a root any more

        var mac = new ExclusionRules(PathFlavor.MacOS, "/Users/u", ["/Users/u/Library/Notes"], null, [], null);
        Assert.False(mac.IsExcluded("/Users/u/Library/Notes/a.txt", false));
    }

    [Fact]
    public void DataDirectoryOutsideDotFoldersIsExcluded()
    {
        var rules = new ExclusionRules(PathFlavor.Linux, "/home/u", ["/home/u"], "/home/u/ValafindData", [], null);
        Assert.True(rules.IsExcluded("/home/u/ValafindData/index.db", false));
        Assert.False(rules.IsExcluded("/home/u/ValafindDataX/a.txt", false));
    }

    [Theory]
    [InlineData("/home/u/app/bin/Debug/x.txt", true)]
    [InlineData("/home/u/app/obj/project.assets.json", true)]
    [InlineData("/home/u/scripts/bin/run.sh", false)] // no project file next to bin
    [InlineData("/home/u/web/dist/main.js", true)]
    [InlineData("/home/u/web/build/main.js", true)]
    [InlineData("/home/u/paper/build/paper.pdf", false)]
    [InlineData("/home/u/rust/target/debug/x", true)]
    [InlineData("/home/u/cache/x.bin", true)] // CACHEDIR.TAG
    [InlineData("/home/u/py/env/lib/x.py", true)] // pyvenv.cfg
    public void ContextDependentRules(string path, bool excluded)
    {
        var probe = new FakeProbe(
            "/home/u/app/App.csproj", "/home/u/web/package.json", "/home/u/rust/Cargo.toml",
            "/home/u/cache/CACHEDIR.TAG", "/home/u/py/env/pyvenv.cfg");
        Assert.Equal(excluded, Linux(probe: probe).IsExcluded(path, isDirectory: false));
    }

    [Fact]
    public void WalkerDirectoryCheckLooksAtLastComponentOnly()
    {
        var rules = Linux(probe: new FakeProbe("/home/u/app/App.sln"));
        Assert.True(rules.IsExcludedDirectory("/home/u/app/bin", "/home/u/app"));
        Assert.True(rules.IsExcludedDirectory("/home/u/x/.git", "/home/u/x"));
        Assert.False(rules.IsExcludedDirectory("/home/u/x/src", "/home/u/x"));
        Assert.True(rules.IsExcludedDirectory("/home/u/.local/share/valafind", "/home/u/.local/share"));
    }

    [Theory]
    [InlineData("/home/u/Documents/a.txt", false)]
    [InlineData("/home/u/proj/node_modules/x.js", true)]
    [InlineData("/home/u/proj/node_modules", true)]
    [InlineData("/home/u/.cache/x", true)]
    [InlineData("/home/u/a/.swp", true)]
    [InlineData("/opt/x", true)]
    public void NameOnlyPrefilter(string path, bool excluded) =>
        Assert.Equal(excluded, Linux().IsExcludedByNameOnly(path));

    [Theory]
    [InlineData(false, "/home/u/", "/home/u")]
    [InlineData(false, "/", "/")]
    [InlineData(true, @"C:\", @"C:\")]
    [InlineData(true, "C:/Users/U/", @"C:\Users\U")]
    [InlineData(true, @"\\server\share\", @"\\server\share")]
    public void Normalize(bool windows, string input, string expected)
    {
        var flavor = windows ? PathFlavor.Windows : PathFlavor.Linux;
        var rules = new ExclusionRules(flavor, flavor == PathFlavor.Windows ? @"C:\Users\U" : "/home/u", [], null, [], null);
        Assert.Equal(expected, rules.Normalize(input));
    }
}
