namespace Valafind.Core.Indexing;

internal enum ChangeKind
{
    /// <summary>File or directory removed (or moved away).</summary>
    Delete = 0,
    /// <summary>File created, or a directory created/moved in (scanned recursively).</summary>
    Upsert = 1,
    /// <summary>Re-walk a whole directory tree and drop index entries that vanished (watcher overflow / error).</summary>
    Rescan = 2,
    /// <summary>Content/metadata of an existing entry changed. For a directory this is only "an entry inside it
    /// changed" (Windows raises it for every create/delete/rename below), which the entry's own event covers, so
    /// it never triggers a directory walk.</summary>
    Changed = 3,
}

internal readonly record struct DueChange(string Path, ChangeKind Kind);

/// <summary>Debounced, de-duplicated set of pending file-system changes. Each path becomes due
/// <c>debounce</c> after its most recent event; the last event kind for a path wins (except that a
/// <see cref="ChangeKind.Changed"/> never downgrades a pending <see cref="ChangeKind.Upsert"/>), and the latest
/// spelling of a path wins on case-insensitive comparers. Events below a pending rescan are dropped (the rescan
/// covers them). When more than <c>capacity</c> paths are pending, they collapse into rescans of their
/// <c>overflowTarget</c> directory (normally the watched root), so memory stays bounded however many events
/// arrive while the worker is paused. Due items are handed out deletes first, then upserts/changes, then rescans.
/// Thread-safe.</summary>
internal sealed class PendingChanges
{
    /// <summary>Default bound on individually tracked paths (a few MB).</summary>
    public const int DefaultCapacity = 50_000;

    private readonly TimeProvider _time;
    private readonly TimeSpan _debounce;
    private readonly Func<string, string?>? _overflowTarget;
    private readonly int _capacity;
    private readonly StringComparison _comparison;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (ChangeKind Kind, DateTimeOffset Due)> _paths;
    private readonly Dictionary<string, DateTimeOffset> _rescans;

    public PendingChanges(TimeProvider time, TimeSpan debounce, StringComparer comparer,
        Func<string, string?>? overflowTarget = null, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(comparer);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _time = time;
        _debounce = debounce;
        _overflowTarget = overflowTarget;
        _capacity = capacity;
        _comparison = comparer.Equals(StringComparer.Ordinal) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        _paths = new Dictionary<string, (ChangeKind, DateTimeOffset)>(comparer);
        _rescans = new Dictionary<string, DateTimeOffset>(comparer);
    }

    public int Count
    {
        get { lock (_lock) return _paths.Count + _rescans.Count; }
    }

    public void Add(string path, ChangeKind kind)
    {
        var due = _time.GetUtcNow() + _debounce;
        lock (_lock)
        {
            if (kind == ChangeKind.Rescan)
            {
                AddRescanLocked(path, due);
                return;
            }
            if (IsUnderPendingRescanLocked(path))
                return;

            if (_paths.Remove(path, out var existing) && kind == ChangeKind.Changed && existing.Kind == ChangeKind.Upsert)
                kind = ChangeKind.Upsert; // a created / moved-in directory must still be walked
            // Remove + add (rather than the indexer) so the newest spelling of the path is kept.
            _paths[path] = (kind, due);

            if (_paths.Count > _capacity)
                CollapseLocked(due);
        }
    }

    private void AddRescanLocked(string directory, DateTimeOffset due)
    {
        // An enclosing pending rescan already covers this one.
        if (IsUnderPendingRescanLocked(directory) && !_rescans.ContainsKey(directory))
            return;
        _rescans.Remove(directory);
        _rescans[directory] = due;

        List<string>? covered = null;
        foreach (var path in _paths.Keys)
        {
            if (IsUnder(path, directory))
                (covered ??= []).Add(path);
        }
        foreach (var other in _rescans.Keys)
        {
            if (!string.Equals(other, directory, _comparison) && IsUnder(other, directory))
                (covered ??= []).Add(other);
        }
        if (covered is null)
            return;
        foreach (var path in covered)
        {
            if (!_paths.Remove(path))
                _rescans.Remove(path);
        }
    }

    /// <summary>Too many individual paths: replace them with rescans of their (root) directories.</summary>
    private void CollapseLocked(DateTimeOffset due)
    {
        var targets = new HashSet<string>(_paths.Comparer);
        foreach (var path in _paths.Keys)
        {
            var target = _overflowTarget?.Invoke(path) ?? Path.GetDirectoryName(path) ?? path;
            targets.Add(target);
        }
        _paths.Clear();
        foreach (var target in targets)
            AddRescanLocked(target, due);
    }

    private bool IsUnderPendingRescanLocked(string path)
    {
        foreach (var directory in _rescans.Keys)
        {
            if (IsUnder(path, directory))
                return true;
        }
        return false;
    }

    private bool IsUnder(string path, string directory)
    {
        if (!path.StartsWith(directory, _comparison))
            return false;
        if (path.Length == directory.Length)
            return true;
        if (directory.Length > 0 && IsSeparator(directory[^1]))
            return true;
        return IsSeparator(path[directory.Length]);
    }

    private static bool IsSeparator(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;

    /// <summary>Removes and returns every change whose debounce period elapsed.</summary>
    public List<DueChange> TakeDue()
    {
        var now = _time.GetUtcNow();
        var result = new List<DueChange>();
        lock (_lock)
        {
            foreach (var (path, (kind, due)) in _paths)
            {
                if (due <= now)
                    result.Add(new DueChange(path, kind));
            }
            foreach (var (path, due) in _rescans)
            {
                if (due <= now)
                    result.Add(new DueChange(path, ChangeKind.Rescan));
            }
            foreach (var change in result)
            {
                if (change.Kind == ChangeKind.Rescan)
                    _rescans.Remove(change.Path);
                else
                    _paths.Remove(change.Path);
            }
        }
        result.Sort(static (a, b) => Rank(a.Kind) != Rank(b.Kind) ? Rank(a.Kind).CompareTo(Rank(b.Kind)) : string.CompareOrdinal(a.Path, b.Path));
        return result;
    }

    private static int Rank(ChangeKind kind) => kind switch
    {
        ChangeKind.Delete => 0,
        ChangeKind.Upsert or ChangeKind.Changed => 1,
        _ => 2,
    };

    /// <summary>Time until the next change becomes due, or null when nothing is pending.</summary>
    public TimeSpan? NextDueIn()
    {
        var now = _time.GetUtcNow();
        DateTimeOffset? next = null;
        lock (_lock)
        {
            foreach (var (_, due) in _paths.Values)
                next = next is null || due < next ? due : next;
            foreach (var due in _rescans.Values)
                next = next is null || due < next ? due : next;
        }
        if (next is null)
            return null;
        var delta = next.Value - now;
        return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
    }
}
