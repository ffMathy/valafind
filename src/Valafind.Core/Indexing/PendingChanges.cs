namespace Valafind.Core.Indexing;

internal enum ChangeKind
{
    /// <summary>File or directory removed (or moved away).</summary>
    Delete = 0,
    /// <summary>File created/changed, or a directory created/moved in (scanned recursively).</summary>
    Upsert = 1,
    /// <summary>Re-walk a whole directory tree (watcher overflow / error).</summary>
    Rescan = 2,
}

internal readonly record struct DueChange(string Path, ChangeKind Kind);

/// <summary>Debounced, de-duplicated set of pending file-system changes. Each path becomes due
/// <c>debounce</c> after its most recent event; the last event kind for a path wins.
/// Due items are handed out deletes first, then upserts, then rescans. Thread-safe.</summary>
internal sealed class PendingChanges(TimeProvider time, TimeSpan debounce, StringComparer comparer)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (ChangeKind Kind, DateTimeOffset Due)> _paths = new(comparer);
    private readonly Dictionary<string, DateTimeOffset> _rescans = new(comparer);

    public int Count
    {
        get { lock (_lock) return _paths.Count + _rescans.Count; }
    }

    public void Add(string path, ChangeKind kind)
    {
        var due = time.GetUtcNow() + debounce;
        lock (_lock)
        {
            if (kind == ChangeKind.Rescan)
                _rescans[path] = due;
            else
                _paths[path] = (kind, due);
        }
    }

    /// <summary>Removes and returns every change whose debounce period elapsed.</summary>
    public List<DueChange> TakeDue()
    {
        var now = time.GetUtcNow();
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
        result.Sort(static (a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : string.CompareOrdinal(a.Path, b.Path));
        return result;
    }

    /// <summary>Time until the next change becomes due, or null when nothing is pending.</summary>
    public TimeSpan? NextDueIn()
    {
        var now = time.GetUtcNow();
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
