namespace Valafind.App.Services;

/// <summary>
/// Notices when the embedding model becomes available (or its files change) while the app runs without it, so the
/// user never has to restart Valafind after running the fetch script. Polls a cheap probe that returns a signature
/// of the installed model (folder + file sizes/times), or null when none is installed.
/// </summary>
/// <remarks>
/// <para>Model missing: any non-null signature triggers. Model failed to load: only a signature that differs from
/// the one that failed triggers, so a broken install is not reloaded every few seconds.</para>
/// <para>The callback runs on the polling thread (a timer thread, or the caller of <see cref="Poll"/>) at most once per
/// <see cref="Start"/>; the watcher stops itself before invoking it.</para>
/// </remarks>
internal sealed class ModelInstallWatcher : IDisposable
{
    /// <summary>Default polling interval.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly Func<string?> _probe;
    private readonly Action _onChanged;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private ITimer? _timer;
    private string? _baseline;
    private bool _active;
    private int _polling;

    public ModelInstallWatcher(Func<string?> probe, Action onChanged, TimeSpan? interval = null, TimeProvider? time = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _interval = interval ?? DefaultInterval;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>True while watching.</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate)
                return _active;
        }
    }

    /// <summary>Starts watching. <paramref name="baseline"/> is the signature that must NOT trigger (null when the
    /// model is missing; the failed model's signature when it could not be loaded).</summary>
    public void Start(string? baseline)
    {
        lock (_gate)
        {
            _baseline = baseline;
            _active = true;
            _timer ??= _time.CreateTimer(static s => ((ModelInstallWatcher)s!).Poll(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(_interval, _interval);
        }
    }

    /// <summary>Stops watching (idempotent).</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _active = false;
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Probes once now (also used by the timer, "Check again" and when the launcher is shown).
    /// Returns true when the change callback was invoked.</summary>
    public bool Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1)
            return false;
        try
        {
            string? baseline;
            lock (_gate)
            {
                if (!_active)
                    return false;
                baseline = _baseline;
            }

            string? current;
            try
            {
                current = _probe();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
            if (current is null || string.Equals(current, baseline, StringComparison.Ordinal))
                return false;

            lock (_gate)
            {
                if (!_active)
                    return false;
                _active = false;
                _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
            _onChanged();
            return true;
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _active = false;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
