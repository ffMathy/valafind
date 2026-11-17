using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Valafind.Core.Abstractions;
using Valafind.Core.Configuration;
using Valafind.Core.Indexing;
using Xunit;

namespace Valafind.Core.Tests.Indexing;

/// <summary>Kind order (documents, pictures, audio, video) and content embedding of media files.</summary>
public class MediaIndexingTests
{
    private static readonly CoordinatorTuning Tuning = CoordinatorTuning.Default with
    {
        Debounce = TimeSpan.FromMilliseconds(100),
        IdlePoll = TimeSpan.FromMilliseconds(200),
        ModelBackoffUnit = TimeSpan.FromMilliseconds(1),
        Throttle = new ThrottleOptions(30, 25, TimeSpan.Zero, true, TimeSpan.FromMilliseconds(25)),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IndexingCoordinator Create(IFileDiscovery discovery, FakeStore store, IEmbeddingModel model, IMediaDecoder? decoder = null) =>
        new(discovery, new FakeReader(), new FakeChunker(), model, store, new FakeMonitor(), new ValafindSettings(),
            NullLogger<IndexingCoordinator>.Instance, null, Tuning, decoder);

    private static async Task WaitFor(Func<bool> condition, string what, int seconds = 60)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(seconds))
                Assert.Fail($"Timed out waiting for: {what}");
            await Task.Delay(20, Ct);
        }
    }

    [Theory]
    [InlineData("a.txt", IndexOrder.Documents)]
    [InlineData("a.pdf", IndexOrder.Documents)]
    [InlineData("a.zip", IndexOrder.Documents)]
    [InlineData("a.unknown", IndexOrder.Documents)]
    [InlineData("a.JPG", IndexOrder.Images)]
    [InlineData("a.mp3", IndexOrder.Audio)]
    [InlineData("a.mkv", IndexOrder.Videos)]
    public void Kinds_are_ranked(string name, int rank) => Assert.Equal(rank, IndexOrder.Rank(name));

    [Fact]
    public async Task Documents_then_pictures_then_audio_then_video_whatever_the_walk_order()
    {
        using var tree = new TempTree();
        var video = tree.File("v/clip.mp4", "x");
        var song = tree.File("m/song.mp3", "x");
        var photo = tree.File("p/photo.jpg", "x");
        var note = tree.File("n/note.txt", "hello");
        var photo2 = tree.File("p/photo2.png", "x");
        var frequentSong = tree.File("m/favourite.mp3", "x");
        var frequentNote = tree.File("n/recent.md", "recent");
        var memo = tree.File("memo.txt", "memo");
        var store = new FakeStore();
        var model = new FakeMediaModel();
        var discovery = new FakeDiscovery([tree.Root],
            frequent: [frequentSong, frequentNote],
            all: [video, song, photo, note, photo2, frequentSong, frequentNote, memo]);
        await using var coordinator = Create(discovery, store, model, new FakeMediaDecoder());
        await coordinator.StartAsync(Ct);
        await WaitFor(() => coordinator.Status.State == IndexingState.UpToDate, "up to date");

        var upserts = store.Snapshot().Where(l => l.Op == "upsert").Select(l => l.Path).ToList();
        // Kinds never mix; within a kind, files may finish in any order (they are indexed in parallel), though the
        // frequent song is started first among the audio files (see Deferred_files_keep_order_drop_duplicates_and_promote_frequent).
        Assert.Equal(8, upserts.Count);
        Assert.Equal(frequentNote, upserts[0]);
        Assert.Equal(new[] { note, memo }.Order(), upserts[1..3].Order());
        Assert.Equal(new[] { photo, photo2 }.Order(), upserts[3..5].Order());
        Assert.Equal(new[] { frequentSong, song }.Order(), upserts[5..7].Order());
        Assert.Equal(video, upserts[7]);
        // Phase 1 covered the frequent documents only.
        Assert.Equal(1, coordinator.Status.FrequentTotal);
        Assert.Equal(8, coordinator.Status.AllDone);
        Assert.Equal(8, coordinator.Status.AllTotal);
        Assert.Equal(IndexPhase.All, store.Get(frequentSong).Entry.Phase);

        // Content vectors next to the name vector: 1 for a picture, 1 per clip for audio, frames + clips for video.
        Assert.Equal(2, store.Get(photo).Chunks.Count);
        Assert.Equal(1 + 2, store.Get(song).Chunks.Count);
        Assert.Equal(1 + 1 + 1, store.Get(video).Chunks.Count);
        Assert.Single(store.Get(note).Chunks);
        var clip = store.Get(song).Chunks[2];
        Assert.Equal((30, 30, string.Empty), (clip.Start, clip.Length, clip.Text));
        Assert.Equal([FakeMediaModel.ImageVector], store.Get(photo).Chunks.Skip(1).Select(c => c.Vector));
        Assert.Equal(Enumerable.Range(0, 4).Select(i => (float)i).ToArray(), store.Get(photo).Chunks[1].Vector);

        // Media were embedded in kind order too, and the encoders were released once idle.
        Assert.Equal(["image", "image", "audio", "audio", "audio", "audio", "video", "audio"], model.Calls.ToArray());
        await WaitFor(() => model.Releases > 0, "media released when idle");
    }

    [Fact]
    public async Task Without_a_decoder_or_capability_media_are_indexed_by_name()
    {
        using var tree = new TempTree();
        var photo = tree.File("photo.jpg", "x");
        var song = tree.File("song.mp3", "x");
        var store = new FakeStore();
        var model = new FakeMediaModel { Capabilities = MediaCapabilities.Image };
        await using (var coordinator = Create(new FakeDiscovery([tree.Root], [], [photo, song]), store, model, new FakeMediaDecoder()))
        {
            await coordinator.StartAsync(Ct);
            await WaitFor(() => coordinator.Status.State == IndexingState.UpToDate, "up to date");
        }

        Assert.Equal(2, store.Get(photo).Chunks.Count);
        Assert.Single(store.Get(song).Chunks); // no audio encoder

        var plain = new FakeStore();
        await using (var coordinator = Create(new FakeDiscovery([tree.Root], [], [photo, song]), plain, new FakeMediaModel()))
        {
            await coordinator.StartAsync(Ct);
            await WaitFor(() => coordinator.Status.State == IndexingState.UpToDate, "up to date");
        }

        Assert.Single(plain.Get(photo).Chunks);
        Assert.Single(plain.Get(song).Chunks);
    }

    [Fact]
    public async Task Undecodable_or_failing_media_keep_their_name_vector()
    {
        using var tree = new TempTree();
        var broken = tree.File("broken.jpg", "x");
        var failing = tree.File("failing.png", "x");
        var store = new FakeStore();
        var model = new FakeMediaModel { FailImagesNamed = "failing.png" };
        var decoder = new FakeMediaDecoder { Undecodable = "broken.jpg" };
        await using var coordinator = Create(new FakeDiscovery([tree.Root], [], [broken, failing]), store, model, decoder);
        await coordinator.StartAsync(Ct);
        await WaitFor(() => coordinator.Status.State == IndexingState.UpToDate, "up to date");

        Assert.Single(store.Get(broken).Chunks);
        Assert.Single(store.Get(failing).Chunks);
    }

    [Fact]
    public async Task Media_found_by_the_watchers_wait_until_documents_are_done()
    {
        using var tree = new TempTree();
        var docs = Enumerable.Range(0, 30).Select(i => tree.File($"d{i}.txt", $"document {i}")).ToList();
        var store = new FakeStore();
        var model = new FakeMediaModel();
        var reader = new FakeReader { Delay = TimeSpan.FromMilliseconds(40) };
        await using var coordinator = new IndexingCoordinator(new FakeDiscovery([tree.Root], [], docs), reader, new FakeChunker(), model,
            store, new FakeMonitor(), new ValafindSettings(), NullLogger<IndexingCoordinator>.Instance, null, Tuning, new FakeMediaDecoder());
        await coordinator.StartAsync(Ct);
        await WaitFor(() => store.Snapshot().Count(l => l.Op == "upsert") >= 3, "indexing under way");

        var photo = tree.File("new.jpg", "x");
        coordinator.OnWatcherEvent(photo, ChangeKind.Upsert);
        await WaitFor(() => coordinator.Status.State == IndexingState.UpToDate && store.Contains(photo), "up to date");

        var upserts = store.Snapshot().Where(l => l.Op == "upsert").Select(l => l.Path).ToList();
        Assert.Equal(photo, upserts[^1]);
        Assert.Equal(31, upserts.Count);
    }

    [Fact]
    public void Deferred_files_keep_order_drop_duplicates_and_promote_frequent()
    {
        var deferred = new DeferredFiles(StringComparer.Ordinal);
        deferred.Add(IndexOrder.Audio, "a", IndexPhase.All);
        deferred.Add(IndexOrder.Audio, "b", IndexPhase.All);
        deferred.Add(IndexOrder.Audio, "c", IndexPhase.Incremental);
        deferred.Add(IndexOrder.Audio, "b", IndexPhase.Incremental);
        deferred.Add(IndexOrder.Images, "p", IndexPhase.All);
        Assert.Equal(4, deferred.Count);
        deferred.PromoteFirst(IndexOrder.Audio, new HashSet<string> { "c", "b" });

        var taken = new List<(string, IndexPhase)>();
        while (deferred.TryTake(IndexOrder.Audio, out var path, out var phase))
            taken.Add((path, phase));
        Assert.Equal([("b", IndexPhase.All), ("c", IndexPhase.Incremental), ("a", IndexPhase.All)], taken);
        Assert.True(deferred.TryTake(IndexOrder.Images, out var image, out _));
        Assert.Equal("p", image);
        Assert.Equal(0, deferred.Count);
    }
}

/// <summary>A media-capable fake model: content vectors are fixed per kind; calls are recorded in order.</summary>
internal sealed class FakeMediaModel : IEmbeddingModel, IMediaEmbeddingModel
{
    public static readonly float[] ImageVector = [0, 1, 2, 3];
    private readonly FakeModel _text = new("fake-media/4");

    public ConcurrentQueue<string> Calls { get; } = new();
    public int Releases;
    public MediaCapabilities Capabilities { get; set; } = MediaCapabilities.Image | MediaCapabilities.Audio | MediaCapabilities.Video;
    public string? FailImagesNamed { get; set; }

    public string ModelId => _text.ModelId;
    public int Dimensions => _text.Dimensions;
    public int MaxInputTokens => _text.MaxInputTokens;
    public int CountTokens(string text) => _text.CountTokens(text);
    public float[] EmbedQuery(string query, CancellationToken cancellationToken = default) => _text.EmbedQuery(query, cancellationToken);
    public IReadOnlyList<float[]> EmbedDocuments(IReadOnlyList<DocumentText> documents, CancellationToken cancellationToken = default) =>
        _text.EmbedDocuments(documents, cancellationToken);

    public MediaCapabilities MediaCapabilities => Capabilities;

    public float[] EmbedImage(DecodedImage image, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue("image");
        if (FailImagesNamed is not null && image.Width == FakeMediaDecoder.FailingWidth)
            throw new InvalidOperationException("encoder failure");
        return ImageVector;
    }

    public float[] EmbedAudio(AudioClip clip, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue("audio");
        return [2, 0, (float)clip.Offset.TotalSeconds, 0];
    }

    public float[] EmbedVideo(IReadOnlyList<DecodedImage> frames, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue("video");
        return [3, frames.Count, 0, 0];
    }

    public void ReleaseMediaResources() => Interlocked.Increment(ref Releases);

    public void Dispose() => _text.Dispose();
}

/// <summary>Decodes nothing for real: a 2×2 picture, two 30 s clips for audio (one for a video's sound), 3 frames.</summary>
internal sealed class FakeMediaDecoder : IMediaDecoder
{
    public const int FailingWidth = 3;

    public string? Undecodable { get; set; }

    public Task<DecodedImage?> DecodeImageAsync(string path, int maxSide, CancellationToken cancellationToken)
    {
        if (Path.GetFileName(path) == Undecodable)
            return Task.FromResult<DecodedImage?>(null);
        var width = Path.GetFileName(path) == "failing.png" ? FailingWidth : 2;
        return Task.FromResult<DecodedImage?>(new DecodedImage(width, 2, new byte[width * 2 * 3]));
    }

    public Task<IReadOnlyList<AudioClip>?> DecodeAudioAsync(string path, int maxClips, CancellationToken cancellationToken)
    {
        IReadOnlyList<AudioClip> clips = Enumerable.Range(0, Math.Min(2, maxClips))
            .Select(i => new AudioClip(new float[30 * AudioClip.SampleRate], TimeSpan.FromSeconds(30 * i))).ToList();
        if (Path.GetExtension(path) == ".mp4")
            clips = clips.Take(1).ToList();
        return Task.FromResult<IReadOnlyList<AudioClip>?>(clips);
    }

    public Task<VideoFrames?> DecodeVideoAsync(string path, int maxFrames, int maxSide, CancellationToken cancellationToken)
    {
        var frames = Enumerable.Range(0, 3).Select(_ => new DecodedImage(2, 2, new byte[12])).ToList();
        return Task.FromResult<VideoFrames?>(new VideoFrames(frames, [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], TimeSpan.FromSeconds(3)));
    }
}
