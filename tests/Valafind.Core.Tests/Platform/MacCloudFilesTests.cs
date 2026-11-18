using Valafind.Core.Platform;
using Valafind.Core.Tests.Indexing;
using Xunit;

namespace Valafind.Core.Tests.Platform;

/// <summary>macOS online-only (dataless) files. The provider-independent logic runs everywhere with a fake dataless
/// flag; the tests that call into Foundation and the file system run on macOS only (the macOS CI job).</summary>
public class MacCloudFilesTests
{
    [Fact]
    public void ProviderKeyGroupsFilesByCloudStorageFolder()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
            return;
        }
        var home = Path.Combine(Path.GetTempPath(), "home");
        var cloud = new MacCloudFiles(home, _ => false);
        var oneDrive = Path.Combine(home, "Library", "CloudStorage", "OneDrive-Personal");

        Assert.Equal(oneDrive, cloud.ProviderKey(Path.Combine(oneDrive, "Taxes", "return.pdf")));
        Assert.Equal(oneDrive, cloud.ProviderKey(Path.Combine(oneDrive, "a.txt")));
        Assert.Equal("iCloud", cloud.ProviderKey(Path.Combine(home, "Library", "Mobile Documents", "com~apple~CloudDocs", "a.txt")));
        Assert.Equal("iCloud", cloud.ProviderKey(Path.Combine(home, "Documents", "a.txt")));
    }

    [Fact]
    public void RegularFilesAreNotDataless()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
            return;
        }
        using var tree = new TempTree();
        var path = tree.File("plain.txt", "local");
        Assert.False(MacFileFlags.IsDataless(path));
        Assert.False(new MacCloudFiles().IsOnlineOnly(path, File.GetAttributes(path)));
    }

    [Fact]
    public void FoundationEvictionApiIsReachable()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
            return;
        }
        Assert.True(MacCloudFiles.FileManagerEvictionAvailable());
    }

    [Fact]
    public void EvictingALocalFileFailsHarmlessly()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
            return;
        }
        using var tree = new TempTree();
        var path = tree.File("plain.txt", "local");

        // Not in iCloud or a File Provider domain: NSFileManager refuses, the file stays as it is.
        Assert.False(MacCloudFiles.EvictWithFileManager(path));
        Assert.Equal("local", File.ReadAllText(path));
    }

    [Fact]
    public void DownloadOfAFileThatIsAlreadyLocalNeedsNoEviction()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
            return;
        }
        using var tree = new TempTree();
        var path = tree.File("plain.txt", "local");
        var cloud = new MacCloudFiles();

        using (var lease = cloud.Download(path, 5, TestContext.Current.CancellationToken))
            Assert.NotNull(lease);

        Assert.Equal("local", File.ReadAllText(path));
        Assert.False(cloud.RefusesEviction(path));
    }

    [Fact]
    public void ProviderThatRefusesEvictionIsNotDownloadedFromAgain()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
            return;
        }
        using var tree = new TempTree();
        var path = tree.File("Library/CloudStorage/Fake/report.txt", "content");
        var cloud = new MacCloudFiles(tree.Root, _ => false, _ => true);
        Assert.True(cloud.CanDownload(path, 7, isMedia: false));

        // A real local file cannot be evicted (neither NSFileManager nor fileproviderctl will): remembered.
        Assert.False(cloud.Evict(path, DateTimeOffset.UtcNow));

        Assert.True(cloud.RefusesEviction(path));
        Assert.False(cloud.CanDownload(path, 7, isMedia: false));
        Assert.False(cloud.CanDownload(tree.File("Library/CloudStorage/Fake/other.txt", "x"), 1, isMedia: false));
        Assert.True(cloud.CanDownload(tree.File("Documents/mine.txt", "x"), 1, isMedia: false)); // iCloud is separate
        Assert.Equal("content", File.ReadAllText(path));
    }

    [Fact]
    public void RequestingADownloadOfALocalFileChangesNothing()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
            return;
        }
        using var tree = new TempTree();
        var path = tree.File("plain.txt", "local");
        var cloud = new MacCloudFiles();

        Assert.Null(cloud.RequestDownload(path, _ => true)); // not dataless: nothing to request
        MacCloudFiles.StartDownloading(path); // not in iCloud: refused or a no-op, never harmful
        cloud.Release(path, new CloudMark(0), DateTimeOffset.UtcNow);

        Assert.Equal("local", File.ReadAllText(path));
    }

    [Fact]
    public void UploadStateOfALocalFileIsUnknown()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
            return;
        }
        using var tree = new TempTree();
        // Not in iCloud or a File Provider domain: Foundation has no upload state for it, and the call is harmless.
        Assert.Null(MacCloudFiles.IsUploaded(tree.File("plain.txt", "local")));
    }

    [Fact]
    public void ProviderThatDoesNotReportUploadStateIsNeverDownloadedFrom()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
            return;
        }
        using var tree = new TempTree();
        var path = tree.File("Library/CloudStorage/Fake/report.txt", "content");
        var cloud = new MacCloudFiles(tree.Root, _ => true, _ => null);

        // Nothing could ever be given back safely, so nothing is downloaded (not even one file is left behind).
        Assert.False(cloud.CanDownload(path, 7, isMedia: false));
        Assert.True(cloud.RefusesEviction(path));
    }

    [Fact]
    public void FileWithChangesNotUploadedIsNeverEvicted()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
            return;
        }
        using var tree = new TempTree();
        var path = tree.File("Library/CloudStorage/Fake/draft.txt", "unsaved work");
        var since = DateTimeOffset.UtcNow.AddMinutes(1); // not written since: only the upload state decides
        var cloud = new MacCloudFiles(tree.Root, _ => false, _ => false);

        Assert.False(cloud.Evict(path, since));

        Assert.False(cloud.RefusesEviction(path)); // kept on purpose, not a refusal: the provider stays usable
        Assert.Equal("unsaved work", File.ReadAllText(path));
    }

    [Fact]
    public void FileWrittenToSinceItWasDownloadedIsNeverEvicted()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
            return;
        }
        using var tree = new TempTree();
        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        var path = tree.File("Library/CloudStorage/Fake/notes.txt", "edited by the user"); // written after `since`
        var uploadChecked = false;
        var cloud = new MacCloudFiles(tree.Root, _ => false, _ =>
        {
            uploadChecked = true;
            return true;
        });

        Assert.False(cloud.Evict(path, since));

        Assert.False(uploadChecked); // never got as far as asking to evict
        Assert.False(cloud.RefusesEviction(path));
        Assert.Equal("edited by the user", File.ReadAllText(path));
    }
}
