namespace Valafind.Core.Platform;

/// <summary>
/// Averages a GPU reading that is only a snapshot (NVML and sysfs on Linux, IOKit on macOS) over the whole sampling
/// interval, by reading it every <see cref="Period"/> in between. The GPU runs model batches in bursts, so one snapshot
/// per 2-second interval lands on a busy or an idle moment more or less at random. Windows' PDH counters are already
/// averages and need none of this.
/// </summary>
/// <remarks>The timer only runs while someone reads regularly: it stops <see cref="IdleAfter"/> after the last
/// <see cref="Read"/>, so an idle indexer (sampled once a minute) causes no extra wake-ups; that read is a snapshot.</remarks>
internal sealed class GpuSampler : IDisposable
{
    /// <summary>Time between snapshots while active.</summary>
    internal static readonly TimeSpan Period = TimeSpan.FromMilliseconds(250);

    /// <summary>The timer stops when nobody has read for this long.</summary>
    internal static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(10);

    private readonly Func<double?> _snapshot;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly ITimer _timer;
    private double _sum;
    private int _count;
    private long _lastRead;
    private bool _running;
    private bool _disposed;

    /// <param name="snapshot">Reads the GPU's utilisation right now (null: unknown). Called under a lock, never
    /// concurrently.</param>
    /// <param name="time">Clock; defaults to <see cref="TimeProvider.System"/>.</param>
    public GpuSampler(Func<double?> snapshot, TimeProvider? time = null)
    {
        _snapshot = snapshot;
        _time = time ?? TimeProvider.System;
        _timer = _time.CreateTimer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The average of the snapshots since the previous call (this call's own snapshot included), or null
    /// when the GPU reports nothing.</summary>
    public double? Read()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = SafeSnapshot();
            if (now is { } value)
            {
                _sum += value;
                _count++;
            }
            double? average = _count > 0 ? _sum / _count : null;
            _sum = 0;
            _count = 0;
            _lastRead = _time.GetTimestamp();
            if (average is not null && !_running)
            {
                _running = true;
                _timer.Change(Period, Period);
            }
            return average;
        }
    }

    private void Tick()
    {
        lock (_lock)
        {
            if (_disposed || !_running)
                return;
            if (_time.GetElapsedTime(_lastRead) > IdleAfter)
            {
                _running = false;
                _sum = 0;
                _count = 0;
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return;
            }
            if (SafeSnapshot() is { } value)
            {
                _sum += value;
                _count++;
            }
        }
    }

    private double? SafeSnapshot()
    {
        try
        {
            return _snapshot() is { } v && double.IsFinite(v) ? Math.Clamp(v, 0, 100) : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _running = false;
        }
        _timer.Dispose();
    }
}
