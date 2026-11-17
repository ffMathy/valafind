namespace Valafind.Core.Indexing;

/// <summary>
/// How hard background indexing works, steered by a <see cref="PidController"/> so whole-machine CPU settles just
/// under the user's limit: when the machine has room (say the limit is 30 % and it sits at 10 %), indexing speeds up;
/// when other programs need the CPU, it backs off before the <see cref="ThrottleGate"/> has to pause it.
/// </summary>
/// <remarks>
/// <para>The controller's output, <see cref="Effort"/>, is measured in cores' worth of indexing work. It is applied
/// with two actuators: <see cref="Concurrency"/> (files indexed at the same time, each running its own model batches)
/// is the effort rounded up, and <see cref="DutyCycle"/> covers the fractional rest: after every CPU-heavy step a
/// worker rests for <see cref="RestAfter"/> so that, on average, it works that fraction of the time. Effort 2.5 is
/// three workers at 83 %; effort 0.4 is one worker that rests 1.5 s after every second of work.</para>
/// <para>The controller compares whole-machine CPU (Valafind included) against <see cref="TargetPercent"/>. Its error is
/// converted from percent of the machine to units of effort (cores, divided by the learnt <see cref="CoresPerEffort"/>),
/// so the same gains work on 2 and on 32 cores, and whether a worker keeps one core or three busy. Readings are only fed
/// in while indexing actually runs; paused or idle periods would otherwise wind the controller up.</para>
/// <para>Thread-safe: <see cref="Update"/> is called by the gate's sampling loop, the getters by worker threads.</para>
/// </remarks>
public sealed class IndexingPace
{
    /// <summary>Lowest effort: one worker at 20 % duty. Never zero; pausing completely is the gate's job.</summary>
    public const double MinEffort = 0.2;

    /// <summary>Longest single rest, so a worker notices a faster pace soon after it is set.</summary>
    internal static readonly TimeSpan MaxRest = TimeSpan.FromSeconds(5);

    /// <summary>Default gains, for an error measured in units of effort: P reacts to the current gap, I removes what
    /// remains (about a third of it per 2-second sample: the level is found within ~20 s without overshooting), and D
    /// damps sudden swings in what other programs use.</summary>
    internal static PidGains DefaultGains(int maxConcurrency) =>
        new(Kp: 0.25, Ki: 0.15, Kd: 0.1, OutputMin: MinEffort, OutputMax: Math.Max(1, maxConcurrency), DerivativeSmoothing: 0.3);

    /// <summary>Bounds and smoothing of the <see cref="CoresPerEffort"/> estimate.</summary>
    private const double MinCoresPerEffort = 0.25, MaxCoresPerEffort = 4, CoresPerEffortSmoothing = 0.3;

    private readonly Lock _lock = new();
    private readonly PidController _pid;
    private readonly int _processorCount;
    private double _effort;
    private double _target;
    private int _inFlight;
    private int _peakInFlight;
    private double _coresPerEffort = 1;

    /// <summary>Creates a pace starting at <paramref name="initialEffort"/> cores.</summary>
    /// <param name="maxConcurrency">Most files indexed at once.</param>
    /// <param name="processorCount">Logical processors (converts percent of the machine to cores).</param>
    /// <param name="initialEffort">Effort before the first reading: one full worker, like a fixed-speed indexer.</param>
    /// <param name="gains">Controller gains; null = <see cref="DefaultGains"/>.</param>
    public IndexingPace(int maxConcurrency, int processorCount, double initialEffort = 1, PidGains? gains = null)
    {
        MaxConcurrency = Math.Max(1, maxConcurrency);
        _processorCount = Math.Max(1, processorCount);
        _pid = new PidController(gains ?? DefaultGains(MaxConcurrency), initialEffort);
        _effort = _pid.Output;
    }

    /// <summary>Most files indexed at once, for a machine with <paramref name="processorCount"/> logical processors:
    /// all of them up to 8 (each concurrent model run holds its own activations, so memory caps it).</summary>
    public static int DefaultMaxConcurrency(int processorCount) => Math.Clamp(processorCount, 1, 8);

    /// <summary>Most files indexed at once.</summary>
    public int MaxConcurrency { get; }

    /// <summary>Current output of the controller, in cores' worth of work (between <see cref="MinEffort"/> and
    /// <see cref="MaxConcurrency"/>).</summary>
    public double Effort
    {
        get { lock (_lock) return _effort; }
    }

    /// <summary>Whole-machine CPU (percent) the controller steers towards; 0 until the first reading.</summary>
    public double TargetPercent
    {
        get { lock (_lock) return _target; }
    }

    /// <summary>Cores of CPU one unit of <see cref="Effort"/> actually turns into, learnt from Valafind's own CPU use. One
    /// worker can use more than one core (ONNX Runtime's intra-op threads) or less (waiting for the disk). The error is
    /// divided by it, so the loop reacts the same on every machine instead of oscillating where a worker is "big".</summary>
    public double CoresPerEffort
    {
        get { lock (_lock) return _coresPerEffort; }
    }

    /// <summary>Files to index at the same time: <see cref="Effort"/> rounded up.</summary>
    public int Concurrency => ConcurrencyFor(Effort, MaxConcurrency);

    /// <summary>Fraction of the time each worker works (the rest of <see cref="Effort"/> after rounding up).</summary>
    public double DutyCycle => DutyCycleFor(Effort, MaxConcurrency);

    /// <summary>Raised (on the sampling thread) when <see cref="Effort"/> changed.</summary>
    public event EventHandler? Changed;

    internal static int ConcurrencyFor(double effort, int max) => Math.Clamp((int)Math.Ceiling(effort - 1e-6), 1, max);

    internal static double DutyCycleFor(double effort, int max) => Math.Clamp(effort / ConcurrencyFor(effort, max), MinEffort, 1);

    /// <summary>The setpoint for a CPU limit: a little under it, so the noise of real readings averages out below the
    /// limit instead of around it (27 % for a 30 % limit, 95 % for 100 %).</summary>
    public static double TargetFor(double maxCpuPercent)
    {
        var limit = Math.Clamp(maxCpuPercent, 1, 100);
        return limit - Math.Min(5, limit * 0.1);
    }

    /// <summary>Feeds one whole-machine CPU reading taken while indexing ran.</summary>
    /// <param name="maxCpuPercent">The user's limit.</param>
    /// <param name="machineCpuPercent">Whole-machine CPU over the last interval, Valafind included.</param>
    /// <param name="dt">Time since the previous reading.</param>
    /// <param name="ownCpuPercent">Valafind's own share of the machine over the same interval, when known; teaches
    /// <see cref="CoresPerEffort"/>.</param>
    public void Update(double maxCpuPercent, double machineCpuPercent, TimeSpan dt, double? ownCpuPercent = null)
    {
        bool changed;
        lock (_lock)
        {
            _target = TargetFor(maxCpuPercent);
            var toCores = _processorCount / 100.0;
            // Only speed up when the workers we already allow were all in use: with less work than slots (a walk over
            // unchanged files, one big file), more effort would not show in the reading and the controller would wind
            // up, then overshoot the moment real work arrives.
            var saturated = _peakInFlight >= ConcurrencyFor(_effort, MaxConcurrency);
            _peakInFlight = _inFlight;
            if (saturated && ownCpuPercent is { } own && dt > TimeSpan.Zero)
            {
                // _effort is what was applied during the interval this reading covers.
                var observed = Math.Clamp(own * toCores / Math.Max(_effort, MinEffort), MinCoresPerEffort, MaxCoresPerEffort);
                _coresPerEffort += CoresPerEffortSmoothing * (observed - _coresPerEffort);
            }
            var toEffort = toCores / _coresPerEffort;
            var output = _pid.Update(_target * toEffort, Math.Clamp(machineCpuPercent, 0, 100) * toEffort, dt, allowIncrease: saturated);
            changed = Math.Abs(output - _effort) > 1e-9;
            _effort = output;
        }
        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reports how many files are being indexed right now (called by the worker pool on every change).</summary>
    public void ReportInFlight(int count)
    {
        lock (_lock)
        {
            _inFlight = count;
            if (count > _peakInFlight)
                _peakInFlight = count;
        }
    }

    /// <summary>Indexing is not running (paused, idle or forced on): the next reading must not be read as a jump.</summary>
    public void Hold()
    {
        lock (_lock)
            _pid.ForgetMeasurement();
    }

    /// <summary>Raises the effort to at least <paramref name="effort"/> (used while the user forces indexing on).</summary>
    public void EnsureAtLeast(double effort)
    {
        bool changed;
        lock (_lock)
        {
            changed = _effort < effort;
            if (changed)
            {
                _pid.Reset(effort);
                _effort = _pid.Output;
            }
        }
        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>How long a worker rests after <paramref name="busy"/> of CPU-heavy work so that it works
    /// <see cref="DutyCycle"/> of the time: busy × (1 − d) / d, at most <see cref="MaxRest"/>.</summary>
    public TimeSpan RestAfter(TimeSpan busy) => RestFor(busy, DutyCycle);

    internal static TimeSpan RestFor(TimeSpan busy, double dutyCycle)
    {
        if (busy <= TimeSpan.Zero || dutyCycle >= 0.999)
            return TimeSpan.Zero;
        var rest = busy.TotalMilliseconds * (1 - dutyCycle) / Math.Max(dutyCycle, MinEffort);
        return TimeSpan.FromMilliseconds(Math.Min(rest, MaxRest.TotalMilliseconds));
    }
}
