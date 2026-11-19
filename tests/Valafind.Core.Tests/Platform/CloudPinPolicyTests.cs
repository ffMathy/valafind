using Valafind.Core.Platform;
using Xunit;
using static Valafind.Core.Platform.CloudPinPolicy;

namespace Valafind.Core.Tests.Platform;

/// <summary>How a Windows file Valafind pinned is given back (runs on every OS: it is pure logic).</summary>
public class CloudPinPolicyTests
{
    [Theory]
    [InlineData(Unspecified)]
    [InlineData(Inherit)]
    public void StepOneAsksTheSyncClientToFreeItThenStepTwoRestoresTheOriginal(int original)
    {
        // Step 1: still pinned by Valafind, downloaded and untouched: "Free up space"; look again later.
        Assert.Equal(new Step(Unpinned, Done: false), Decide(Pinned, original, onlineOnly: false, modified: false, inPinnedFolder: false));
        // Not freed yet: nothing to do but wait.
        Assert.Equal(new Step(null, Done: false), Decide(Unpinned, original, onlineOnly: false, modified: false, inPinnedFolder: false));
        // Freed: back to the original setting, done.
        Assert.Equal(new Step(original, Done: true), Decide(Unpinned, original, onlineOnly: true, modified: false, inPinnedFolder: false));
    }

    [Fact]
    public void AnOriginallyUnpinnedFileIsDoneOnceUnpinned()
    {
        Assert.Equal(new Step(Unpinned, Done: true), Decide(Pinned, Unpinned, onlineOnly: false, modified: false, inPinnedFolder: false));
        Assert.Equal(new Step(null, Done: true), Decide(Unpinned, Unpinned, onlineOnly: false, modified: false, inPinnedFolder: false));
    }

    [Theory]
    [InlineData(Pinned)]
    [InlineData(Unpinned)]
    public void AFileWrittenToIsKeptOnTheDeviceAndNeverSetToFreeUpSpace(int now)
    {
        Assert.Equal(new Step(Unspecified, Done: true), Decide(now, Unspecified, onlineOnly: false, modified: true, inPinnedFolder: false));
        // Even when it was "free up space" before: someone is working on it now.
        if (now == Pinned)
            Assert.Equal(new Step(Unspecified, Done: true), Decide(now, Unpinned, onlineOnly: false, modified: true, inPinnedFolder: false));
    }

    [Fact]
    public void AFileThatNeverArrivedOrWhoseFolderIsKeptGetsItsOriginalSettingBack()
    {
        Assert.Equal(new Step(Unspecified, Done: true), Decide(Pinned, Unspecified, onlineOnly: true, modified: false, inPinnedFolder: false));
        Assert.Equal(new Step(Unpinned, Done: true), Decide(Pinned, Unpinned, onlineOnly: true, modified: false, inPinnedFolder: false));
        Assert.Equal(new Step(Unspecified, Done: true), Decide(Pinned, Unspecified, onlineOnly: false, modified: false, inPinnedFolder: true));
    }

    [Theory]
    [InlineData(Unspecified)]
    [InlineData(Inherit)]
    [InlineData(3)] // excluded from sync
    public void APinTheUserChangedMeanwhileIsLeftAlone(int now)
    {
        Assert.Equal(new Step(null, Done: true), Decide(now, Unspecified, onlineOnly: false, modified: false, inPinnedFolder: false));
        Assert.Equal(new Step(null, Done: true), Decide(now, Unpinned, onlineOnly: true, modified: true, inPinnedFolder: true));
    }
}
