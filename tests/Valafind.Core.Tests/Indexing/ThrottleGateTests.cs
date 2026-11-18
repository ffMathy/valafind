using Valafind.Core.Abstractions;
using Valafind.Core.Indexing;
using Valafind.Core.Platform;
using Xunit;

namespace Valafind.Core.Tests.Indexing;

internal sealed class FakeMonitor : ISystemPressureMonitor
{
    public double Cpu { get; set; }
    public double? DiskBusy { get; set; }
    public double? DiskBytes { get; set; }
    public double? Gpu { get; set; }
    public double? OwnGpu { get; set; }
    public bool OnBattery { get; set; }
    public bool Throw { get; set; }
    public int Samples;

    public PressureSample Sample()
    {
        Interlocked.Increment(ref Samples);
        if (Throw)
            throw new InvalidOperationException("boom");
        return new PressureSample(DateTimeOffset.UtcNow, Cpu, DiskBusy, DiskBytes, OnBattery, Gpu, OwnGpu);
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeOwnUsage : IOwnUsageProbe
{
    public TimeSpan Cpu { get; set; }
    public long? Io { get; set; }
    public OwnUsage Read() => new(Cpu, Io);
}

public class ThrottleGateTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private sealed class Rig : IDisposable
    {
        public readonly ManualTimeProvider Time = new();
        public readonly FakeMonitor Monitor = new();
        public readonly FakeOwnUsage Own = new();
        public ThrottleOptions Options = new(30, 25, true);
        public readonly ThrottleGate Gate;

        public Rig(int processors = 4)
        {
            Gate = new ThrottleGate(Monitor, () => Options, Time, Own, processors);
        }

        /// <summary>Advances one sampling interval and samples.</summary>
        public IndexingState Step()
        {
            Time.Advance(Interval);
            Gate.Tick();
            return Gate.State;
        }

        public void Dispose() => Gate.Dispose();
    }

    [Fact]
    public void StartsOpen()
    {
        using var rig = new Rig();
        Assert.Equal(IndexingState.Running, rig.Gate.State);
        Assert.True(rig.Gate.WaitUntilOpenAsync(TestContext.Current.CancellationToken).IsCompleted);
        rig.Gate.Tick();
        Assert.True(rig.Gate.IsOpen);
    }

    [Theory]
    [InlineData(100, null)]
    [InlineData(5, 100.0)]
    [InlineData(100, 100.0)]
    public void System_load_never_pauses_indexing(double cpu, double? disk)
    {
        // CPU and disk only steer the pace (IndexingPace); they never close the gate.
        using var rig = new Rig();
        rig.Monitor.Cpu = cpu;
        rig.Monitor.DiskBusy = disk;
        for (var i = 0; i < 20; i++)
            Assert.Equal(IndexingState.Running, rig.Step());
    }

    [Fact]
    public void A_busy_GPU_slows_indexing_only_while_the_model_runs_on_the_GPU()
    {
        using var rig = new Rig();
        rig.Monitor.Cpu = 5;
        rig.Monitor.Gpu = 100; // a game keeps the GPU busy
        rig.Monitor.OwnGpu = 1;
        rig.Options = rig.Options with { MaxGpuPercent = 30, SteerGpu = false };
        for (var i = 0; i < 30; i++)
        {
            rig.Gate.Pace.ReportInFlight(rig.Gate.Pace.Concurrency);
            Assert.Equal(IndexingState.Running, rig.Step());
        }
        Assert.Equal(PaceLimit.Cpu, rig.Gate.Pace.LimitedBy);
        Assert.Equal(0, rig.Gate.Pace.GpuTargetPercent);

        rig.Options = rig.Options with { SteerGpu = true };
        for (var i = 0; i < 60; i++)
        {
            rig.Gate.Pace.ReportInFlight(rig.Gate.Pace.Concurrency);
            Assert.Equal(IndexingState.Running, rig.Step()); // slows down, never pauses
        }
        Assert.Equal(PaceLimit.Gpu, rig.Gate.Pace.LimitedBy);
        Assert.Equal(27, rig.Gate.Pace.GpuTargetPercent, 6);
        Assert.Equal(IndexingPace.MinEffort, rig.Gate.Pace.Effort, 6);
        Assert.Equal(99, rig.Gate.LastEffectiveSample!.GpuPercent!.Value, 6);
    }

    [Fact]
    public void ThrottleOptionsFromSettingsCarriesTheGpuLimit()
    {
        var settings = new Valafind.Core.Configuration.ValafindSettings { MaxGpuPercent = 40 };
        var cpuOnly = ThrottleOptions.From(settings);
        Assert.Equal(40, cpuOnly.MaxGpuPercent);
        Assert.False(cpuOnly.SteerGpu);
        Assert.True(ThrottleOptions.From(settings, usesGpu: true).SteerGpu);
    }

    [Fact]
    public void BatteryClosesImmediatelyWhenConfigured()
    {
        using var rig = new Rig();
        rig.Gate.Tick();
        Assert.True(rig.Gate.IsOpen);
        rig.Monitor.OnBattery = true;
        Assert.Equal(IndexingState.PausedOnBattery, rig.Step());
        rig.Options = rig.Options with { PauseOnBattery = false };
        Assert.Equal(IndexingState.Running, rig.Step());
        rig.Options = rig.Options with { PauseOnBattery = true };
        Assert.Equal(IndexingState.PausedOnBattery, rig.Step());
        rig.Monitor.OnBattery = false;
        Assert.Equal(IndexingState.Running, rig.Step());
    }

    [Fact]
    public void UserPauseWinsOverEverything()
    {
        using var rig = new Rig();
        rig.Gate.Tick();
        rig.Gate.SetUserPaused(true);
        Assert.Equal(IndexingState.PausedByUser, rig.Gate.State);
        rig.Gate.OverrideUntil = rig.Time.GetUtcNow().AddMinutes(5);
        Assert.Equal(IndexingState.PausedByUser, rig.Gate.State);
        rig.Gate.SetUserPaused(false);
        Assert.Equal(IndexingState.Running, rig.Gate.State);
    }

    [Fact]
    public void OverrideForcesOpenUntilItExpires()
    {
        using var rig = new Rig();
        rig.Monitor.OnBattery = true;
        rig.Gate.Tick();
        Assert.Equal(IndexingState.PausedOnBattery, rig.Gate.State);

        rig.Gate.OverrideUntil = rig.Time.GetUtcNow() + TimeSpan.FromSeconds(5);
        Assert.Equal(IndexingState.Running, rig.Gate.State);
        Assert.Equal(IndexingState.Running, rig.Step()); // t+2
        Assert.Equal(IndexingState.Running, rig.Step()); // t+4
        Assert.Equal(IndexingState.PausedOnBattery, rig.Step()); // t+6: expired
        rig.Monitor.OnBattery = false;
        Assert.Equal(IndexingState.Running, rig.Step());
    }

    [Fact]
    public void OwnCpuIsSubtractedForTheEffectiveSample()
    {
        using var rig = new Rig(processors: 4);
        rig.Monitor.Cpu = 50;
        rig.Gate.Tick(); // establishes own-usage baseline

        // We burnt 3.2 s CPU over 2 s wall on 4 cores = 40 % of the machine → others use 10 %.
        rig.Own.Cpu += TimeSpan.FromSeconds(3.2);
        rig.Step();
        Assert.Equal(10, rig.Gate.LastEffectiveSample!.CpuPercent, 3);
        Assert.Equal(50, rig.Gate.LastSample!.CpuPercent, 3);
    }

    [Fact]
    public void A_failing_monitor_never_blocks()
    {
        using var rig = new Rig();
        rig.Monitor.Throw = true;
        rig.Gate.Tick();
        for (var i = 0; i < 5; i++)
            Assert.Equal(IndexingState.Running, rig.Step());
    }

    [Fact]
    public async Task WaitUntilOpenCompletesWhenGateOpens()
    {
        using var rig = new Rig();
        var ct = TestContext.Current.CancellationToken;
        rig.Monitor.OnBattery = true;
        rig.Gate.Tick();
        var wait = rig.Gate.WaitUntilOpenAsync(ct);
        Assert.False(wait.IsCompleted);
        rig.Monitor.OnBattery = false;
        rig.Step();
        await wait.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.True(rig.Gate.WaitUntilOpenAsync(ct).IsCompleted);

        // Closing creates a fresh signal; waiting again blocks until reopened.
        rig.Gate.SetUserPaused(true);
        var second = rig.Gate.WaitUntilOpenAsync(ct);
        Assert.False(second.IsCompleted);
        rig.Gate.SetUserPaused(false);
        await second.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    [Fact]
    public async Task WaitUntilOpenHonoursCancellationAndDispose()
    {
        var rig = new Rig();
        rig.Gate.SetUserPaused(true);
        using var cts = new CancellationTokenSource();
        var wait = rig.Gate.WaitUntilOpenAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);

        var pending = rig.Gate.WaitUntilOpenAsync(TestContext.Current.CancellationToken);
        rig.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public void ChangedIsRaisedOnSamplesAndStateChanges()
    {
        using var rig = new Rig();
        var states = new List<IndexingState>();
        rig.Gate.Changed += (_, s) => states.Add(s);
        rig.Gate.Tick();
        rig.Gate.SetUserPaused(true);
        rig.Gate.SetUserPaused(true); // no change → no event
        Assert.Equal([IndexingState.Running, IndexingState.PausedByUser], states);
    }

    [Fact]
    public async Task BackgroundLoopSamplesPeriodically()
    {
        var monitor = new FakeMonitor();
        using var gate = new ThrottleGate(monitor,
            () => new ThrottleOptions(30, 25, true, TimeSpan.FromMilliseconds(20)));
        var ct = TestContext.Current.CancellationToken;
        await gate.StartAsync(ct);
        await gate.WaitUntilOpenAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref monitor.Samples) < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(10, ct);
        Assert.True(monitor.Samples >= 3);
    }
}

public class PressureMathTests
{
    [Theory]
    [InlineData(1000, 1000, 4, 25)]
    [InlineData(4000, 1000, 4, 100)]
    [InlineData(8000, 1000, 4, 100)] // clamped
    [InlineData(0, 1000, 4, 0)]
    [InlineData(500, 0, 4, 0)] // no wall time
    [InlineData(500, 1000, 1, 50)]
    public void OwnCpuPercent(double cpuMs, double wallMs, int cpus, double expected) =>
        Assert.Equal(expected, PressureMath.OwnCpuPercent(TimeSpan.FromMilliseconds(cpuMs), TimeSpan.FromMilliseconds(wallMs), cpus), 6);

    [Fact]
    public void SubtractOwnNeverGoesNegative()
    {
        var s = new PressureSample(DateTimeOffset.UtcNow, 20, null, null, false);
        Assert.Equal(0, PressureMath.SubtractOwn(s, 35, null, TimeSpan.FromSeconds(2)).CpuPercent);
    }

    [Fact]
    public void SubtractOwnScalesDiskBusyByForeignThroughputShare()
    {
        // 10 MB/s total, we did 15 MB over 2 s = 7.5 MB/s → others 2.5 MB/s = 25 % of throughput.
        var s = new PressureSample(DateTimeOffset.UtcNow, 50, 80, 10_000_000, false);
        var e = PressureMath.SubtractOwn(s, 10, 15_000_000, TimeSpan.FromSeconds(2));
        Assert.Equal(40, e.CpuPercent, 6);
        Assert.Equal(2_500_000, e.DiskBytesPerSecond!.Value, 3);
        Assert.Equal(20, e.DiskBusyPercent!.Value, 6);
    }

    [Fact]
    public void SubtractOwnKeepsDiskBusyWhenThroughputUnknown()
    {
        var s = new PressureSample(DateTimeOffset.UtcNow, 50, 80, null, false);
        var e = PressureMath.SubtractOwn(s, 0, 15_000_000, TimeSpan.FromSeconds(2));
        Assert.Equal(80, e.DiskBusyPercent);
    }
}

public class ThrottleGatePaceTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private sealed class Rig : IDisposable
    {
        public readonly ManualTimeProvider Time = new();
        public readonly FakeMonitor Monitor = new();
        public readonly FakeOwnUsage Own = new();
        public readonly IndexingPace Pace = new(maxConcurrency: 8, processorCount: 8);
        public readonly ThrottleGate Gate;

        public Rig()
        {
            Gate = new ThrottleGate(Monitor, () => new ThrottleOptions(30, 25, true), Time, Own, 8, Pace);
            Pace.ReportInFlight(8); // plenty of work
        }

        public void Step(double machineCpu, double ownCpu = 0)
        {
            Monitor.Cpu = machineCpu;
            Time.Advance(Interval);
            Own.Cpu += TimeSpan.FromSeconds(Interval.TotalSeconds * 8 * ownCpu / 100);
            Gate.Tick();
        }

        public void Dispose() => Gate.Dispose();
    }

    [Fact]
    public void Speeds_up_while_open_and_the_machine_has_room()
    {
        using var rig = new Rig();
        rig.Step(5);
        Assert.True(rig.Gate.IsOpen);
        var before = rig.Pace.Effort;
        for (var i = 0; i < 5; i++)
            rig.Step(10, ownCpu: 5);
        Assert.True(rig.Pace.Effort > before, $"effort {rig.Pace.Effort} did not rise from {before}");
        Assert.Equal(27, rig.Pace.TargetPercent, 6);
    }

    [Fact]
    public void Holds_while_paused_or_idle()
    {
        using var rig = new Rig();
        rig.Step(5);
        rig.Gate.SetUserPaused(true);
        var before = rig.Pace.Effort;
        for (var i = 0; i < 5; i++)
            rig.Step(1);
        Assert.Equal(before, rig.Pace.Effort);

        rig.Gate.SetUserPaused(false);
        rig.Gate.SetIdle(true);
        for (var i = 0; i < 5; i++)
            rig.Step(1);
        Assert.Equal(before, rig.Pace.Effort);
    }

    [Fact]
    public void Index_now_keeps_at_least_one_full_worker_however_busy_the_machine()
    {
        using var rig = new Rig();
        rig.Gate.OverrideUntil = rig.Time.GetUtcNow() + TimeSpan.FromHours(1);
        for (var i = 0; i < 20; i++)
            rig.Step(90, ownCpu: 10);
        Assert.True(rig.Gate.IsOpen);
        Assert.True(rig.Pace.Effort >= 1);
    }
}
