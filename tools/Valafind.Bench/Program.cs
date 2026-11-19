using System.Diagnostics;
using System.Globalization;
using System.Text;
using Valafind.Core.Abstractions;
using Valafind.Core.Configuration;
using Valafind.Core.Embedding;
using Valafind.Core.Extraction;
using Valafind.Core.Extraction.Media;
using Valafind.Core.Indexing;

// Times the indexing pipeline per file, stage by stage, mirroring IndexingCoordinator.Index (without its pacing rests).
var opts = BenchOptions.Parse(args);
if (opts is null)
    return 1;

var modelDir = opts.ModelDirectory ?? ModelLocator.Find() ?? throw new InvalidOperationException("No model found.");
Console.WriteLine($"Model: {modelDir}");
Console.WriteLine($"Provider: {opts.Provider}, intra-op threads: {opts.Threads}, batch: {opts.Batch}, parallel files: {opts.Parallel}");

var load = Stopwatch.StartNew();
using var model = new EmbeddingGemmaModel(new EmbeddingOptions
{
    ModelDirectory = modelDir,
    IntraOpThreads = opts.Threads,
    BatchSize = opts.Batch,
    Device = Enum.Parse<EmbeddingDevice>(opts.Provider, ignoreCase: true),
});
model.EnsureLoaded();
model.EmbedQuery("warm up");
Console.WriteLine($"Load + warm-up: {load.ElapsedMilliseconds} ms on {model.ActiveDevice}{(model.GpuUnavailableReason is { } why ? $" ({why})" : "")}");

if (opts.Micro)
{
    Micro(model);
    return 0;
}

var files = opts.FileList is { } list
    ? File.ReadAllLines(list).Where(l => l.Length > 0 && File.Exists(l)).ToList()
    : Sample(opts);
Console.WriteLine($"{files.Count} files");

if (opts.DecodeOnly)
{
    var mediaDecoder = new MediaDecoder();
    foreach (var path in files)
    {
        var t = Stopwatch.StartNew();
        var frames = mediaDecoder.DecodeVideoAsync(path, 8, 1280, CancellationToken.None).GetAwaiter().GetResult();
        var framesMs = t.Elapsed.TotalMilliseconds;
        t.Restart();
        var clips = mediaDecoder.DecodeAudioAsync(path, 2, CancellationToken.None).GetAwaiter().GetResult();
        var first = frames is { Frames.Count: > 0 } ? frames.Frames[0] : null;
        Console.WriteLine($"frames {framesMs,7:F0} ms ({frames?.Frames.Count ?? 0}, {frames?.Duration.TotalSeconds ?? 0:F0} s long, {first?.Width}x{first?.Height})  audio {t.Elapsed.TotalMilliseconds,7:F0} ms ({clips?.Count ?? 0} clips)  {Path.GetFileName(path)}");
    }
    return 0;
}

var reader = new DocumentReader();
var decoder = new MediaDecoder();
var results = new List<FileResult>();
var gate = new object();
var wall = Stopwatch.StartNew();
Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = opts.Parallel }, path =>
{
    var r = Measure(path);
    lock (gate)
    {
        results.Add(r);
        Console.WriteLine(r.Line());
    }
});
wall.Stop();

Console.WriteLine();
Console.WriteLine($"Wall: {wall.Elapsed.TotalSeconds:F1} s for {results.Count} files ({results.Sum(r => r.Chunks)} text chunks, {results.Sum(r => r.MediaParts)} media parts)");
Console.WriteLine();
Console.WriteLine($"{"kind",-13}{"n",4}{"total p50",11}{"p95",9}{"max",9} | mean ms: {"read",8}{"chunk",8}{"(tok)",8}{"embed",8}{"decode",8}{"mediaEmb",9} | chunks/file");
foreach (var g in results.GroupBy(r => r.Kind).OrderBy(g => g.Key))
{
    var totals = g.Select(r => r.Total).Order().ToArray();
    Console.WriteLine($"{g.Key,-13}{g.Count(),4}{Pct(totals, 0.5),11:F0}{Pct(totals, 0.95),9:F0}{totals[^1],9:F0} | {"",9}{g.Average(r => r.Read),8:F0}{g.Average(r => r.Chunk),8:F0}{g.Average(r => r.ChunkTokenize),8:F0}{g.Average(r => r.Embed),8:F0}{g.Average(r => r.Decode),8:F0}{g.Average(r => r.MediaEmbed),9:F0} | {g.Average(r => r.Chunks),5:F1}");
}
var all = results.Select(r => r.Total).Order().ToArray();
if (all.Length > 0)
{
    Console.WriteLine($"{"ALL",-13}{all.Length,4}{Pct(all, 0.5),11:F0}{Pct(all, 0.95),9:F0}{all[^1],9:F0} | sum s: read {results.Sum(r => r.Read) / 1000:F1}, chunk {results.Sum(r => r.Chunk) / 1000:F1}, embed {results.Sum(r => r.Embed) / 1000:F1}, decode {results.Sum(r => r.Decode) / 1000:F1}, media embed {results.Sum(r => r.MediaEmbed) / 1000:F1}");
}
Console.WriteLine();
Console.WriteLine("Slowest files:");
foreach (var r in results.OrderByDescending(r => r.Total).Take(15))
    Console.WriteLine("  " + r.Line());

if (opts.CsvPath is { } csv)
{
    var sb = new StringBuilder("path,kind,bytes,chars,chunks,media,read_ms,chunk_ms,chunk_tok_ms,tok_calls,embed_ms,decode_ms,media_embed_ms,total_ms\n");
    foreach (var r in results)
        sb.AppendLine(CultureInfo.InvariantCulture, $"\"{r.Path}\",{r.Kind},{r.Bytes},{r.Chars},{r.Chunks},{r.MediaParts},{r.Read:F1},{r.Chunk:F1},{r.ChunkTokenize:F1},{r.TokenizeCalls},{r.Embed:F1},{r.Decode:F1},{r.MediaEmbed:F1},{r.Total:F1}");
    File.WriteAllText(csv, sb.ToString());
    Console.WriteLine($"CSV: {csv}");
}
return 0;

FileResult Measure(string path)
{
    var r = new FileResult { Path = path, Kind = FileKinds.Classify(path), Bytes = new FileInfo(path).Length };
    var total = Stopwatch.StartNew();
    var sw = Stopwatch.StartNew();
    ExtractedText? extracted = null;
    try
    {
        extracted = reader.ReadAsync(path, CancellationToken.None).GetAwaiter().GetResult();
    }
    catch (Exception e)
    {
        r.Error = "read: " + e.GetType().Name;
    }
    r.Read = sw.Elapsed.TotalMilliseconds;
    r.Chars = extracted?.Text.Length ?? 0;
    if (extracted is not null)
        r.Kind = extracted.Kind;

    // Chunk with a counting token counter so tokenisation inside the chunker shows up on its own.
    long tokTicks = 0;
    var calls = 0;
    var chunker = new TextChunker(new ChunkerOptions(), s =>
    {
        var t = Stopwatch.GetTimestamp();
        var n = model.CountTokens(s);
        tokTicks += Stopwatch.GetTimestamp() - t;
        calls++;
        return n;
    });
    IReadOnlyList<TextChunk> chunks = [];
    sw.Restart();
    if (extracted is not null && !string.IsNullOrWhiteSpace(extracted.Text))
        chunks = chunker.Chunk(extracted.Text);
    r.Chunk = sw.Elapsed.TotalMilliseconds;
    r.ChunkTokenize = Stopwatch.GetElapsedTime(0, tokTicks).TotalMilliseconds;
    r.TokenizeCalls = calls;

    var title = Path.GetFileName(path);
    List<DocumentText> docs = chunks.Count == 0
        ? [new DocumentText(title, title)]
        : chunks.Select(c => new DocumentText(title, c.Text)).ToList();
    r.Chunks = docs.Count;
    sw.Restart();
    for (var start = 0; start < docs.Count; start += opts.Batch)
        model.EmbedDocuments(docs.GetRange(start, Math.Min(opts.Batch, docs.Count - start)));
    r.Embed = sw.Elapsed.TotalMilliseconds;

    if (opts.Media && r.Kind is FileKind.Image or FileKind.Audio or FileKind.Video && (extracted is null || string.IsNullOrWhiteSpace(extracted.Text)))
    {
        try
        {
            switch (r.Kind)
            {
                case FileKind.Image:
                {
                    sw.Restart();
                    var image = decoder.DecodeImageAsync(path, 2048, CancellationToken.None).GetAwaiter().GetResult();
                    r.Decode = sw.Elapsed.TotalMilliseconds;
                    if (image is not null)
                    {
                        sw.Restart();
                        model.EmbedImage(image);
                        r.MediaEmbed = sw.Elapsed.TotalMilliseconds;
                        r.MediaParts++;
                    }
                    break;
                }
                case FileKind.Audio:
                    Clips(4);
                    break;
                case FileKind.Video:
                {
                    sw.Restart();
                    var frames = decoder.DecodeVideoAsync(path, 8, 1280, CancellationToken.None).GetAwaiter().GetResult();
                    r.Decode += sw.Elapsed.TotalMilliseconds;
                    if (frames is { Frames.Count: > 0 })
                    {
                        sw.Restart();
                        model.EmbedVideo(frames.Frames);
                        r.MediaEmbed += sw.Elapsed.TotalMilliseconds;
                        r.MediaParts++;
                    }
                    Clips(2);
                    break;
                }
            }
        }
        catch (Exception e)
        {
            r.Error = "media: " + e.GetType().Name + ": " + e.Message;
        }
    }
    r.Total = total.Elapsed.TotalMilliseconds;
    return r;

    void Clips(int max)
    {
        sw.Restart();
        var clips = decoder.DecodeAudioAsync(path, max, CancellationToken.None).GetAwaiter().GetResult();
        r.Decode += sw.Elapsed.TotalMilliseconds;
        foreach (var clip in clips ?? [])
        {
            sw.Restart();
            model.EmbedAudio(clip);
            r.MediaEmbed += sw.Elapsed.TotalMilliseconds;
            r.MediaParts++;
        }
    }
}

// Fixed workload (repo README chunks + the test media fixtures): ms per chunk / picture / clip, and, with --compare,
// how close the vectors are to those of a reference CPU model.
void Micro(EmbeddingGemmaModel model)
{
    var repo = File.Exists(Path.Combine(Environment.CurrentDirectory, "Valafind.slnx")) ? Environment.CurrentDirectory : AppContext.BaseDirectory;
    while (!File.Exists(Path.Combine(repo, "Valafind.slnx")))
        repo = Path.GetDirectoryName(repo) ?? throw new InvalidOperationException("Run from inside the repository.");
    var media = Path.Combine(repo, "tests", "Valafind.Core.Tests", "Fixtures", "Media");
    var text = File.ReadAllText(Path.Combine(repo, "README.md"));
    var chunks = new TextChunker(new ChunkerOptions { MaxChunks = opts.MicroChunks }, model.CountTokens).Chunk(text);
    var docs = chunks.Select(c => new DocumentText("README.md", c.Text)).ToList();
    Console.WriteLine($"Text: {docs.Count} chunks, mean {docs.Average(d => model.CountTokens(d.Text)):F0} tokens");

    using var reference = opts.CompareDirectory is { } dir
        ? new EmbeddingGemmaModel(new EmbeddingOptions { ModelDirectory = dir, IntraOpThreads = Math.Max(2, opts.Threads), BatchSize = opts.Batch, Device = EmbeddingDevice.Cpu })
        : null;

    List<float[]> Embed(EmbeddingGemmaModel m)
    {
        var vectors = new List<float[]>();
        for (var start = 0; start < docs.Count; start += opts.Batch)
            vectors.AddRange(m.EmbedDocuments(docs.GetRange(start, Math.Min(opts.Batch, docs.Count - start))));
        return vectors;
    }

    Embed(model); // warm-up (GPU providers compile shaders per shape)
    model.EmbedQuery("where is the invoice from march");
    var q = Stopwatch.StartNew();
    for (var r = 0; r < 5; r++)
        model.EmbedQuery("where is the invoice from march");
    Console.WriteLine($"  query: {q.Elapsed.TotalMilliseconds / 5,7:F1} ms (short input, 5 runs)");
    var sw = Stopwatch.StartNew();
    List<float[]> vectors = [];
    for (var r = 0; r < opts.Repeat; r++)
        vectors = Embed(model);
    Console.WriteLine($"  text: {sw.Elapsed.TotalMilliseconds / (opts.Repeat * docs.Count),7:F1} ms/chunk ({opts.Repeat} x {docs.Count} chunks, batch {opts.Batch})");
    if (reference is not null)
        Report("text", vectors, Embed(reference));

    if (!opts.Media)
        return;
    var decoder = new MediaDecoder();
    var image = decoder.DecodeImageAsync(Path.Combine(media, "invoice.png"), 2048, CancellationToken.None).GetAwaiter().GetResult()!;
    var clip = decoder.DecodeAudioAsync(Path.Combine(media, "speech.wav"), 1, CancellationToken.None).GetAwaiter().GetResult()![0];
    var frames = decoder.DecodeVideoAsync(Path.Combine(media, "clip.mp4"), 8, 1280, CancellationToken.None).GetAwaiter().GetResult();
    Time("image", m => m.EmbedImage(image));
    Time("audio", m => m.EmbedAudio(clip));
    if (frames is { Frames.Count: > 0 })
        Time($"video ({frames.Frames.Count} frames)", m => m.EmbedVideo(frames.Frames));

    void Time(string name, Func<EmbeddingGemmaModel, float[]> embed)
    {
        embed(model);
        var t = Stopwatch.StartNew();
        float[] v = [];
        for (var r = 0; r < opts.Repeat; r++)
            v = embed(model);
        Console.WriteLine($"  {name}: {t.Elapsed.TotalMilliseconds / opts.Repeat,7:F0} ms each");
        if (reference is not null)
            Report(name, [v], [embed(reference)]);
    }

    static void Report(string name, List<float[]> a, List<float[]> b)
    {
        var cos = a.Zip(b, (x, y) => x.Zip(y, (p, q) => (double)p * q).Sum()).ToArray();
        Console.WriteLine($"    {name} cosine vs reference: min {cos.Min():F5}, mean {cos.Average():F5}");
    }
}

static List<string> Sample(BenchOptions opts)
{
    var discovery = new UserFileDiscovery(new ValafindSettings());
    var byKind = new Dictionary<FileKind, List<(string Path, long Size)>>();
    var walk = Stopwatch.StartNew();
    var n = 0;
    foreach (var path in discovery.EnumerateAllFiles(CancellationToken.None))
    {
        if (++n > opts.MaxWalk || walk.Elapsed > TimeSpan.FromSeconds(90))
            break;
        var kind = FileKinds.Classify(path);
        if (kind == FileKind.Other)
            continue;
        if (opts.Kinds.Count > 0 && !opts.Kinds.Contains(kind))
            continue;
        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (IOException)
        {
            continue;
        }
        if (size == 0)
            continue;
        if (!byKind.TryGetValue(kind, out var l))
            byKind[kind] = l = [];
        l.Add((path, size));
    }
    Console.WriteLine($"Walked {n} files in {walk.Elapsed.TotalSeconds:F1} s: " +
        string.Join(", ", byKind.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value.Count}")));

    var random = new Random(opts.Seed);
    var picked = new List<string>();
    foreach (var (_, candidates) in byKind.OrderBy(k => k.Key))
    {
        var largest = candidates.OrderByDescending(c => c.Size).Take(opts.Largest).Select(c => c.Path).ToList();
        var rest = candidates.Select(c => c.Path).Except(largest).OrderBy(_ => random.Next()).Take(opts.PerKind);
        picked.AddRange(largest);
        picked.AddRange(rest);
    }
    return picked;
}

static double Pct(double[] sorted, double p) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(p * sorted.Length))];

sealed class FileResult
{
    public required string Path;
    public FileKind Kind;
    public long Bytes;
    public int Chars, Chunks, MediaParts, TokenizeCalls;
    public double Read, Chunk, ChunkTokenize, Embed, Decode, MediaEmbed, Total;
    public string? Error;

    public string Line() =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Total,8:F0} ms  {Kind,-12} read {Read,6:F0}  chunk {Chunk,6:F0} (tok {ChunkTokenize,6:F0}, {TokenizeCalls,4} calls)  embed {Embed,6:F0} ({Chunks,2} chunks)  decode {Decode,6:F0}  media {MediaEmbed,6:F0} ({MediaParts})  {Bytes / 1024,8} KB  {Chars,6} ch  {System.IO.Path.GetFileName(Path)}{(Error is null ? "" : "  !" + Error)}");
}

sealed class BenchOptions
{
    public string? ModelDirectory;
    public string? FileList;
    public string? CsvPath;
    public int PerKind = 6;
    public int Largest = 2;
    public int Seed = 42;
    public int Threads = 2;
    public int Batch = 2;
    public int Parallel = 1;
    public int MaxWalk = 400_000;
    public bool Media = true;
    public string Provider = "cpu";
    public HashSet<FileKind> Kinds = [];
    public bool Micro;
    public bool DecodeOnly;
    public int MicroChunks = 16;
    public int Repeat = 2;
    public string? CompareDirectory;

    public static BenchOptions? Parse(string[] args)
    {
        var o = new BenchOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => args[++i];
            switch (args[i])
            {
                case "--model": o.ModelDirectory = Next(); break;
                case "--files": o.FileList = Next(); break;
                case "--csv": o.CsvPath = Next(); break;
                case "--per-kind": o.PerKind = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--largest": o.Largest = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--seed": o.Seed = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--threads": o.Threads = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--batch": o.Batch = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--parallel": o.Parallel = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--max-walk": o.MaxWalk = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--provider": o.Provider = Next(); break;
                case "--kinds": o.Kinds = Next().Split(',').Select(k => Enum.Parse<FileKind>(k, true)).ToHashSet(); break;
                case "--no-media": o.Media = false; break;
                case "--micro": o.Micro = true; break;
                case "--decode-only": o.DecodeOnly = true; break;
                case "--micro-chunks": o.MicroChunks = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--repeat": o.Repeat = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--compare": o.CompareDirectory = Next(); break;
                default:
                    Console.WriteLine("""
                        Valafind.Bench [options]
                          --model DIR        model folder (default: the installed one)
                          --files LIST       newline-separated file paths instead of sampling the home folder
                          --kinds K1,K2      only these FileKinds when sampling (Pdf,Text,Image,...)
                          --per-kind N       random files per kind (6)        --largest N   largest files per kind (2)
                          --threads N        ORT intra-op threads (2)         --batch N     chunks per model run (2)
                          --parallel N       files at once (1)                --provider    cpu | gpu | auto
                          --no-media         skip picture/audio/video content --csv PATH    per-file CSV
                          --decode-only      only time video frame and audio decoding of the files
                          --micro            fixed workload (README chunks + test media) instead of real files
                          --micro-chunks N   README chunks (16)               --repeat N    timed passes (2)
                          --compare DIR      with --micro: cosine of every vector vs a CPU model from DIR
                        """);
                    return null;
            }
        }
        return o;
    }
}
