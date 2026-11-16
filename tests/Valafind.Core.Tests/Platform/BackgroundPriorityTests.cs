using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Valafind.Core.Platform;
using Xunit;

namespace Valafind.Core.Tests.Platform;

public sealed class BackgroundPriorityTests
{
    [Theory]
    [InlineData(Architecture.X64, 251, 252, 186)]
    [InlineData(Architecture.Arm64, 30, 31, 178)]
    [InlineData(Architecture.X86, 289, 290, 224)]
    [InlineData(Architecture.Arm, 314, 315, 224)]
    public void LinuxSyscallNumbers_MatchKernelTables(Architecture architecture, int ioprioSet, int ioprioGet, int gettid)
    {
        var numbers = BackgroundPriority.LinuxSyscallNumbers(architecture);
        Assert.NotNull(numbers);
        Assert.Equal((nint)ioprioSet, numbers.Value.IoPrioSet);
        Assert.Equal((nint)ioprioGet, numbers.Value.IoPrioGet);
        Assert.Equal((nint)gettid, numbers.Value.GetTid);
    }

    [Fact]
    public void LinuxSyscallNumbers_UnknownArchitecture_IsNull()
    {
        Assert.Null(BackgroundPriority.LinuxSyscallNumbers(Architecture.Wasm));
    }

    [Fact]
    public void LinuxSyscallArguments_ArePointerSized()
    {
        // glibc's syscall() takes C long arguments, which are pointer-sized on every Linux ABI. A 64-bit
        // managed long would shift every argument on 32-bit ARM and x86.
        var method = typeof(BackgroundPriority).GetNestedType("Linux", System.Reflection.BindingFlags.NonPublic)!
            .GetMethod("syscall", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.Equal(typeof(nint), method.ReturnType);
        Assert.All(method.GetParameters(), p => Assert.Equal(typeof(nint), p.ParameterType));
    }

    [Fact]
    public void Linux_IdleIoPriority_IsAppliedAndRestored()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("ioprio is Linux-only.");
        }

        if (BackgroundPriority.LinuxSyscallNumbers(RuntimeInformation.ProcessArchitecture) is null)
        {
            Assert.Skip("No syscall numbers for this architecture.");
        }

        // Run on a dedicated thread: the scope changes per-thread priorities.
        (int? Before, int? Inside, int? After) observed = default;
        var thread = new Thread(() => observed = OperatingSystem.IsLinux() ? ObserveIoPriority() : default);
        thread.Start();
        thread.Join();
        var (before, inside, after) = observed;

        if (before is null)
        {
            Assert.Skip("ioprio_get is not permitted in this environment.");
        }

        const int idleClass = 3 << 13;
        Assert.Equal(idleClass, inside);
        Assert.Equal(before, after);
    }

    [SupportedOSPlatform("linux")]
    private static (int? Before, int? Inside, int? After) ObserveIoPriority()
    {
        var before = BackgroundPriority.GetLinuxIoPriorityForCurrentThread();
        int? inside;
        using (BackgroundPriority.EnterForCurrentThread())
        {
            inside = BackgroundPriority.GetLinuxIoPriorityForCurrentThread();
        }

        return (before, inside, BackgroundPriority.GetLinuxIoPriorityForCurrentThread());
    }
}
