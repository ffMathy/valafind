using Valafind.Core.Abstractions;
using Valafind.Core.Platform;

namespace Valafind.Core.Indexing;

/// <summary>Thresholds for <see cref="ThrottleGate"/>.</summary>
/// <param name="MaxCpuPercent">Indexing pauses when CPU used by other processes exceeds this.</param>
/// <param name="MaxDiskBusyPercent">Indexing pauses when disk busy time (minus Valafind's estimated share) exceeds this.</param>
/// <param name="CalmFor">Pressure must stay below the thresholds this long before the gate (re)opens.</param>
/// <param name="PauseOnBattery">Close immediately when the machine runs on battery.</param>
/// <param name="SampleInterval">How often the gate samples system pressure. Null = 2 seconds.</param>
/// <param name="IdleSampleInterval">Sampling interval while the indexer is idle and nobody waits on the gate.
/// Null = 60 seconds.</param>
public sealed record ThrottleOptions(
    double MaxCpuPercent,
    double MaxDiskBusyPercent,
    TimeSpan CalmFor,
    bool PauseOnBattery,
    TimeSpan? SampleInterval = null,
    TimeSpan? IdleSampleInterval = null)
{
    /// <summary>Default sampling interval.</summary>
    public static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromSeconds(2);

    /// <summary>Default sampling interval while idle.</summary>
    public static readonly TimeSpan DefaultIdleSampleInterval = TimeSpan.FromSeconds(60);

    /// <summary>The effective idle sampling interval (never shorter than <see cref="EffectiveSampleInterval"/>).</summary>
    public TimeSpan EffectiveIdleSampleInterval =>
        IdleSampleInterval is { } s && s > TimeSpan.Zero
            ? (s > EffectiveSampleInterval ? s : EffectiveSampleInterval)
            : (DefaultIdleSampleInterval > EffectiveSampleInterval ? DefaultIdleSampleInterval : EffectiveSampleInterval);

    /// <summary>The effective sampling interval.</summary>
    public TimeSpan EffectiveSampleInterval =>
        SampleInterval is { } s && s > TimeSpan.Zero ? s : DefaultSampleInterval;

    /// <summary>Builds options from user settings.</summary>
    public static ThrottleOptions From(Configuration.ValafindSettings settings) => new(
        settings.MaxCpuPercent, settings.MaxDiskBusyPercent,
        TimeSpan.FromSeconds(Math.Max(0, settings.CalmSeconds)), settings.PauseOnBattery);
}

/// <summary>
/// Decides when background indexing may run. Samples <see cref="ISystemPressureMonitor"/> on its own loop,
/// subtracts Valafind's own CPU and I/O, and applies hysteresis: the gate opens only after pressure stayed below
/// the thresholds for <see cref="ThrottleOptions.CalmFor"/> and closes after two consecutive samples above them
/// (immediately when switching to battery with <see cref="ThrottleOptions.PauseOnBattery"/>).
/// A user pause always wins; <see cref="OverrideUntil"/> forces the gate open otherwise.
/// While the owner reports it is idle (<see cref="SetIdle"/>) and nobody waits on the gate, sampling slows to
/// <see cref="ThrottleOptions.EffectiveIdleSampleInterval"/> to save wake-ups; new work or a waiter wakes the loop
/// for an immediate fresh sample.
/// </summary>
public sealed class ThrottleGate : IDisposable
{
    private const int BusySamplesToClose = 2;
    private const int FailuresBeforeAssumingCalm = 5;

    private readonly ISystemPressureMonitor _monitor;
    private readonly Func<ThrottleOptions> _options;
    private readonly TimeProvider _time;
    private readonly IOwnUsageProbe _ownUsage;
    private readonly int _processorCount;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cts = new();

    private TaskCompletionSource _openSignal = NewSignal();
    private TaskCompletionSource _wakeSignal = NewSignal();
    private bool _idle;
    private int _waiters;
    private Task? _loop;
    private IndexingState _state = IndexingState.PausedSystemBusy;
    private bool _userPaused;
    private DateTimeOffset? _overrideUntil;
    private bool _systemOpen;
    private bool _onBattery;
    private int _consecutiveBusy;
    private int _consecutiveFailures;
    private DateTimeOffset? _calmSince;
    private OwnUsage? _previousOwn;
    private long _previousOwnTimestamp;
    private PressureSample? _lastSample;
    private PressureSample? _lastEffectiveSample;
    private bool _disposed;

    /// <summary>Creates a gate. Call <see cref="StartAsync"/> to begin sampling.</summary>
    /// <param name="monitor">System pressure source (not owned; not disposed by the gate).</param>
    /// <param name="options">Read on every sample so settings changes apply live.</param>
    /// <param name="time">Clock; defaults to <see cref="TimeProvider.System"/>.</param>
    public ThrottleGate(ISystemPressureMonitor monitor, Func<ThrottleOptions> options, TimeProvider? time = null)
        : this(monitor, options, time, new ProcessUsageProbe(), Environment.ProcessorCount)
    {
    }

    internal ThrottleGate(ISystemPressureMonitor monitor, Func<ThrottleOptions> options, TimeProvider? time,
        IOwnUsageProbe ownUsage, int processorCount)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(options);
        _monitor = monitor;
        _options = options;
        _time = time ?? TimeProvider.System;
        _ownUsage = ownUsage;
        _processorCount = Math.Max(1, processorCount);
    }

    /// <summary>Current gate state: <see cref="IndexingState.Running"/>, <see cref="IndexingState.PausedSystemBusy"/>,
    /// <see cref="IndexingState.PausedOnBattery"/> or <see cref="IndexingState.PausedByUser"/>.</summary>
    public IndexingState State
    {
        get { lock (_lock) return _state; }
    }

    /// <summary>True when work may proceed.</summary>
    public bool IsOpen => State == IndexingState.Running;

    /// <summary>The most recent raw machine-wide sample.</summary>
    public PressureSample? LastSample
    {
        get { lock (_lock) return _lastSample; }
    }

    /// <summary>The most recent sample with Valafind's own CPU / I/O removed (what the thresholds are compared to).</summary>
    public PressureSample? LastEffectiveSample
    {
        get { lock (_lock) return _lastEffectiveSample; }
    }

    /// <summary>When set in the future, system pressure and battery are ignored until then (user pause still applies).</summary>
    public DateTimeOffset? OverrideUntil
    {
        get { lock (_lock) return _overrideUntil; }
        set
        {
            lock (_lock)
                _overrideUntil = value;
            Reevaluate();
        }
    }

    /// <summary>Whether the user paused indexing.</summary>
    public bool UserPaused
    {
        get { lock (_lock) return _userPaused; }
    }

    /// <summary>Raised (on a background thread) after every sample and whenever <see cref="State"/> changes.</summary>
    public event EventHandler<IndexingState>? Changed;

    /// <summary>Pauses or resumes on user request. Takes effect immediately.</summary>
    public void SetUserPaused(bool paused)
    {
        lock (_lock)
            _userPaused = paused;
        Reevaluate();
    }

    /// <summary>Starts the background sampling loop (idempotent). The first sample is taken right away.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loop is not null)
                return Task.CompletedTask;
            var token = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken).Token;
            _loop = Task.Run(() => LoopAsync(token), CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    /// <summary>Completes when the gate is open (immediately if it already is). A waiter keeps sampling at the
    /// normal rate even while the owner is idle.</summary>
    public Task WaitUntilOpenAsync(CancellationToken cancellationToken)
    {
        Task signal;
        lock (_lock)
        {
            if (_state == IndexingState.Running)
                return Task.CompletedTask;
            signal = _openSignal.Task;
            if (_idle && _waiters == 0)
                WakeLocked(); // the loop sleeps at the idle rate: sample now
            _waiters++;
        }
        return WaitAsync(signal, cancellationToken);
    }

    private async Task WaitAsync(Task signal, CancellationToken cancellationToken)
    {
        try
        {
            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
                _waiters--;
        }
    }

    /// <summary>Tells the gate whether its owner has work. While idle (and nobody waits), sampling slows down;
    /// switching to busy wakes the loop for an immediate sample.</summary>
    public void SetIdle(bool idle)
    {
        lock (_lock)
        {
            if (_idle == idle)
                return;
            _idle = idle;
            if (!idle)
                WakeLocked();
        }
    }

    /// <summary>True while samples are taken at the slow idle rate.</summary>
    internal bool IsDormant
    {
        get { lock (_lock) return _idle && _waiters == 0; }
    }

    private void WakeLocked() => _wakeSignal.TrySetResult();

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Task wake;
            lock (_lock)
            {
                // Replaced before sampling: a wake request arriving during Tick() is not lost.
                if (_wakeSignal.Task.IsCompleted)
                    _wakeSignal = NewSignal();
                wake = _wakeSignal.Task;
            }
            Tick();
            var options = _options();
            var interval = IsDormant ? options.EffectiveIdleSampleInterval : options.EffectiveSampleInterval;
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                var delay = Task.Delay(interval, _time, delayCts.Token);
                await Task.WhenAny(delay, wake).ConfigureAwait(false);
                await delayCts.CancelAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Takes one sample and updates the state machine. Exposed for tests.</summary>
    internal void Tick()
    {
        PressureSample? sample;
        try
        {
            sample = _monitor.Sample();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            sample = null;
        }
        OwnUsage? own = null;
        try
        {
            own = _ownUsage.Read();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            own = null;
        }
        Evaluate(sample, own, _time.GetTimestamp());
    }

    internal void Evaluate(PressureSample? sample, OwnUsage? own, long timestamp)
    {
        var options = _options();
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (_disposed)
                return;
            EvaluateLocked(sample, own, timestamp, options, now);
            UpdateStateLocked(options, now);
        }
        RaiseChanged();
    }

    private void EvaluateLocked(PressureSample? sample, OwnUsage? own, long timestamp, ThrottleOptions options, DateTimeOffset now)
    {
        PressureSample effective;
        if (sample is null)
        {
            if (++_consecutiveFailures < FailuresBeforeAssumingCalm)
                return;
            // A monitor that keeps failing must not block indexing forever.
            effective = new PressureSample(now, 0, null, null, false);
        }
        else
        {
            _consecutiveFailures = 0;
            _lastSample = sample;
            effective = sample;
            if (own is { } o && _previousOwn is { } prev)
            {
                var wall = _time.GetElapsedTime(_previousOwnTimestamp, timestamp);
                var ownCpu = PressureMath.OwnCpuPercent(o.CpuTime - prev.CpuTime, wall, _processorCount);
                long? ownBytes = o.IoBytes is { } a && prev.IoBytes is { } b ? Math.Max(0, a - b) : null;
                effective = PressureMath.SubtractOwn(sample, ownCpu, ownBytes, wall);
            }
            if (own is not null)
            {
                _previousOwn = own;
                _previousOwnTimestamp = timestamp;
            }
        }

        _lastEffectiveSample = effective;
        _onBattery = effective.OnBattery;
        if (PressureMath.IsBusy(effective, options))
        {
            _consecutiveBusy++;
            _calmSince = null;
        }
        else
        {
            _consecutiveBusy = 0;
            _calmSince ??= now;
        }

        if (_systemOpen)
        {
            if (_consecutiveBusy >= BusySamplesToClose)
                _systemOpen = false;
        }
        else if (_calmSince is { } since && now - since >= options.CalmFor)
        {
            _systemOpen = true;
        }
    }

    private void Reevaluate()
    {
        var options = _options();
        var now = _time.GetUtcNow();
        bool changed;
        lock (_lock)
        {
            if (_disposed)
                return;
            changed = UpdateStateLocked(options, now);
        }
        if (changed)
            RaiseChanged();
    }

    /// <returns>True when the state changed.</returns>
    private bool UpdateStateLocked(ThrottleOptions options, DateTimeOffset now)
    {
        IndexingState next;
        if (_userPaused)
            next = IndexingState.PausedByUser;
        else if (_overrideUntil is { } until && until > now)
            next = IndexingState.Running;
        else if (_onBattery && options.PauseOnBattery)
            next = IndexingState.PausedOnBattery;
        else
            next = _systemOpen ? IndexingState.Running : IndexingState.PausedSystemBusy;

        if (next == _state)
            return false;
        var wasOpen = _state == IndexingState.Running;
        _state = next;
        if (next == IndexingState.Running)
            _openSignal.TrySetResult();
        else if (wasOpen)
            _openSignal = NewSignal();
        return true;
    }

    private void RaiseChanged()
    {
        IndexingState state;
        lock (_lock)
            state = _state;
        try
        {
            Changed?.Invoke(this, state);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Subscriber bugs must not kill the sampling loop.
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Stops sampling. Pending <see cref="WaitUntilOpenAsync"/> calls are cancelled.</summary>
    public void Dispose()
    {
        Task? loop;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            loop = _loop;
            _openSignal.TrySetCanceled();
        }
        _cts.Cancel();
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
        _cts.Dispose();
    }
}

/// <summary>Pure arithmetic behind <see cref="ThrottleGate"/>.</summary>
internal static class PressureMath
{
    /// <summary>Own CPU share of the whole machine in percent: Δcpu / (Δwall × processors) × 100.</summary>
    public static double OwnCpuPercent(TimeSpan cpuDelta, TimeSpan wall, int processorCount)
    {
        if (wall <= TimeSpan.Zero || cpuDelta <= TimeSpan.Zero)
            return 0;
        return Math.Clamp(cpuDelta.TotalMilliseconds / (wall.TotalMilliseconds * Math.Max(1, processorCount)) * 100.0, 0, 100);
    }

    /// <summary>Removes Valafind's own share. Disk busy time cannot be attributed per process, so it is scaled by
    /// the share of throughput that was not ours (only when both throughputs are known and meaningful).</summary>
    public static PressureSample SubtractOwn(PressureSample sample, double ownCpuPercent, long? ownIoBytes, TimeSpan wall)
    {
        var cpu = Math.Max(0, sample.CpuPercent - ownCpuPercent);
        var bytes = sample.DiskBytesPerSecond;
        var busy = sample.DiskBusyPercent;
        if (ownIoBytes is { } own && wall > TimeSpan.Zero && bytes is { } total)
        {
            var ownRate = own / wall.TotalSeconds;
            var others = Math.Max(0, total - ownRate);
            if (busy is { } b && total > 64 * 1024)
                busy = b * Math.Clamp(others / total, 0, 1);
            bytes = others;
        }
        return sample with { CpuPercent = cpu, DiskBusyPercent = busy, DiskBytesPerSecond = bytes };
    }

    public static bool IsBusy(PressureSample effective, ThrottleOptions options) =>
        effective.CpuPercent > options.MaxCpuPercent ||
        (effective.DiskBusyPercent is { } disk && disk > options.MaxDiskBusyPercent);
}
