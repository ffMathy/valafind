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
    public bool OnBattery { get; set; }
    public bool Throw { get; set; }
    public int Samples;

    public PressureSample Sample()
    {
        Interlocked.Increment(ref Samples);
        if (Throw)
            throw new InvalidOperationException("boom");
        return new PressureSample(DateTimeOffset.UtcNow, Cpu, DiskBusy, DiskBytes, OnBattery);
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
        public ThrottleOptions Options = new(30, 25, TimeSpan.FromSeconds(10), true);
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
    public void StartsClosedAndOpensOnlyAfterCalmPeriod()
    {
        using var rig = new Rig();
        Assert.Equal(IndexingState.PausedSystemBusy, rig.Gate.State);
        rig.Gate.Tick(); // calm since t0
        Assert.Equal(IndexingState.PausedSystemBusy, rig.Gate.State);
        for (var i = 0; i < 4; i++)
            Assert.Equal(IndexingState.PausedSystemBusy, rig.Step()); // 8 s calm
        Assert.Equal(IndexingState.Running, rig.Step()); // 10 s calm
        Assert.True(rig.Gate.IsOpen);
    }

    [Fact]
    public void BusySampleResetsCalmPeriod()
    {
        using var rig = new Rig();
        rig.Gate.Tick();
        rig.Step();
        rig.Step(); // 4 s calm
        rig.Monitor.Cpu = 80;
        Assert.Equal(IndexingState.PausedSystemBusy, rig.Step());
        rig.Monitor.Cpu = 5;
        for (var i = 0; i < 5; i++)
            Assert.Equal(IndexingState.PausedSystemBusy, rig.Step()); // calm since this point: 0..8 s
        Assert.Equal(IndexingState.Running, rig.Step());
    }

    [Fact]
    public void ClosesAfterTwoConsecutiveBusySamples()
    {
        using var rig = new Rig { Options = new(30, 25, TimeSpan.Zero, true) };
        rig.Gate.Tick();
        Assert.Equal(IndexingState.Running, rig.Gate.State);

        rig.Monitor.Cpu = 90;
        Assert.Equal(IndexingState.Running, rig.Step()); // first busy sample tolerated
        rig.Monitor.Cpu = 10;
        Assert.Equal(IndexingState.Running, rig.Step()); // streak broken
        rig.Monitor.Cpu = 90;
        Assert.Equal(IndexingState.Running, rig.Step());
        Assert.Equal(IndexingState.PausedSystemBusy, rig.Step());
    }

    [Fact]
    public void DiskBusyCountsAsPressure()
    {
        using var rig = new Rig { Options = new(30, 25, TimeSpan.Zero, true) };
        rig.Gate.Tick();
        rig.Monitor.DiskBusy = 60;
        rig.Step();
        Assert.Equal(IndexingState.PausedSystemBusy, rig.Step());
        rig.Monitor.DiskBusy = null; // unknown disk metrics never block
        Assert.Equal(IndexingState.Running, rig.Step());
    }

    [Fact]
    public void BatteryClosesImmediatelyWhenConfigured()
    {
        using var rig = new Rig { Options = new(30, 25, TimeSpan.Zero, true) };
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
        using var rig = new Rig { Options = new(30, 25, TimeSpan.Zero, true) };
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
        rig.Monitor.Cpu = 95;
        rig.Monitor.OnBattery = true;
        rig.Gate.Tick();
        Assert.Equal(IndexingState.PausedOnBattery, rig.Gate.State);

        rig.Gate.OverrideUntil = rig.Time.GetUtcNow() + TimeSpan.FromSeconds(5);
        Assert.Equal(IndexingState.Running, rig.Gate.State);
        Assert.Equal(IndexingState.Running, rig.Step()); // t+2
        Assert.Equal(IndexingState.Running, rig.Step()); // t+4
        Assert.Equal(IndexingState.PausedOnBattery, rig.Step()); // t+6: expired
        rig.Monitor.OnBattery = false;
        Assert.Equal(IndexingState.PausedSystemBusy, rig.Step());
    }

    [Fact]
    public void OwnCpuIsSubtractedBeforeComparing()
    {
        using var rig = new Rig(processors: 4) { Options = new(30, 25, TimeSpan.Zero, true) };
        rig.Monitor.Cpu = 50;
        rig.Gate.Tick(); // establishes own-usage baseline; 50 % > 30 % → busy
        Assert.Equal(IndexingState.PausedSystemBusy, rig.Gate.State);

        // We burnt 3.2 s CPU over 2 s wall on 4 cores = 40 % of the machine → others use 10 %.
        rig.Own.Cpu += TimeSpan.FromSeconds(3.2);
        Assert.Equal(IndexingState.Running, rig.Step());
        Assert.Equal(10, rig.Gate.LastEffectiveSample!.CpuPercent, 3);
        Assert.Equal(50, rig.Gate.LastSample!.CpuPercent, 3);

        // Same machine load but we were idle → others really use 50 % (two busy samples close the gate).
        Assert.Equal(IndexingState.Running, rig.Step());
        Assert.Equal(IndexingState.PausedSystemBusy, rig.Step());
    }

    [Fact]
    public void PersistentMonitorFailureEventuallyAssumesCalm()
    {
        using var rig = new Rig { Options = new(30, 25, TimeSpan.Zero, true) };
        rig.Monitor.Throw = true;
        rig.Gate.Tick();
        for (var i = 0; i < 3; i++)
            Assert.Equal(IndexingState.PausedSystemBusy, rig.Step());
        Assert.Equal(IndexingState.Running, rig.Step());
    }

    [Fact]
    public async Task WaitUntilOpenCompletesWhenGateOpens()
    {
        using var rig = new Rig { Options = new(30, 25, TimeSpan.FromSeconds(4), true) };
        var ct = TestContext.Current.CancellationToken;
        var wait = rig.Gate.WaitUntilOpenAsync(ct);
        rig.Gate.Tick();
        rig.Step();
        Assert.False(wait.IsCompleted);
        rig.Step();
        await wait.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.True(rig.Gate.WaitUntilOpenAsync(ct).IsCompleted);

        // Closing creates a fresh signal; waiting again blocks until reopened.
        rig.Monitor.Cpu = 99;
        rig.Step();
        rig.Step();
        var second = rig.Gate.WaitUntilOpenAsync(ct);
        Assert.False(second.IsCompleted);
        rig.Gate.OverrideUntil = rig.Time.GetUtcNow().AddMinutes(1);
        await second.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    [Fact]
    public async Task WaitUntilOpenHonoursCancellationAndDispose()
    {
        var rig = new Rig();
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
        using var rig = new Rig { Options = new(30, 25, TimeSpan.Zero, true) };
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
            () => new ThrottleOptions(30, 25, TimeSpan.Zero, true, TimeSpan.FromMilliseconds(20)));
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

    [Theory]
    [InlineData(31, null, true)]
    [InlineData(30, null, false)]
    [InlineData(10, 26.0, true)]
    [InlineData(10, 25.0, false)]
    public void IsBusy(double cpu, double? disk, bool expected) =>
        Assert.Equal(expected, PressureMath.IsBusy(new PressureSample(DateTimeOffset.UtcNow, cpu, disk, null, false),
            new ThrottleOptions(30, 25, TimeSpan.Zero, true)));
}
