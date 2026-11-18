using Microsoft.ML.OnnxRuntime;
using Valafind.Core.Abstractions;
using Valafind.Core.Embedding.Media;

namespace Valafind.Core.Embedding;

/// <summary>
/// <see cref="IEmbeddingModel"/> and <see cref="IMediaEmbeddingModel"/> backed by Google's EmbeddingGemma 2 (ONNX export
/// by onnx-community) running on ONNX Runtime, on the GPU through the WebGPU provider when <see cref="EmbeddingOptions.Device"/>
/// allows it and one is usable, otherwise on the CPU: text, pictures, audio and video in one vector space.
/// Entirely offline: the tokenizer and models are read from <see cref="EmbeddingOptions.ModelDirectory"/> and nothing is
/// ever downloaded.
/// </summary>
/// <remarks>
/// <para>The tokenizer and inference session are loaded on first use (or by <see cref="EnsureLoaded"/>). Load
/// failures are not cached, so a later call retries.</para>
/// <para>Thread-safe: tokenisation runs concurrently, and so do document batches from different threads (the indexer
/// runs as many at once as its CPU budget allows). Queries have priority and run alone: <see cref="EmbedQuery"/>
/// never waits behind a document batch: when a query arrives while <see cref="EmbedDocuments"/> calls are running
/// batches, those ORT runs are terminated (<see cref="RunOptions.Terminate"/>), the query runs, and the interrupted
/// batches are re-run transparently (the document calls just take longer; their cancellation tokens are still
/// honoured). Document batches also wait while any query is queued.
/// <see cref="EmbedDocumentsInteractive"/> (the search "explain" pass) gets the same priority as a query.</para>
/// <para>ORT's intra-op worker threads are created by native code when the session is built; they are not managed
/// threads, so their OS priority cannot be changed from C# (BackgroundPriority on the calling thread does not reach
/// them). To keep them cheap when idle, spin-waiting is disabled (<c>session.intra_op.allow_spinning = 0</c>,
/// likewise inter-op), so they block instead of burning CPU between runs.</para>
/// <para>Queries are embedded as <c>"task: search result | query: {query}"</c> and documents as
/// <c>"title: {title|none} | text: {text}"</c>. The model's 768-d output is truncated to
/// <see cref="Dimensions"/> (Matryoshka) and re-normalised.</para>
/// <para>Media go through a separate encoder first (<c>vision_encoder</c> for pictures and video frames,
/// <c>audio_encoder</c> for audio), whose soft tokens replace placeholder tokens in the text model's input, exactly as the
/// reference processor lays them out: a picture is <c>&lt;bos&gt; &lt;|image&gt; &lt;|image|&gt;×n &lt;image|&gt; &lt;eos&gt;</c>,
/// a video one such block per frame with <c>&lt;|video|&gt;</c> placeholders, audio
/// <c>&lt;|audio&gt; &lt;|audio|&gt;×n &lt;audio|&gt;</c>; media get no task prompt. The encoders are loaded on first use and
/// freed by <see cref="ReleaseMediaResources"/>. Every encoder run is scheduled like a document batch (queries preempt
/// it); a video runs the encoder once per frame so a query never waits long.</para>
/// </remarks>
public sealed class EmbeddingGemmaModel : IEmbeddingModel, IInteractiveEmbedding, IMediaEmbeddingModel, IGpuAccelerated
{
    /// <summary>Model family part of <see cref="ModelId"/>.</summary>
    public const string ModelName = "embeddinggemma-2";

    internal const int MinTokens = 64;

    /// <summary>Document titles longer than this many tokens are shortened.</summary>
    internal const int TitleTokenBudget = 32;

    /// <summary>Tokens reserved outside the caller's text: BOS + EOS, the document prompt framing
    /// ("title: " / " | text: ", 5 tokens), the title budget, and slack for merges across the prompt boundary.</summary>
    internal const int ReservedPromptTokens = 2 + 6 + TitleTokenBudget + 4;

    internal const string InputIdsName = "input_ids";
    internal const string AttentionMaskName = "attention_mask";
    internal const string ImageFeaturesName = "image_features";
    internal const string VideoFeaturesName = "video_features";
    internal const string AudioFeaturesName = "audio_features";
    internal const string SentenceEmbeddingName = "sentence_embedding";
    internal const string PixelValuesName = "pixel_values";
    internal const string PixelPositionIdsName = "pixel_position_ids";
    internal const string InputFeaturesName = "input_features";
    internal const string InputFeaturesMaskName = "input_features_mask";

    // Special tokens of the EmbeddingGemma 2 vocabulary (tokenizer_config.json / config.json).
    internal const int BeginImageId = 255999;
    internal const int BeginAudioId = 256000;
    internal const int ImageTokenId = 258880;
    internal const int AudioTokenId = 258881;
    internal const int EndImageId = 258882;
    internal const int EndAudioId = 258883;
    internal const int VideoTokenId = 258884;

    internal const string IntraOpSpinningKey = "session.intra_op.allow_spinning";
    internal const string InterOpSpinningKey = "session.inter_op.allow_spinning";

    private readonly string _modelDirectory;
    private readonly string _modelPath;
    private readonly string? _visionPath;
    private readonly string? _audioPath;
    private readonly int _maxTokens;
    private readonly int _intraOpThreads;
    private readonly EmbeddingDevice _device;
    private readonly string? _gpuCrashMarker;
    private readonly int _batchSize;
    private readonly int _imageSoftTokens;
    private readonly int _videoFrameSoftTokens;
    private readonly int _maxVideoFrames;

    private readonly Lock _loadLock = new();
    private readonly RunScheduler _scheduler = new();
    private int _preemptedRuns;
    private volatile ITextTokenizer? _tokenizer;
    private volatile InferenceSession? _session;
    private readonly Dictionary<InferenceSession, SessionOptions> _sessionOptions = []; // per session, under _loadLock
    private OrtEpDevice? _gpu; // the device the text model runs on (null: CPU), written under _loadLock
    private InferenceSession? _visionSession; // read with Volatile.Read, written under _loadLock
    private InferenceSession? _audioSession;
    private int _outputWidth;
    private int _featureWidth = 512;
    private HashSet<string> _textInputs = [];
    private volatile bool _disposed;

    /// <summary>Validates <paramref name="options"/> and resolves the model files. Does not load the model yet.</summary>
    /// <exception cref="ArgumentException">An option is out of range.</exception>
    /// <exception cref="NotSupportedException">An fp16 model variant was configured.</exception>
    /// <exception cref="FileNotFoundException">The tokenizer or ONNX model is missing.</exception>
    public EmbeddingGemmaModel(EmbeddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _modelDirectory = Path.GetFullPath(options.ModelDirectory);
        _maxTokens = options.MaxTokens;
        _intraOpThreads = options.IntraOpThreads;
        _device = options.Device;
        _gpuCrashMarker = string.IsNullOrWhiteSpace(options.GpuCrashMarker) ? null : Path.GetFullPath(options.GpuCrashMarker);
        _batchSize = options.BatchSize;
        _imageSoftTokens = options.ImageSoftTokens;
        _videoFrameSoftTokens = options.VideoFrameSoftTokens;
        _maxVideoFrames = options.MaxVideoFrames;
        Dimensions = options.Dimensions;

        var modelFile = options.ModelFile.Replace('\\', '/');
        if (ModelLocator.IsFp16(modelFile))
            throw new NotSupportedException($"'{modelFile}' is an fp16 model; EmbeddingGemma does not support fp16 activations. Use model_quantized.onnx or model.onnx.");
        if (!ModelLocator.IsUsableModelFile(_modelDirectory, modelFile))
        {
            if (modelFile == EmbeddingOptions.DefaultModelFile && ModelLocator.IsValid(_modelDirectory, out var alternative))
                modelFile = alternative;
            else
                throw new FileNotFoundException($"ONNX model '{modelFile}' (and its .onnx_data weights, if external) not found in '{_modelDirectory}'.",
                    Path.Combine(_modelDirectory, modelFile));
        }
        _modelPath = Path.Combine(_modelDirectory, modelFile.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(Path.Combine(_modelDirectory, "tokenizer.json")) && !File.Exists(Path.Combine(_modelDirectory, "tokenizer.model")))
            throw new FileNotFoundException($"Neither tokenizer.json nor tokenizer.model found in '{_modelDirectory}'.",
                Path.Combine(_modelDirectory, "tokenizer.json"));

        ModelId = $"{ModelName}/{Path.GetFileNameWithoutExtension(modelFile)}/{Dimensions}";

        if (options.EnableMedia)
        {
            if (ModelLocator.FindVisionEncoder(_modelDirectory) is { } vision)
                _visionPath = Path.Combine(_modelDirectory, vision.Replace('/', Path.DirectorySeparatorChar));
            if (ModelLocator.FindAudioEncoder(_modelDirectory) is { } audio)
                _audioPath = Path.Combine(_modelDirectory, audio.Replace('/', Path.DirectorySeparatorChar));
        }
    }

    /// <summary>E.g. <c>"embeddinggemma-2/model_quantized/256"</c>.</summary>
    public string ModelId { get; }

    /// <inheritdoc />
    public int Dimensions { get; }

    /// <summary>Largest <see cref="CountTokens"/> value a document chunk may have without being truncated: the
    /// configured <see cref="EmbeddingOptions.MaxTokens"/> minus room for BOS/EOS, the document prompt and the title.</summary>
    public int MaxInputTokens => _maxTokens - ReservedPromptTokens;

    /// <summary>Full path of the ONNX file in use.</summary>
    internal string ModelPath => _modelPath;

    /// <inheritdoc />
    public MediaCapabilities MediaCapabilities =>
        (_visionPath is null ? MediaCapabilities.None : MediaCapabilities.Image | MediaCapabilities.Video)
        | (_audioPath is null ? MediaCapabilities.None : MediaCapabilities.Audio);

    /// <summary>Largest frame count <see cref="EmbedVideo"/> uses (more frames are thinned out evenly).</summary>
    public int MaxVideoFrames => _maxVideoFrames;

    /// <summary>The tokenizer (loaded on demand). Exposed for the chunker.</summary>
    internal ITextTokenizer Tokenizer => GetTokenizer();

    /// <summary>Loads the tokenizer and the ONNX Runtime session now instead of on first use.</summary>
    /// <exception cref="InvalidDataException">The tokenizer or model is malformed or has unexpected inputs/outputs.</exception>
    /// <exception cref="OnnxRuntimeException">ONNX Runtime failed to load the model.</exception>
    public void EnsureLoaded()
    {
        GetTokenizer();
        GetSession();
    }

    /// <summary>Token count of <paramref name="text"/> as-is (no prompt, no BOS/EOS).</summary>
    public int CountTokens(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return string.IsNullOrEmpty(text) ? 0 : GetTokenizer().CountTokens(text);
    }

    /// <inheritdoc />
    public float[] EmbedQuery(string query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var input = BuildInput(EmbeddingPrompts.Query(query));
        return RunBatch([input], cancellationToken, interactive: true)[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<float[]> EmbedDocuments(IReadOnlyList<DocumentText> documents, CancellationToken cancellationToken = default) =>
        EmbedDocumentsCore(documents, interactive: false, cancellationToken);

    /// <inheritdoc />
    public IReadOnlyList<float[]> EmbedDocumentsInteractive(IReadOnlyList<DocumentText> documents, CancellationToken cancellationToken = default) =>
        EmbedDocumentsCore(documents, interactive: true, cancellationToken);

    private float[][] EmbedDocumentsCore(IReadOnlyList<DocumentText> documents, bool interactive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (documents.Count == 0)
            return [];

        var inputs = new int[documents.Count][];
        for (var i = 0; i < documents.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var doc = documents[i] ?? throw new ArgumentException($"documents[{i}] is null.", nameof(documents));
            inputs[i] = BuildInput(BuildDocumentPrompt(doc.Title, doc.Text ?? string.Empty));
        }

        // Sort by length so each batch pads as little as possible; restore the caller's order afterwards.
        var order = Enumerable.Range(0, inputs.Length).OrderBy(i => inputs[i].Length).ToArray();
        var results = new float[inputs.Length][];
        var batch = new List<int[]>(_batchSize);
        for (var start = 0; start < order.Length; start += _batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            batch.Clear();
            var end = Math.Min(order.Length, start + _batchSize);
            for (var k = start; k < end; k++)
                batch.Add(inputs[order[k]]);
            var vectors = RunBatch(batch, cancellationToken, interactive);
            for (var k = start; k < end; k++)
                results[order[k]] = vectors[k - start];
        }
        return results;
    }

    /// <summary>
    /// Length (UTF-16 chars) of the longest prefix of <paramref name="text"/> whose tokens are among its first
    /// <paramref name="maxTokens"/> tokens. Never splits a surrogate pair. Re-tokenising the prefix on its own can
    /// differ by a token or so at the cut (BPE is not prefix-stable), so callers should keep a small margin.
    /// </summary>
    internal int GetPrefixLengthWithinTokens(string text, int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(maxTokens);
        if (text.Length == 0)
            return 0;
        var ids = new List<int>();
        var offsets = new List<int>();
        GetTokenizer().Encode(text, ids, offsets);
        return ids.Count <= maxTokens ? text.Length : offsets[maxTokens];
    }

    /// <summary>Builds the document prompt, shortening over-long titles to <see cref="TitleTokenBudget"/> tokens.</summary>
    internal string BuildDocumentPrompt(string? title, string text)
    {
        var normalizedTitle = EmbeddingPrompts.NormalizeTitle(title);
        if (normalizedTitle.Length > TitleTokenBudget) // a token covers at least one char, so shorter titles fit
        {
            var cut = GetPrefixLengthWithinTokens(normalizedTitle, TitleTokenBudget);
            if (cut < normalizedTitle.Length)
                normalizedTitle = normalizedTitle[..cut].TrimEnd();
            if (normalizedTitle.Length == 0)
                normalizedTitle = EmbeddingPrompts.NoTitle;
        }
        return EmbeddingPrompts.Document(normalizedTitle, text);
    }

    /// <summary>Tokenises an already-prompted input and truncates it to MaxTokens, keeping BOS and EOS.</summary>
    internal int[] BuildInput(string promptedText)
    {
        var tokenizer = GetTokenizer();
        var budget = _maxTokens - 2;

        // Bound the work for huge inputs: a token never covers more than MaxTokenLength chars, so the first
        // `budget` tokens lie within budget*MaxTokenLength chars; the extra margin keeps the cut far from them.
        var capChars = (long)(budget + 64) * tokenizer.MaxTokenLength;
        if (promptedText.Length > capChars)
        {
            var cap = (int)capChars;
            if (char.IsHighSurrogate(promptedText[cap - 1]))
                cap--;
            promptedText = promptedText[..cap];
        }

        var ids = new List<int>(Math.Min(promptedText.Length + 2, _maxTokens + 64));
        tokenizer.Encode(promptedText, ids);
        var count = Math.Min(ids.Count, budget);
        var result = new int[count + 2];
        result[0] = tokenizer.BosId;
        ids.CopyTo(0, result, 1, count);
        result[^1] = tokenizer.EosId;
        return result;
    }

    /// <summary>Runs one padded batch and returns Matryoshka-truncated, normalised vectors in input order.</summary>
    /// <param name="sequences">Token sequences (BOS/EOS included).</param>
    /// <param name="cancellationToken">Aborts the ORT run (<see cref="RunOptions.Terminate"/>) and throws.</param>
    /// <param name="interactive">True for a query: it preempts a running document batch and runs before any
    /// waiting one. False for a document batch: it yields to queries and is transparently re-run when preempted.</param>
    internal float[][] RunBatch(IReadOnlyList<int[]> sequences, CancellationToken cancellationToken, bool interactive = false) =>
        RunTextModel(sequences, null, interactive, cancellationToken);

    /// <summary>Runs the text model on padded <paramref name="sequences"/>; <paramref name="media"/> fills the
    /// placeholder tokens (null: text only).</summary>
    private float[][] RunTextModel(IReadOnlyList<int[]> sequences, MediaFeatures? media, bool interactive, CancellationToken cancellationToken) =>
        RunTextModelOn(GetSession(), sequences, media, body => Scheduled(body, interactive, cancellationToken));

    /// <summary>Builds the inputs for <paramref name="sequences"/> and hands one ORT run on <paramref name="session"/> to
    /// <paramref name="run"/> (the scheduler, or a direct call while the session is being verified).</summary>
    private float[][] RunTextModelOn(InferenceSession session, IReadOnlyList<int[]> sequences, MediaFeatures? media,
        Func<Func<RunOptions, float[][]>, float[][]> run)
    {
        var padId = GetTokenizer().PadId;
        var batch = sequences.Count;
        var seqLen = sequences.Max(s => s.Length);

        var inputIds = new long[batch * seqLen];
        var mask = new long[batch * seqLen];
        if (padId != 0)
            Array.Fill(inputIds, padId);
        for (var b = 0; b < batch; b++)
        {
            var seq = sequences[b];
            var row = b * seqLen;
            for (var i = 0; i < seq.Length; i++)
            {
                inputIds[row + i] = seq[i];
                mask[row + i] = 1;
            }
        }

        long[] shape = [batch, seqLen];
        var names = new List<string>(5) { InputIdsName, AttentionMaskName };
        var values = new List<OrtValue>(5);
        try
        {
            values.Add(OrtValue.CreateTensorValueFromMemory(inputIds, shape));
            values.Add(OrtValue.CreateTensorValueFromMemory(mask, shape));
            foreach (var (name, kind) in new[] { (ImageFeaturesName, MediaKind.Image), (VideoFeaturesName, MediaKind.Video), (AudioFeaturesName, MediaKind.Audio) })
            {
                if (!_textInputs.Contains(name))
                    continue;
                var features = media is not null && media.Kind == kind ? media.Values : [];
                names.Add(name);
                values.Add(OrtValue.CreateTensorValueFromMemory(features, [features.Length / _featureWidth, _featureWidth]));
            }

            return run(runOptions =>
            {
                var results = new float[batch][];
                using var outputs = session.Run(runOptions, names, values, [SentenceEmbeddingName]);
                var output = outputs[0];
                var outShape = output.GetTensorTypeAndShape().Shape;
                if (outShape.Length != 2 || outShape[0] != batch || outShape[1] < Dimensions)
                    throw new InvalidDataException($"Unexpected {SentenceEmbeddingName} shape [{string.Join(", ", outShape)}].");
                var width = (int)outShape[1];
                var data = output.GetTensorDataAsSpan<float>();
                for (var b = 0; b < batch; b++)
                    results[b] = Matryoshka.TruncateAndNormalize(data.Slice(b * width, width), Dimensions);
                return results;
            });
        }
        finally
        {
            foreach (var value in values)
                value.Dispose();
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> (one ORT run) under the scheduler: interactive runs preempt background ones;
    /// a preempted background run is re-run once the query is done.
    /// </summary>
    private T Scheduled<T>(Func<RunOptions, T> body, bool interactive, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var run = new ActiveRun(new RunOptions());
            try
            {
                using var registration = cancellationToken.Register(static state => ((RunOptions)state!).Terminate = true, run.Options);
                if (interactive)
                    _scheduler.EnterInteractive(cancellationToken);
                else
                    _scheduler.EnterBackground(run, cancellationToken);
                try
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    return body(run.Options);
                }
                catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Embedding was cancelled.", cancellationToken);
                }
                catch (OnnxRuntimeException) when (run.Preempted)
                {
                    // A query terminated this background run; re-run it once the query is done.
                    Interlocked.Increment(ref _preemptedRuns);
                }
                finally
                {
                    _scheduler.Exit(run);
                }
            }
            finally
            {
                run.Options.Dispose();
            }
        }
    }

    // ------------------------------------------------------------------ media

    private enum MediaKind
    {
        Image,
        Video,
        Audio,
    }

    /// <summary>Soft tokens from an encoder: [rows × feature width], row-major.</summary>
    private sealed record MediaFeatures(MediaKind Kind, float[] Values, int Rows);

    /// <inheritdoc />
    public float[] EmbedImage(DecodedImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var vision = GetVisionSession();
        var patches = VisionPreprocessor.Preprocess(image, _imageSoftTokens);
        cancellationToken.ThrowIfCancellationRequested();
        var features = RunVision(vision, patches, cancellationToken);
        var rows = features.Length / _featureWidth;
        var ids = new int[rows + 4];
        ids[0] = GetTokenizer().BosId;
        ids[1] = BeginImageId;
        Array.Fill(ids, ImageTokenId, 2, rows);
        ids[^2] = EndImageId;
        ids[^1] = GetTokenizer().EosId;
        return RunTextModel([ids], new MediaFeatures(MediaKind.Image, features, rows), interactive: false, cancellationToken)[0];
    }

    /// <inheritdoc />
    public float[] EmbedVideo(IReadOnlyList<DecodedImage> frames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frames.Count == 0)
            throw new ArgumentException("A video needs at least one frame.", nameof(frames));
        var vision = GetVisionSession();
        var selected = SelectFrames(frames.Count, _maxVideoFrames).Select(i => frames[i]).ToList();

        // One encoder run per frame: a query waits for at most one frame, and memory stays small.
        var perFrame = new List<float[]>(selected.Count);
        foreach (var frame in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            perFrame.Add(RunVision(vision, VisionPreprocessor.Preprocess(frame, _videoFrameSoftTokens), cancellationToken));
        }

        var total = perFrame.Sum(f => f.Length);
        var values = new float[total];
        var ids = new List<int>(total / _featureWidth + 2 * perFrame.Count + 2) { GetTokenizer().BosId };
        var offset = 0;
        foreach (var f in perFrame)
        {
            f.CopyTo(values, offset);
            offset += f.Length;
            ids.Add(BeginImageId);
            for (var i = 0; i < f.Length / _featureWidth; i++)
                ids.Add(VideoTokenId);
            ids.Add(EndImageId);
        }
        ids.Add(GetTokenizer().EosId);
        return RunTextModel([ids.ToArray()], new MediaFeatures(MediaKind.Video, values, total / _featureWidth), interactive: false, cancellationToken)[0];
    }

    /// <inheritdoc />
    public float[] EmbedAudio(AudioClip clip, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var audio = GetAudioSession();
        var input = AudioFeatureExtractor.Extract(clip.Samples);
        if (input.Frames == 0 || !input.Mask.Any(m => m))
            throw new ArgumentException("The audio clip is too short to embed.", nameof(clip));
        cancellationToken.ThrowIfCancellationRequested();

        var features = Scheduled(runOptions => RunAudioUnscheduled(audio, input, runOptions), interactive: false, cancellationToken);

        var rows = features.Length / _featureWidth;
        if (rows == 0)
            throw new ArgumentException("The audio clip is too short to embed.", nameof(clip));
        var ids = new int[rows + 4];
        ids[0] = GetTokenizer().BosId;
        ids[1] = BeginAudioId;
        Array.Fill(ids, AudioTokenId, 2, rows);
        ids[^2] = EndAudioId;
        ids[^1] = GetTokenizer().EosId;
        return RunTextModel([ids], new MediaFeatures(MediaKind.Audio, features, rows), interactive: false, cancellationToken)[0];
    }

    /// <summary>
    /// Indices of the frames to use: all of them when they fit, otherwise <paramref name="max"/> spread evenly (the
    /// reference processor's "uniform" overflow strategy: <c>np.linspace(0, n - 1, max, dtype=int)</c>).
    /// </summary>
    internal static int[] SelectFrames(int count, int max)
    {
        if (count <= max)
            return Enumerable.Range(0, count).ToArray();
        var result = new int[max];
        for (var i = 0; i < max; i++)
            result[i] = max == 1 ? 0 : (int)((double)(count - 1) * i / (max - 1));
        return result;
    }

    private float[] RunAudioUnscheduled(InferenceSession audio, AudioFeatures input, RunOptions? runOptions = null)
    {
        using var featuresValue = OrtValue.CreateTensorValueFromMemory(input.Features, [1, input.Frames, AudioFeatureExtractor.MelBins]);
        using var maskValue = OrtValue.CreateTensorValueFromMemory(input.Mask, [1, input.Frames]);
        using var defaultOptions = runOptions is null ? new RunOptions() : null;
        using var outputs = audio.Run(runOptions ?? defaultOptions!, [InputFeaturesName, InputFeaturesMaskName], [featuresValue, maskValue], [audio.OutputNames[0]]);
        return ReadFeatures(outputs[0]);
    }

    private float[] RunVision(InferenceSession vision, VisionPatches patches, CancellationToken cancellationToken) =>
        Scheduled(runOptions => RunVisionUnscheduled(vision, patches, runOptions), interactive: false, cancellationToken);

    private float[] RunVisionUnscheduled(InferenceSession vision, VisionPatches patches, RunOptions? runOptions = null)
    {
        using var pixels = OrtValue.CreateTensorValueFromMemory(patches.PixelValues, [1, patches.Patches, VisionPreprocessor.PatchPixels]);
        using var positions = OrtValue.CreateTensorValueFromMemory(patches.PositionIds, [1, patches.Patches, 2]);
        using var defaultOptions = runOptions is null ? new RunOptions() : null;
        using var outputs = vision.Run(runOptions ?? defaultOptions!, [PixelValuesName, PixelPositionIdsName], [pixels, positions], [vision.OutputNames[0]]);
        return ReadFeatures(outputs[0]);
    }

    /// <summary>
    /// Runs a first inference on a freshly created GPU session. Some GPU kernels do not fail with an error but abort the
    /// whole process, which nothing in-process can catch; the marker file (<see cref="EmbeddingOptions.GpuCrashMarker"/>)
    /// is present while this runs, so after such a crash the next start finds it and stays on the CPU.
    /// </summary>
    private void VerifyOnGpu(Action firstRun)
    {
        var marker = _gpuCrashMarker;
        try
        {
            if (marker is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                File.WriteAllText(marker, GpuMarkerStamp);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            marker = null; // no guard this time; the check itself still runs
        }
        try
        {
            firstRun();
        }
        finally
        {
            // Reached after success and after an ordinary (catchable) failure alike; only a process abort skips it.
            if (marker is not null)
            {
                try
                {
                    File.Delete(marker);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>What a crash marker records: a newer Valafind or GPU provider tries the GPU again.</summary>
    internal static string GpuMarkerStamp =>
        $"{typeof(EmbeddingGemmaModel).Assembly.GetName().Version} webgpu {typeof(Microsoft.ML.OnnxRuntime.EP.WebGpu.WebGpuEp).Assembly.GetName().Version}";

    /// <summary>True when a previous process died during <see cref="VerifyOnGpu"/> with this same build.</summary>
    private bool GpuCrashedBefore()
    {
        try
        {
            return _gpuCrashMarker is { } marker && File.Exists(marker) && File.ReadAllText(marker).Trim() == GpuMarkerStamp;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private float[] ReadFeatures(OrtValue output)
    {
        var shape = output.GetTensorTypeAndShape().Shape;
        if (shape.Length != 2 || shape[1] != _featureWidth)
            throw new InvalidDataException($"Unexpected encoder output shape [{string.Join(", ", shape)}].");
        return output.GetTensorDataAsSpan<float>().ToArray();
    }

    /// <inheritdoc />
    public void ReleaseMediaResources()
    {
        lock (_loadLock)
        {
            if (_disposed || (_visionSession is null && _audioSession is null))
                return;
            _scheduler.EnterExclusive();
            try
            {
                CloseSession(ref _visionSession);
                CloseSession(ref _audioSession);
            }
            finally
            {
                _scheduler.Exit(null);
            }
        }
    }

    /// <summary>Disposes a media session and its options. Called under <see cref="_loadLock"/>.</summary>
    private void CloseSession(ref InferenceSession? field)
    {
        if (field is not { } session)
            return;
        session.Dispose();
        if (_sessionOptions.Remove(session, out var options))
            options.Dispose();
        Volatile.Write(ref field, null);
    }

    /// <summary>True while an encoder is loaded (tests).</summary>
    internal bool HasMediaSessions => Volatile.Read(ref _visionSession) is not null || Volatile.Read(ref _audioSession) is not null;

    private InferenceSession GetVisionSession() =>
        GetMediaSession(ref _visionSession, _visionPath ?? throw new NotSupportedException("The vision encoder is not installed."),
            [PixelValuesName, PixelPositionIdsName]);

    private InferenceSession GetAudioSession() =>
        GetMediaSession(ref _audioSession, _audioPath ?? throw new NotSupportedException("The audio encoder is not installed."),
            [InputFeaturesName, InputFeaturesMaskName]);

    private InferenceSession GetMediaSession(ref InferenceSession? field, string path, string[] inputs)
    {
        GetSession(); // the text model fixes the feature width the encoders must produce
        var session = Volatile.Read(ref field);
        if (session is not null)
            return session;
        lock (_loadLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (field is not null)
                return field;
            InferenceSession? created = null;
            if (_gpu is { } gpu)
            {
                try
                {
                    created = OpenValidatedMediaSession(path, inputs, gpu);
                    var opened = created;
                    if (path == _visionPath)
                        VerifyOnGpu(() => RunVisionUnscheduled(opened, VisionPreprocessor.Preprocess(new DecodedImage(64, 64, new byte[64 * 64 * 3]), _videoFrameSoftTokens)));
                    else
                        VerifyOnGpu(() => RunAudioUnscheduled(opened, AudioFeatureExtractor.Extract(new float[AudioClip.SampleRate])));
                }
                catch (Exception e) when (_device == EmbeddingDevice.Auto && IsGpuFailure(e))
                {
                    // This encoder does not run on the GPU (the text model does): keep it on the CPU.
                    CloseSession(ref created);
                }
            }
            created ??= OpenValidatedMediaSession(path, inputs, null);
            Volatile.Write(ref field, created);
            return created;
        }
    }

    private InferenceSession OpenValidatedMediaSession(string path, string[] inputs, OrtEpDevice? gpu)
    {
        InferenceSession? created = OpenSession(path, gpu);
        try
        {
            var names = created.InputMetadata.Keys.ToHashSet(StringComparer.Ordinal);
            if (!names.SetEquals(inputs))
                throw new InvalidDataException($"Encoder inputs [{string.Join(", ", names)}] are not [{string.Join(", ", inputs)}].");
            if (created.OutputNames.Count != 1)
                throw new InvalidDataException($"Encoder has {created.OutputNames.Count} outputs; expected one.");
            return created;
        }
        catch
        {
            CloseSession(ref created);
            throw;
        }
    }

    /// <summary>Number of document runs that were terminated by a query and re-run (diagnostics and tests).</summary>
    internal int PreemptedRuns => Volatile.Read(ref _preemptedRuns);

    /// <summary>True while a document batch holds the session (tests).</summary>
    internal bool IsBackgroundRunActive => _scheduler.IsBackgroundRunActive;

    /// <summary>One ORT run and whether a query asked it to stop.</summary>
    internal sealed class ActiveRun(RunOptions options)
    {
        public RunOptions Options { get; } = options;

        public volatile bool Preempted;
    }

    /// <summary>
    /// Schedules inference on the shared sessions with query priority. Document (background) runs may overlap each
    /// other, up to <c>maxBackgroundRuns</c>: ONNX Runtime's <c>Run</c> is thread-safe, and the indexer decides how many
    /// it starts at once from the CPU budget. A query (interactive run) is exclusive: on arrival it terminates every
    /// running document batch and goes next; document batches wait while any query or exclusive entry is pending.
    /// </summary>
    internal sealed class RunScheduler(int maxBackgroundRuns = int.MaxValue)
    {
        // Monitor (not System.Threading.Lock) because waiting needs Monitor.Wait/PulseAll.
        private readonly object _gate = new();
        private readonly int _maxBackgroundRuns = Math.Max(1, maxBackgroundRuns);
        private readonly List<ActiveRun> _background = [];
        private bool _exclusive;
        private int _pendingExclusive;

        public bool IsBackgroundRunActive
        {
            get
            {
                lock (_gate)
                    return _background.Count > 0;
            }
        }

        /// <summary>Document runs in flight (tests, diagnostics).</summary>
        public int BackgroundRuns
        {
            get
            {
                lock (_gate)
                    return _background.Count;
            }
        }

        public void EnterInteractive(CancellationToken cancellationToken) => EnterExclusiveCore(preempt: true, cancellationToken);

        public void EnterBackground(ActiveRun run, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                WaitUntil(() => !_exclusive && _pendingExclusive == 0 && _background.Count < _maxBackgroundRuns, cancellationToken);
                _background.Add(run);
            }
        }

        /// <summary>Waits for the sessions to be idle (used by Dispose) and keeps them reserved.</summary>
        public void EnterExclusive() => EnterExclusiveCore(preempt: false, CancellationToken.None);

        private void EnterExclusiveCore(bool preempt, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _pendingExclusive++;
                try
                {
                    if (preempt)
                    {
                        foreach (var running in _background)
                        {
                            running.Preempted = true;
                            running.Options.Terminate = true;
                        }
                    }
                    WaitUntil(() => !_exclusive && _background.Count == 0, cancellationToken);
                    _exclusive = true;
                }
                finally
                {
                    _pendingExclusive--;
                    if (_pendingExclusive == 0)
                        Monitor.PulseAll(_gate);
                }
            }
        }

        public void Exit(ActiveRun? run)
        {
            lock (_gate)
            {
                if (run is null || !_background.Remove(run))
                    _exclusive = false;
                Monitor.PulseAll(_gate);
            }
        }

        private void WaitUntil(Func<bool> condition, CancellationToken cancellationToken)
        {
            if (condition())
                return;
            // The callback takes _gate, and this method runs while holding it. Disposing the registration here would
            // block until a callback already running on another thread (the canceller) finishes, while that callback
            // blocks on _gate: a deadlock. Unregister() never waits; a callback that already started just pulses the
            // monitor after we release it, which is harmless.
            var registration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(static state => ((RunScheduler)state!).Wake(), this)
                : default;
            try
            {
                while (!condition())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Monitor.Wait(_gate);
                }
            }
            finally
            {
                registration.Unregister();
            }
        }

        private void Wake()
        {
            lock (_gate)
                Monitor.PulseAll(_gate);
        }
    }

    private ITextTokenizer GetTokenizer()
    {
        var tokenizer = _tokenizer;
        if (tokenizer is not null)
            return tokenizer;
        lock (_loadLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_tokenizer is not null)
                return _tokenizer;
            var json = Path.Combine(_modelDirectory, "tokenizer.json");
            _tokenizer = File.Exists(json)
                ? HfBpeTokenizer.Load(json)
                : SentencePieceTextTokenizer.Load(Path.Combine(_modelDirectory, "tokenizer.model"));
            return _tokenizer;
        }
    }

    private InferenceSession GetSession()
    {
        var session = _session;
        if (session is not null)
            return session;
        lock (_loadLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null)
                return _session;

            OrtEpDevice? gpu = null;
            if (_device == EmbeddingDevice.Auto && GpuCrashedBefore())
            {
                GpuUnavailableReason = $"Valafind stopped unexpectedly while first using the GPU; staying on the CPU (delete '{_gpuCrashMarker}' to retry)";
            }
            else if (_device != EmbeddingDevice.Cpu)
            {
                gpu = WebGpuDevice.Find(out var reason);
                if (gpu is null)
                {
                    if (_device == EmbeddingDevice.Gpu)
                        throw new NotSupportedException($"No usable GPU: {reason}");
                    GpuUnavailableReason = reason;
                }
            }

            if (gpu is not null)
            {
                try
                {
                    return CreateTextSession(gpu);
                }
                catch (Exception e) when (_device == EmbeddingDevice.Auto && IsGpuFailure(e))
                {
                    GpuUnavailableReason = $"GPU session failed: {e.Message}";
                }
            }
            return CreateTextSession(null);
        }
    }

    private static bool IsGpuFailure(Exception e) =>
        e is OnnxRuntimeException or InvalidDataException or EntryPointNotFoundException or DllNotFoundException or NotSupportedException;

    /// <summary>Opens and validates the text model on the CPU or <paramref name="gpu"/>. Called under <see cref="_loadLock"/>.</summary>
    private InferenceSession CreateTextSession(OrtEpDevice? gpu)
    {
        var created = OpenSession(_modelPath, gpu);
        try
        {
            ValidateSignature(created);
            if (gpu is not null)
            {
                var tokenizer = GetTokenizer();
                VerifyOnGpu(() => RunTextModelOn(created, [[tokenizer.BosId, tokenizer.EosId]], null, body =>
                {
                    using var runOptions = new RunOptions();
                    return body(runOptions);
                }));
            }
        }
        catch
        {
            InferenceSession? failed = created;
            CloseSession(ref failed);
            throw;
        }
        _gpu = gpu;
        ActiveDevice = gpu is null ? "CPU" : WebGpuDevice.Describe(gpu);
        _session = created;
        return created;
    }

    /// <summary>
    /// Opens one ONNX file on the CPU or <paramref name="gpu"/>, with its own session options (kept in
    /// <see cref="_sessionOptions"/> until disposal).
    /// </summary>
    private InferenceSession OpenSession(string path, OrtEpDevice? gpu)
    {
        var options = new SessionOptions
        {
            IntraOpNumThreads = _intraOpThreads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        try
        {
            // Idle ORT pool threads must sleep, not spin: spinning burns CPU between ops and batches, and that CPU is
            // invisible to the throttle gate (it subtracts the process's own usage).
            options.AddSessionConfigEntry(IntraOpSpinningKey, "0");
            options.AddSessionConfigEntry(InterOpSpinningKey, "0");
            if (gpu is not null)
                WebGpuDevice.Append(options, gpu, WebGpuDevice.CpuNodes(File.ReadAllBytes(path)));
            var session = new InferenceSession(path, options);
            _sessionOptions[session] = options;
            return session;
        }
        catch
        {
            options.Dispose();
            throw;
        }
    }

    /// <summary>Where inference runs once loaded: "CPU" or "GPU: vendor (WebGPU)"; null before the model is loaded.</summary>
    public string? ActiveDevice { get; private set; }

    /// <summary>True once the model is loaded on the GPU.</summary>
    public bool UsesGpu => _gpu is not null;

    /// <summary>Why <see cref="EmbeddingDevice.Auto"/> fell back to the CPU, or null.</summary>
    public string? GpuUnavailableReason { get; private set; }

    private void ValidateSignature(InferenceSession session)
    {
        var inputs = session.InputMetadata.Keys.ToHashSet(StringComparer.Ordinal);
        var known = new HashSet<string>(StringComparer.Ordinal) { InputIdsName, AttentionMaskName, ImageFeaturesName, VideoFeaturesName, AudioFeaturesName };
        if (!inputs.Contains(InputIdsName) || !inputs.Contains(AttentionMaskName) || !inputs.IsSubsetOf(known))
            throw new InvalidDataException($"Model inputs [{string.Join(", ", inputs)}] are not those of EmbeddingGemma 2 ({string.Join(", ", known)}).");
        foreach (var name in new[] { ImageFeaturesName, VideoFeaturesName, AudioFeaturesName })
        {
            if (session.InputMetadata.TryGetValue(name, out var meta) && meta.Dimensions is [_, > 0 and var width])
                _featureWidth = width;
        }
        _textInputs = inputs;
        if (!session.OutputMetadata.TryGetValue(SentenceEmbeddingName, out var output))
            throw new InvalidDataException($"Model has no '{SentenceEmbeddingName}' output (outputs: {string.Join(", ", session.OutputMetadata.Keys)}).");
        var dims = output.Dimensions;
        _outputWidth = dims.Length == 2 ? dims[1] : -1;
        if (_outputWidth > 0 && _outputWidth < Dimensions)
            throw new InvalidDataException($"Model produces {_outputWidth}-d embeddings; {Dimensions} requested.");
    }

    /// <summary>Releases the ONNX Runtime session. Waits for an in-flight inference call to finish; calls waiting
    /// for the session then throw <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        lock (_loadLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _scheduler.EnterExclusive();
            try
            {
                _visionSession?.Dispose();
                _visionSession = null;
                _audioSession?.Dispose();
                _audioSession = null;
                _session?.Dispose();
                _session = null;
                foreach (var options in _sessionOptions.Values)
                    options.Dispose();
                _sessionOptions.Clear();
                _tokenizer = null;
            }
            finally
            {
                _scheduler.Exit(null);
            }
        }
    }
}
