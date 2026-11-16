namespace Valafind.Core.Tests.Indexing;

/// <summary>Deterministic <see cref="TimeProvider"/>: time only moves on <see cref="Advance"/>, which fires due timers.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;
    private long _ticks;

    public ManualTimeProvider() : this(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
            return _now;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_lock)
            return _ticks;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_lock)
        {
            _now += by;
            _ticks += by.Ticks;
            due = _timers.Where(t => t.Due is { } d && d <= _now).ToList();
        }
        foreach (var timer in due)
            timer.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_lock)
            _timers.Add(timer);
        return timer;
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime;
            return true;
        }

        public void Fire()
        {
            Due = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : owner.GetUtcNow() + _period;
            callback(state);
        }

        public void Dispose()
        {
            Due = null;
            lock (owner._lock)
                owner._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
