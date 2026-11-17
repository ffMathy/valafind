#pragma warning disable CA1707 // xunit test names use underscores for readability

using Valafind.Core.Indexing;
using Xunit;

namespace Valafind.Core.Tests.Indexing;

public class PidControllerTests
{
    private static readonly TimeSpan Dt = TimeSpan.FromSeconds(2);

    [Fact]
    public void Starts_at_the_initial_output_and_moves_towards_the_setpoint()
    {
        var pid = new PidController(new PidGains(0.5, 0.2, 0, 0, 10), 3);
        Assert.Equal(3, pid.Output);
        Assert.True(pid.Update(10, 5, Dt) > 3); // below the setpoint: more
        pid.Reset(3);
        Assert.True(pid.Update(10, 15, Dt) < 3); // above: less
    }

    [Fact]
    public void Output_stays_within_its_limits()
    {
        var pid = new PidController(new PidGains(5, 5, 0, 1, 4), 2);
        for (var i = 0; i < 20; i++)
            Assert.InRange(pid.Update(100, 0, Dt), 1, 4);
        Assert.Equal(4, pid.Output);
        for (var i = 0; i < 20; i++)
            Assert.InRange(pid.Update(0, 100, Dt), 1, 4);
        Assert.Equal(1, pid.Output);
    }

    [Fact]
    public void A_long_stretch_at_the_limit_does_not_wind_up_the_integral()
    {
        var pid = new PidController(new PidGains(0.1, 0.5, 0, 0, 4), 1);
        for (var i = 0; i < 500; i++)
            pid.Update(10, 0, Dt); // can never reach the setpoint
        Assert.Equal(4, pid.Output);
        // The moment the measurement overshoots, the output comes down right away instead of first unwinding
        // 500 samples' worth of accumulated error.
        pid.Update(10, 20, Dt);
        Assert.True(pid.Output < 4, $"output {pid.Output} stayed at the limit");
    }

    [Fact]
    public void Without_allowIncrease_the_output_may_fall_but_not_rise()
    {
        var pid = new PidController(new PidGains(0.5, 0.5, 0, 0, 10), 2);
        for (var i = 0; i < 10; i++)
            Assert.Equal(2, pid.Update(10, 0, Dt, allowIncrease: false));
        Assert.True(pid.Update(10, 20, Dt, allowIncrease: false) < 2);
    }

    [Fact]
    public void Derivative_acts_on_the_measurement_not_on_setpoint_changes()
    {
        var pid = new PidController(new PidGains(0, 0, 1, -100, 100, DerivativeSmoothing: 1), 0);
        pid.Update(10, 5, Dt);
        Assert.Equal(0, pid.Update(50, 5, Dt), 6); // setpoint jumped, measurement did not: no kick
        Assert.True(pid.Update(50, 9, Dt) < 0); // measurement rising: the derivative brakes
    }

    [Fact]
    public void Rejects_inverted_limits_and_bad_smoothing()
    {
        Assert.Throws<ArgumentException>(() => new PidController(new PidGains(1, 1, 1, 5, 1), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PidController(new PidGains(1, 1, 1, 0, 1, DerivativeSmoothing: 0), 0));
    }
}

public class IndexingPaceTests
{
    private static readonly TimeSpan Dt = TimeSpan.FromSeconds(2);

    [Theory]
    [InlineData(0.2, 1, 0.2)]
    [InlineData(0.5, 1, 0.5)]
    [InlineData(1.0, 1, 1.0)]
    [InlineData(2.5, 3, 2.5 / 3)]
    [InlineData(4.0, 4, 1.0)]
    [InlineData(12.0, 8, 1.0)]
    public void Effort_maps_to_workers_and_a_duty_cycle(double effort, int workers, double duty)
    {
        Assert.Equal(workers, IndexingPace.ConcurrencyFor(effort, 8));
        Assert.Equal(duty, IndexingPace.DutyCycleFor(effort, 8), 6);
    }

    [Fact]
    public void Rest_makes_a_worker_work_the_duty_cycle_of_the_time()
    {
        Assert.Equal(TimeSpan.Zero, IndexingPace.RestFor(TimeSpan.FromSeconds(1), 1));
        Assert.Equal(TimeSpan.FromSeconds(1), IndexingPace.RestFor(TimeSpan.FromSeconds(1), 0.5));
        Assert.Equal(TimeSpan.FromSeconds(4), IndexingPace.RestFor(TimeSpan.FromSeconds(1), 0.2));
        Assert.Equal(IndexingPace.MaxRest, IndexingPace.RestFor(TimeSpan.FromMinutes(1), 0.2));
        Assert.Equal(TimeSpan.Zero, IndexingPace.RestFor(TimeSpan.Zero, 0.2));
    }

    [Theory]
    [InlineData(30, 27)]
    [InlineData(100, 95)]
    [InlineData(10, 9)]
    [InlineData(5, 4.5)]
    public void Target_sits_a_little_under_the_limit(double limit, double target) =>
        Assert.Equal(target, IndexingPace.TargetFor(limit), 6);

    [Fact]
    public void Speeds_up_while_the_machine_has_room_under_the_limit()
    {
        // The scenario from the issue: limit 30 %, the machine at 10 %.
        var pace = new IndexingPace(maxConcurrency: 8, processorCount: 8);
        pace.ReportInFlight(1);
        var before = pace.Effort;
        pace.Update(30, 10, Dt);
        Assert.True(pace.Effort > before, $"effort {pace.Effort} did not rise from {before}");
    }

    [Fact]
    public void Slows_down_when_the_machine_is_above_the_target()
    {
        var pace = new IndexingPace(maxConcurrency: 8, processorCount: 8, initialEffort: 4);
        pace.ReportInFlight(4);
        pace.Update(30, 60, Dt);
        Assert.True(pace.Effort < 4);
    }

    [Fact]
    public void Does_not_speed_up_when_the_allowed_workers_are_not_all_busy()
    {
        var pace = new IndexingPace(maxConcurrency: 8, processorCount: 8, initialEffort: 3);
        pace.ReportInFlight(1); // a walk over unchanged files: one file at a time needs work
        for (var i = 0; i < 20; i++)
            pace.Update(30, 5, Dt);
        Assert.Equal(3, pace.Effort, 6);
    }

    [Fact]
    public void Raises_Changed_when_the_effort_changes()
    {
        var pace = new IndexingPace(8, 8);
        var raised = 0;
        pace.Changed += (_, _) => raised++;
        pace.ReportInFlight(1);
        pace.Update(30, 5, Dt);
        Assert.Equal(1, raised);
        pace.EnsureAtLeast(0.5); // already above
        Assert.Equal(1, raised);
    }

    /// <summary>
    /// Closed loop against a simple model of the machine: other programs use <paramref name="othersPercent"/>, and every
    /// core's worth of effort shows up as <paramref name="coresPerEffort"/> cores of CPU in the next reading (a worker
    /// may use more than one core: ONNX Runtime's intra-op pool, the walk, SQLite). The pace must settle near the target
    /// without oscillating, whatever that ratio.
    /// </summary>
    [Theory]
    [InlineData(8, 30, 2, 1.0)]
    [InlineData(8, 30, 2, 2.0)]
    [InlineData(8, 30, 2, 3.0)]
    [InlineData(8, 30, 2, 0.6)]
    [InlineData(32, 30, 2, 2.0)]
    [InlineData(4, 50, 10, 1.5)]
    [InlineData(16, 30, 5, 1.0)]
    [InlineData(2, 100, 0, 1.0)]
    public void Settles_at_the_target_on_a_simulated_machine(int processors, double limit, double othersPercent, double coresPerEffort)
    {
        var pace = new IndexingPace(IndexingPace.DefaultMaxConcurrency(processors), processors);
        var target = IndexingPace.TargetFor(limit);
        var readings = new List<double>();
        var effort = pace.Effort;
        for (var i = 0; i < 120; i++) // four minutes of 2-second samples
        {
            pace.ReportInFlight(pace.Concurrency); // plenty of files to index
            var own = Math.Min(100 - othersPercent, effort * coresPerEffort * 100 / processors);
            var reading = othersPercent + own;
            readings.Add(reading);
            effort = pace.Effort; // applied from the next interval on
            pace.Update(limit, reading, Dt, own);
        }

        var reachable = othersPercent + pace.MaxConcurrency * coresPerEffort * 100 / processors;
        var expected = Math.Min(target, reachable);
        var settled = readings.Skip(30).ToList(); // within a minute
        Assert.All(settled, r => Assert.InRange(r, expected - 3, Math.Max(expected, target) + 3));
        // Never far above the limit, even while finding the level.
        Assert.All(readings, r => Assert.True(r <= limit + 15, $"reading {r:0.0} overshot the {limit} % limit"));
        if (othersPercent < target)
            Assert.InRange(pace.CoresPerEffort, coresPerEffort * 0.9, coresPerEffort * 1.1);
    }

    [Fact]
    public void The_disk_limit_takes_over_when_the_disk_is_the_bottleneck()
    {
        // 8 cores, CPU limit 30 % (target 27 %), disk limit 25 % (target 22.5 %). Each unit of effort costs 1 core
        // and 15 % disk: the disk runs out first (at 1.5 effort), long before the CPU would (at ~2 effort).
        const int processors = 8;
        var pace = new IndexingPace(8, processors);
        double effort = pace.Effort;
        var disks = new List<double>();
        for (var i = 0; i < 120; i++)
        {
            pace.ReportInFlight(pace.Concurrency);
            var ownCpu = effort * 100 / processors;
            var ownDisk = Math.Min(100, effort * 15);
            disks.Add(ownDisk);
            effort = pace.Effort;
            pace.Update(new PaceReading(30, 2 + ownCpu, ownCpu, 25, ownDisk, ownDisk), Dt);
        }
        Assert.Equal(PaceLimit.Disk, pace.LimitedBy);
        Assert.Equal(22.5, pace.DiskTargetPercent, 6);
        Assert.All(disks.Skip(30), d => Assert.InRange(d, 19.5, 25.5));
        Assert.InRange(pace.DiskPerEffort, 13.5, 16.5);
    }

    [Fact]
    public void A_busy_disk_slows_indexing_to_a_trickle_but_never_stops_it()
    {
        var pace = new IndexingPace(8, 8, initialEffort: 4);
        for (var i = 0; i < 60; i++)
        {
            pace.ReportInFlight(pace.Concurrency);
            pace.Update(new PaceReading(30, 10, 5, 25, DiskBusyPercent: 100, OwnDiskBusyPercent: 1), Dt); // another program copies files
        }
        Assert.Equal(IndexingPace.MinEffort, pace.Effort, 6);
        Assert.Equal(PaceLimit.Disk, pace.LimitedBy);

        // The copy ends: the CPU controller was tracking, so indexing speeds up again without a jump.
        var previous = pace.Effort;
        for (var i = 0; i < 60; i++)
        {
            pace.ReportInFlight(pace.Concurrency);
            var own = pace.Effort * 100 / 8;
            pace.Update(new PaceReading(30, 2 + own, own, 25, 3, 1), Dt);
            Assert.True(pace.Effort - previous < 1.5, "effort jumped");
            previous = pace.Effort;
        }
        Assert.Equal(PaceLimit.Cpu, pace.LimitedBy);
        Assert.True(pace.Effort > 1);
    }

    [Fact]
    public void Without_disk_metrics_only_the_CPU_steers()
    {
        var pace = new IndexingPace(8, 8);
        pace.ReportInFlight(1);
        pace.Update(new PaceReading(30, 10, 8, 25, DiskBusyPercent: null), Dt);
        Assert.Equal(PaceLimit.Cpu, pace.LimitedBy);
        Assert.Equal(0, pace.DiskTargetPercent);
        Assert.True(pace.Effort > 1);
    }

    [Fact]
    public void Backs_off_when_other_programs_start_using_the_CPU()
    {
        const int processors = 8;
        var pace = new IndexingPace(8, processors);
        double others = 2, effort = pace.Effort;
        for (var i = 0; i < 120; i++)
        {
            if (i == 60)
                others = 20; // something else starts
            pace.ReportInFlight(pace.Concurrency);
            var own = effort * 100 / processors;
            effort = pace.Effort;
            pace.Update(30, others + own, Dt, own);
        }
        // 27 % target, 20 % taken by others: about half a core is left for indexing.
        Assert.InRange(pace.Effort * 100 / processors, 4, 10);
    }
}

public class IndexingWorkersTests
{
    private static readonly TimeSpan Dt = TimeSpan.FromSeconds(2);

    private static IndexingPace PaceWithConcurrency(int concurrency) =>
        new(maxConcurrency: 8, processorCount: 8, initialEffort: concurrency);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    public void Never_runs_more_items_at_once_than_the_pace_allows(int concurrency)
    {
        using var workers = new IndexingWorkers(PaceWithConcurrency(concurrency));
        using var full = new ManualResetEventSlim();
        int running = 0, peak = 0;
        for (var i = 0; i < 40; i++)
        {
            workers.Run(() =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref peak, now);
                if (now >= concurrency)
                    full.Set();
                full.Wait(TimeSpan.FromSeconds(10)); // hold the first items until every slot is taken
                Interlocked.Decrement(ref running);
            }, TestContext.Current.CancellationToken);
        }
        workers.WaitIdle(TestContext.Current.CancellationToken);
        Assert.Equal(concurrency, peak);
        Assert.Equal(0, workers.InFlight);
        Assert.True(workers.ThreadCount <= concurrency);
    }

    [Fact]
    public void A_higher_pace_adds_workers_while_items_are_waiting()
    {
        var pace = PaceWithConcurrency(1);
        using var workers = new IndexingWorkers(pace);
        using var release = new ManualResetEventSlim();
        int running = 0, peak = 0;
        void Item()
        {
            InterlockedMax(ref peak, Interlocked.Increment(ref running));
            release.Wait(TimeSpan.FromSeconds(10));
            Interlocked.Decrement(ref running);
        }

        workers.Run(Item, TestContext.Current.CancellationToken);
        var second = Task.Run(() => workers.Run(Item, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Thread.Sleep(100);
        Assert.False(second.IsCompleted, "the second item must wait for a slot");

        pace.EnsureAtLeast(2); // e.g. the controller found room under the limit
        Assert.True(second.Wait(TimeSpan.FromSeconds(10)), "a faster pace did not free a slot");
        SpinWait.SpinUntil(() => Volatile.Read(ref running) == 2, TimeSpan.FromSeconds(10));
        Assert.Equal(2, Volatile.Read(ref peak));
        release.Set();
        workers.WaitIdle(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void A_failing_item_fails_the_next_wait()
    {
        using var workers = new IndexingWorkers(PaceWithConcurrency(2));
        workers.Run(() => throw new InvalidOperationException("boom"), TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => workers.WaitIdle(TestContext.Current.CancellationToken));
        workers.WaitIdle(TestContext.Current.CancellationToken); // reported once
    }

    [Fact]
    public void Reports_the_items_in_flight_to_the_pace()
    {
        var pace = PaceWithConcurrency(2);
        using var workers = new IndexingWorkers(pace);
        using var release = new ManualResetEventSlim();
        workers.Run(() => release.Wait(TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken);
        workers.Run(() => release.Wait(TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken);
        // Both slots busy and the machine far under the limit: the pace may speed up.
        var before = pace.Effort;
        pace.Update(30, 5, Dt);
        Assert.True(pace.Effort > before);
        release.Set();
        workers.WaitIdle(TestContext.Current.CancellationToken);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
