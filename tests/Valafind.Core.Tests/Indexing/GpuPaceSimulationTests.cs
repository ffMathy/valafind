#pragma warning disable CA1707 // xunit test names use underscores for readability

using Valafind.Core.Indexing;
using Valafind.Core.Platform;
using Xunit;

namespace Valafind.Core.Tests.Indexing;

/// <summary>Closed-loop tests of the GPU limit against a simulated machine whose GPU is busy in bursts.</summary>
public class GpuPaceSimulationTests
{
    [Theory]
    [InlineData(30, true)]
    [InlineData(60, true)]
    [InlineData(90, true)]
    [InlineData(90, false)] // Linux / macOS: Valafind's own GPU share is unknown
    public void Settles_under_the_GPU_limit_without_oscillating(double gpuPerWorker, bool ownKnown)
    {
        var run = GpuPlant.Run(gpuPerWorker, ownKnown, seed: 1);

        // The smoothed GPU utilisation (what the meter shows and the loop steers) sits at the 27 % target for a 30 % limit…
        Assert.InRange(run.SmoothedMean, 25, 29);
        Assert.True(run.SmoothedSd < 3, $"smoothed GPU swings by {run.SmoothedSd:0.0} points");
        // …and the effort holds steady instead of see-sawing (before smoothing: ±0.1–0.3 around 0.3–0.45).
        Assert.True(run.EffortSd < 0.03, $"effort swings by {run.EffortSd:0.00}");
    }

    [Fact]
    public void Backs_off_smoothly_when_another_program_takes_the_GPU_and_recovers_after()
    {
        var run = GpuPlant.Run(60, ownKnown: true, seed: 2, othersAt: s => s is >= 150 and < 300 ? 70 : 3, samples: 450);

        Assert.Equal(IndexingPace.MinEffort, run.Efforts[290], 6);
        // Recovered to the same steady level, without overshooting far past the limit on the way.
        Assert.InRange(run.Efforts[^1], run.Efforts[140] * 0.8, run.Efforts[140] * 1.2);
        Assert.All(run.Smoothed.Skip(330), g => Assert.InRange(g, 20, 32));
    }
}

/// <summary>A simulated machine: indexing workers alternate model batches, each keeping the GPU <c>gpuPerWorker</c> %
/// busy while it runs, with the rests the pace asks for. Every 2 s the GPU's average utilisation over the interval is
/// smoothed like <see cref="ThrottleGate"/> does and fed to the pace.</summary>
internal static class GpuPlant
{
    public sealed record Result(double SmoothedMean, double SmoothedSd, double EffortSd, IReadOnlyList<double> Efforts,
        IReadOnlyList<double> Smoothed);

    public static Result Run(double gpuPerWorker, bool ownKnown, int seed, Func<int, double>? othersAt = null, int samples = 400)
    {
        othersAt ??= _ => 3;
        const double Limit = 30, Tick = 0.02, Interval = 2;
        var rng = new Random(seed);
        var pace = new IndexingPace(8, 8);
        var workers = new List<(bool Working, double Left, double Busy)>();
        var efforts = new List<double>();
        var smoothedReadings = new List<double>();
        double? smoothed = null, smoothedOwn = null;
        for (var s = 0; s < samples; s++)
        {
            double busy = 0, ownBusy = 0;
            var others = othersAt(s);
            for (var t = 0.0; t < Interval; t += Tick)
            {
                var want = pace.Concurrency;
                while (workers.Count < want)
                    workers.Add((false, 0, 0));
                if (workers.Count > want)
                    workers.RemoveRange(want, workers.Count - want);
                double own = 0;
                for (var i = 0; i < workers.Count; i++)
                {
                    var w = workers[i];
                    w.Left -= Tick;
                    if (w.Working)
                    {
                        w.Busy += Tick;
                        own += gpuPerWorker;
                        if (w.Left <= 0)
                            w = (false, IndexingPace.RestFor(TimeSpan.FromSeconds(w.Busy), pace.DutyCycle).TotalSeconds, 0);
                    }
                    else if (w.Left <= 0)
                    {
                        w = (true, 0.3 + (rng.NextDouble() * 0.9), 0); // one model batch: 0.3–1.2 s
                    }
                    workers[i] = w;
                }
                var total = Math.Min(100, others + own);
                busy += total * Tick;
                ownBusy += Math.Min(own, total) * Tick;
            }
            pace.ReportInFlight(pace.Concurrency);
            smoothed = Smooth(smoothed, busy / Interval);
            smoothedOwn = Smooth(smoothedOwn, ownBusy / Interval);
            pace.Update(new PaceReading(30, 5, 2, 100, null, null, Limit, smoothed, ownKnown ? smoothedOwn : null),
                TimeSpan.FromSeconds(Interval));
            efforts.Add(pace.Effort);
            smoothedReadings.Add(smoothed.Value);
        }
        var tail = smoothedReadings.Skip(samples / 4).ToList();
        return new Result(tail.Average(), Sd(tail), Sd(efforts.Skip(samples / 4).ToList()), efforts, smoothedReadings);
    }

    private static double Smooth(double? average, double reading) =>
        average is { } previous ? previous + (IndexingPace.GpuSmoothing * (reading - previous)) : reading;

    private static double Sd(IReadOnlyCollection<double> values)
    {
        var mean = values.Average();
        return Math.Sqrt(values.Sum(x => (x - mean) * (x - mean)) / values.Count);
    }
}

public class GpuSamplerTests
{
    [Fact]
    public void Averages_snapshots_over_the_interval_instead_of_returning_one_moment()
    {
        var time = new ManualTimeProvider();
        var values = new Queue<double?>([0, 100, 100, 0, 0, 100, 100, 0, 0]);
        using var sampler = new GpuSampler(() => values.Count > 0 ? values.Dequeue() : 0, time);

        Assert.Equal(0, sampler.Read()); // first read: a snapshot, starts the timer
        for (var i = 0; i < 7; i++)
            time.Advance(GpuSampler.Period);
        Assert.Equal(50, sampler.Read()!.Value, 6); // 7 timer snapshots + this read's own: 4 × 100 of 8
    }

    [Fact]
    public void Stops_sampling_when_nobody_reads_and_restarts_on_the_next_read()
    {
        var time = new ManualTimeProvider();
        var calls = 0;
        using var sampler = new GpuSampler(() => { calls++; return 40; }, time);

        sampler.Read();
        for (var i = 0; i < 80; i++)
            time.Advance(GpuSampler.Period); // 20 s without a read
        var afterIdle = calls;
        for (var i = 0; i < 20; i++)
            time.Advance(GpuSampler.Period);
        Assert.Equal(afterIdle, calls); // timer stopped
        Assert.Equal(40, sampler.Read());
        time.Advance(GpuSampler.Period);
        Assert.True(calls > afterIdle + 1, "sampling resumed");
    }

    [Fact]
    public void Reports_nothing_without_a_GPU_and_never_starts_the_timer()
    {
        var time = new ManualTimeProvider();
        var calls = 0;
        using var sampler = new GpuSampler(() => { calls++; return null; }, time);

        Assert.Null(sampler.Read());
        for (var i = 0; i < 10; i++)
            time.Advance(GpuSampler.Period);
        Assert.Equal(1, calls);
    }
}
