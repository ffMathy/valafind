namespace Valafind.Core.Indexing;

/// <summary>
/// A file-system change log that survives restarts (macOS FSEvents). Lets the start-up check ask "what changed under
/// these roots since the checkpoint?" instead of walking every root.
/// </summary>
internal interface IChangeHistory
{
    /// <summary>A checkpoint naming the present moment for <paramref name="roots"/>, to store and pass to
    /// <see cref="TryReplay"/> later; null when some root has no usable history (e.g. a network volume).</summary>
    string? Capture(IReadOnlyList<string> roots);

    /// <summary>Reports every change under <paramref name="roots"/> recorded after <paramref name="checkpoint"/>, on
    /// the calling thread or another one. A file is reported as <see cref="ChangeKind.Changed"/> and a directory as
    /// <see cref="ChangeKind.Upsert"/> (whether it was created, modified or removed: the handler checks what is there
    /// now); <see cref="ChangeKind.Rescan"/> means the log lost detail below that directory.</summary>
    /// <returns>False when the history cannot answer (another volume, the log was reset, the roots changed, a
    /// timeout): the caller walks the roots instead. Changes reported before a false return are still valid.</returns>
    bool TryReplay(string checkpoint, IReadOnlyList<string> roots, Action<string, ChangeKind> onChange, CancellationToken cancellationToken);
}
