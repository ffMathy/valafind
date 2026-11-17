using Valafind.Core.Abstractions;

namespace Valafind.Core.Indexing;

/// <summary>
/// The order in which files are indexed: documents first (text, office files, PDFs, mail, code, … and everything else
/// that is cheap to index), then pictures, then audio, then video. Media cost far more to embed and are searched for
/// less often, so the documents are always searchable first.
/// </summary>
public static class IndexOrder
{
    public const int Documents = 0;
    public const int Images = 1;
    public const int Audio = 2;
    public const int Videos = 3;

    public static int Rank(FileKind kind) => kind switch
    {
        FileKind.Image => Images,
        FileKind.Audio => Audio,
        FileKind.Video => Videos,
        _ => Documents,
    };

    public static int Rank(string path) => Rank(FileKinds.Classify(path));
}

/// <summary>Files waiting for their kind's turn (see <see cref="IndexOrder"/>), one queue per kind, without duplicates.
/// Used by the indexing worker only (single thread).</summary>
internal sealed class DeferredFiles(IEqualityComparer<string> comparer)
{
    private readonly LinkedList<(string Path, IndexPhase Phase)>[] _queues =
        [new(), new(), new(), new()];
    private readonly HashSet<string> _queued = new(comparer);

    public int Count => _queued.Count;

    public void Add(int rank, string path, IndexPhase phase)
    {
        if (_queued.Add(path))
            _queues[rank].AddLast((path, phase));
    }

    public bool TryTake(int rank, out string path, out IndexPhase phase)
    {
        var queue = _queues[rank];
        if (queue.First is { } first)
        {
            queue.RemoveFirst();
            _queued.Remove(first.Value.Path);
            (path, phase) = first.Value;
            return true;
        }

        path = string.Empty;
        phase = default;
        return false;
    }

    /// <summary>Moves the queued files of <paramref name="rank"/> that are in <paramref name="first"/> to the front,
    /// keeping their relative order.</summary>
    public void PromoteFirst(int rank, IReadOnlySet<string>? first)
    {
        if (first is null || first.Count == 0)
            return;
        var queue = _queues[rank];
        var promoted = new List<(string, IndexPhase)>();
        for (var node = queue.First; node is not null;)
        {
            var next = node.Next;
            if (first.Contains(node.Value.Path))
            {
                promoted.Add(node.Value);
                queue.Remove(node);
            }

            node = next;
        }

        for (var i = promoted.Count - 1; i >= 0; i--)
            queue.AddFirst(promoted[i]);
    }
}
