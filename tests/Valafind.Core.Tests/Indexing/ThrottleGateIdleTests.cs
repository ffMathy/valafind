using System.Diagnostics;
using Valafind.Core.Indexing;
using Xunit;

namespace Valafind.Core.Tests.Indexing;

public class ThrottleGateIdleTests
{
    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10))
                Assert.Fail($"Timed out waiting for: {what}");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Lets the loop reach its next await before time moves.</summary>
    private static Task Settle() => Task.Delay(100, TestContext.Current.CancellationToken);

    [Fact]
    public async Task IdleGateSamplesRarelyAndWakesForWork()
    {
        var time = new ManualTimeProvider();
        var monitor = new FakeMonitor();
        using var gate = new ThrottleGate(monitor, () => new ThrottleOptions(30, 25, true), time, new FakeOwnUsage(), 4);
        await gate.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => monitor.Samples == 1, "first sample");
        await Settle();

        // Busy owner: every 2 s.
        time.Advance(TimeSpan.FromSeconds(2));
        await WaitFor(() => monitor.Samples == 2, "regular sample");
        await Settle(); // the loop armed its next (regular) delay

        gate.SetIdle(true);
        Assert.True(gate.IsDormant);
        await Settle();
        time.Advance(TimeSpan.FromSeconds(2)); // the delay armed while busy still elapses …
        await WaitFor(() => monitor.Samples == 3, "last regular sample");
        await Settle();
        for (var i = 0; i < 25; i++)            // … then 50 s pass with no sampling at all
        {
            time.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.Equal(3, monitor.Samples);

        // Work arrives: an immediate fresh sample, then the normal rate again.
        gate.SetIdle(false);
        await WaitFor(() => monitor.Samples == 4, "wake-up sample");
        await Settle();
        time.Advance(TimeSpan.FromSeconds(2));
        await WaitFor(() => monitor.Samples == 5, "regular rate resumed");
    }

    [Fact]
    public async Task WaiterWakesADormantGate()
    {
        var time = new ManualTimeProvider();
        var monitor = new FakeMonitor { OnBattery = true };
        using var gate = new ThrottleGate(monitor, () => new ThrottleOptions(30, 25, true), time, new FakeOwnUsage(), 4);
        gate.SetIdle(true);
        await gate.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => monitor.Samples == 1, "first sample");
        await Settle();
        Assert.False(gate.IsOpen);

        monitor.OnBattery = false;
        var open = gate.WaitUntilOpenAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => monitor.Samples == 2, "wake-up sample");
        await open.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await WaitFor(() => gate.IsDormant, "dormant again once the waiter left");
    }

    [Fact]
    public void IdleIntervalIsNeverShorterThanTheRegularOne()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), new ThrottleOptions(30, 25, true).EffectiveIdleSampleInterval);
        Assert.Equal(TimeSpan.FromSeconds(90),
            new ThrottleOptions(30, 25, true, TimeSpan.FromSeconds(90)).EffectiveIdleSampleInterval);
        Assert.Equal(TimeSpan.FromSeconds(5),
            new ThrottleOptions(30, 25, true, null, TimeSpan.FromSeconds(5)).EffectiveIdleSampleInterval);
    }
}
