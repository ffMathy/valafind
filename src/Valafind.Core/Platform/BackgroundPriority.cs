using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Valafind.Core.Platform;

/// <summary>
/// Lowers CPU and I/O scheduling priority of the calling thread for background indexing work.
/// Dispose the returned handle on the same thread to restore the previous priorities.
/// Every OS call is best-effort: failures are swallowed and never surface to the caller.
/// </summary>
public static partial class BackgroundPriority
{
    /// <summary>Enters background mode for the current thread (Windows THREAD_MODE_BACKGROUND, Linux nice 19 +
    /// idle I/O class, macOS background QoS + throttled disk I/O policy) and sets the managed thread priority to Lowest.</summary>
    public static IDisposable EnterForCurrentThread()
    {
        var scope = new Scope(Environment.CurrentManagedThreadId);
        try
        {
            scope.PreviousThreadPriority = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.Lowest;
        }
        catch (Exception e) when (e is ThreadStateException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            scope.PreviousThreadPriority = null;
        }

        try
        {
            if (OperatingSystem.IsWindows())
                scope.WindowsBackground = Windows.Enter();
            else if (OperatingSystem.IsLinux())
                Linux.Enter(scope);
            else if (OperatingSystem.IsMacOS())
                Mac.Enter(scope);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            // Non-fatal: indexing simply runs at normal priority.
        }
        return scope;
    }

    /// <summary>
    /// Linux syscall numbers (ioprio_set, ioprio_get, gettid) for <paramref name="architecture"/>, or null
    /// where they are unknown. The numbers come from the kernel's per-architecture tables:
    /// x86_64 <c>arch/x86/entry/syscalls/syscall_64.tbl</c> (251, 252, 186);
    /// arm64 <c>include/uapi/asm-generic/unistd.h</c> (30, 31, 178);
    /// i386 <c>arch/x86/entry/syscalls/syscall_32.tbl</c> (289, 290, 224);
    /// 32-bit ARM EABI <c>arch/arm/tools/syscall.tbl</c> (314, 315, 224).
    /// </summary>
    internal static (nint IoPrioSet, nint IoPrioGet, nint GetTid)? LinuxSyscallNumbers(Architecture architecture) => architecture switch
    {
        Architecture.X64 => (251, 252, 186),
        Architecture.Arm64 => (30, 31, 178),
        Architecture.X86 => (289, 290, 224),
        Architecture.Arm => (314, 315, 224),
        _ => null,
    };

    /// <summary>The calling thread's I/O priority (ioprio_get), or null if unavailable. For tests.</summary>
    [SupportedOSPlatform("linux")]
    internal static int? GetLinuxIoPriorityForCurrentThread() => Linux.GetIoPriority();

    private sealed class Scope(int threadId) : IDisposable
    {
        public ThreadPriority? PreviousThreadPriority;
        public bool WindowsBackground;
        public int? LinuxTid;
        public int? LinuxPreviousNice;
        public int? LinuxPreviousIoPriority;
        public bool MacIoPolicySet;
        public int? MacPreviousIoPolicy;
        public uint? MacPreviousQos;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (Environment.CurrentManagedThreadId != threadId)
                return; // Priorities are per-thread; restoring from another thread would affect the wrong thread.
            try
            {
                if (OperatingSystem.IsWindows() && WindowsBackground)
                    Windows.Exit();
                else if (OperatingSystem.IsLinux())
                    Linux.Exit(this);
                else if (OperatingSystem.IsMacOS())
                    Mac.Exit(this);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
            {
            }
            try
            {
                if (PreviousThreadPriority is { } p)
                    Thread.CurrentThread.Priority = p;
            }
            catch (Exception e) when (e is ThreadStateException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static partial class Windows
    {
        private const int ThreadModeBackgroundBegin = 0x00010000;
        private const int ThreadModeBackgroundEnd = 0x00020000;

        public static bool Enter() => SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);

        public static void Exit() => _ = SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd);

        [LibraryImport("kernel32.dll")]
        private static partial nint GetCurrentThread();

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetThreadPriority(nint thread, int priority);
    }

    [SupportedOSPlatform("linux")]
    private static partial class Linux
    {
        private const int PrioProcess = 0;
        private const int IoPrioWhoProcess = 1;
        private const int IoPrioClassIdle = 3;
        private const int IoPrioClassShift = 13;

        public static void Enter(Scope scope)
        {
            var tid = GetTid();
            if (tid <= 0)
                return;
            scope.LinuxTid = tid;

            // getpriority returns -1 both as a legitimate value and as an error; errno disambiguates.
            Marshal.SetLastPInvokeError(0);
            var nice = getpriority(PrioProcess, (uint)tid);
            if (nice != -1 || Marshal.GetLastPInvokeError() == 0)
                scope.LinuxPreviousNice = nice;
            _ = setpriority(PrioProcess, (uint)tid, 19);

            if (Syscalls() is { } nr)
            {
                var previous = syscall(nr.IoPrioGet, IoPrioWhoProcess, tid, 0);
                if (previous >= 0)
                    scope.LinuxPreviousIoPriority = (int)previous;
                _ = syscall(nr.IoPrioSet, IoPrioWhoProcess, tid, IoPrioClassIdle << IoPrioClassShift);
            }
        }

        public static void Exit(Scope scope)
        {
            if (scope.LinuxTid is not { } tid)
                return;
            if (Syscalls() is { } nr && scope.LinuxPreviousIoPriority is { } io)
                _ = syscall(nr.IoPrioSet, IoPrioWhoProcess, tid, io);
            // Lowering nice back requires CAP_SYS_NICE (or RLIMIT_NICE); failure is expected and harmless.
            if (scope.LinuxPreviousNice is { } nice)
                _ = setpriority(PrioProcess, (uint)tid, nice);
        }

        private static (nint IoPrioSet, nint IoPrioGet, nint GetTid)? Syscalls() =>
            LinuxSyscallNumbers(RuntimeInformation.ProcessArchitecture);

        public static int? GetIoPriority()
        {
            var tid = GetTid();
            if (tid <= 0 || Syscalls() is not { } nr)
                return null;
            var value = syscall(nr.IoPrioGet, IoPrioWhoProcess, tid, 0);
            return value >= 0 ? (int)value : null;
        }

        private static int GetTid()
        {
            try
            {
                return gettid();
            }
            catch (EntryPointNotFoundException)
            {
                return Syscalls() is { } nr ? (int)syscall(nr.GetTid, 0, 0, 0) : -1;
            }
        }

        [LibraryImport("libc")]
        private static partial int gettid();

        [LibraryImport("libc", SetLastError = true)]
        private static partial int getpriority(int which, uint who);

        [LibraryImport("libc", SetLastError = true)]
        private static partial int setpriority(int which, uint who, int priority);

        // glibc: long syscall(long number, ...). C long is pointer-sized on every Linux ABI (LP64 and ILP32),
        // so nint is the matching type; a 64-bit C# long would be split across two registers/stack slots on
        // 32-bit ARM and x86 and shift every argument.
        [LibraryImport("libc", SetLastError = true)]
        private static partial nint syscall(nint number, nint arg1, nint arg2, nint arg3);
    }

    [SupportedOSPlatform("macos")]
    private static partial class Mac
    {
        private const string LibSystem = "/usr/lib/libSystem.dylib";
        private const int IopolTypeDisk = 0;
        private const int IopolScopeThread = 1;
        private const int IopolThrottle = 3;
        private const uint QosClassBackground = 0x09;

        public static void Enter(Scope scope)
        {
            var previous = getiopolicy_np(IopolTypeDisk, IopolScopeThread);
            if (previous >= 0)
                scope.MacPreviousIoPolicy = previous;
            scope.MacIoPolicySet = setiopolicy_np(IopolTypeDisk, IopolScopeThread, IopolThrottle) == 0;

            var qos = qos_class_self();
            if (pthread_set_qos_class_self_np(QosClassBackground, 0) == 0)
                scope.MacPreviousQos = qos;
        }

        public static void Exit(Scope scope)
        {
            if (scope.MacIoPolicySet)
                _ = setiopolicy_np(IopolTypeDisk, IopolScopeThread, scope.MacPreviousIoPolicy ?? 0);
            if (scope.MacPreviousQos is { } qos && qos != 0)
                _ = pthread_set_qos_class_self_np(qos, 0);
        }

        [LibraryImport(LibSystem)]
        private static partial int setiopolicy_np(int type, int scope, int policy);

        [LibraryImport(LibSystem)]
        private static partial int getiopolicy_np(int type, int scope);

        [LibraryImport(LibSystem)]
        private static partial int pthread_set_qos_class_self_np(uint qosClass, int relativePriority);

        [LibraryImport(LibSystem)]
        private static partial uint qos_class_self();
    }
}
