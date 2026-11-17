using Microsoft.ML.OnnxRuntime;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Embedding;

/// <summary>
/// <see cref="IEmbeddingModel"/> backed by Google's EmbeddingGemma-300m (ONNX export by onnx-community) running on
/// ONNX Runtime's CPU provider. Entirely offline: the tokenizer and model are read from
/// <see cref="EmbeddingOptions.ModelDirectory"/> and nothing is ever downloaded.
/// </summary>
/// <remarks>
/// <para>The tokenizer and inference session are loaded on first use (or by <see cref="EnsureLoaded"/>). Load
/// failures are not cached, so a later call retries.</para>
/// <para>Thread-safe: tokenisation runs concurrently, inference calls are serialised on one session with query
/// priority. <see cref="EmbedQuery"/> never waits behind a document batch: when a query arrives while
/// <see cref="EmbedDocuments"/> is running a batch, that ORT run is terminated (<see cref="RunOptions.Terminate"/>),
/// the query runs, and the interrupted batch is re-run transparently (the document call just takes longer; its
/// cancellation token is still honoured). Document batches also wait while any query is queued.
/// <see cref="EmbedDocumentsInteractive"/> (the search "explain" pass) gets the same priority as a query.</para>
/// <para>ORT's intra-op worker threads are created by native code when the session is built; they are not managed
/// threads, so their OS priority cannot be changed from C# (BackgroundPriority on the calling thread does not reach
/// them). To keep them cheap when idle, spin-waiting is disabled (<c>session.intra_op.allow_spinning = 0</c>,
/// likewise inter-op), so they block instead of burning CPU between runs.</para>
/// <para>Queries are embedded as <c>"task: search result | query: {query}"</c> and documents as
/// <c>"title: {title|none} | text: {text}"</c>. The model's 768-d output is truncated to
/// <see cref="Dimensions"/> (Matryoshka) and re-normalised.</para>
/// </remarks>
public sealed class EmbeddingGemmaModel : IEmbeddingModel, IInteractiveEmbedding
{
    /// <summary>Model family part of <see cref="ModelId"/>.</summary>
    public const string ModelName = "embeddinggemma-300m";

    internal const int MinTokens = 64;

    /// <summary>Document titles longer than this many tokens are shortened.</summary>
    internal const int TitleTokenBudget = 32;

    /// <summary>Tokens reserved outside the caller's text: BOS + EOS, the document prompt framing
    /// ("title: " / " | text: ", 5 tokens), the title budget, and slack for merges across the prompt boundary.</summary>
    internal const int ReservedPromptTokens = 2 + 6 + TitleTokenBudget + 4;

    internal const string InputIdsName = "input_ids";
    internal const string AttentionMaskName = "attention_mask";
    internal const string SentenceEmbeddingName = "sentence_embedding";

    internal const string IntraOpSpinningKey = "session.intra_op.allow_spinning";
    internal const string InterOpSpinningKey = "session.inter_op.allow_spinning";

    private readonly string _modelDirectory;
    private readonly string _modelPath;
    private readonly int _maxTokens;
    private readonly int _intraOpThreads;
    private readonly int _batchSize;

    private readonly Lock _loadLock = new();
    private readonly RunScheduler _scheduler = new();
    private int _preemptedRuns;
    private volatile ITextTokenizer? _tokenizer;
    private volatile InferenceSession? _session;
    private SessionOptions? _sessionOptions;
    private int _outputWidth;
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
        _batchSize = options.BatchSize;
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
    }

    /// <summary>E.g. <c>"embeddinggemma-300m/model_quantized/256"</c>.</summary>
    public string ModelId { get; }

    /// <inheritdoc />
    public int Dimensions { get; }

    /// <summary>Largest <see cref="CountTokens"/> value a document chunk may have without being truncated: the
    /// configured <see cref="EmbeddingOptions.MaxTokens"/> minus room for BOS/EOS, the document prompt and the title.</summary>
    public int MaxInputTokens => _maxTokens - ReservedPromptTokens;

    /// <summary>Full path of the ONNX file in use.</summary>
    internal string ModelPath => _modelPath;

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
    internal float[][] RunBatch(IReadOnlyList<int[]> sequences, CancellationToken cancellationToken, bool interactive = false)
    {
        var session = GetSession();
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
        using var idsValue = OrtValue.CreateTensorValueFromMemory(inputIds, shape);
        using var maskValue = OrtValue.CreateTensorValueFromMemory(mask, shape);

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
                    return Execute(session, run.Options, idsValue, maskValue, batch);
                }
                catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Embedding was cancelled.", cancellationToken);
                }
                catch (OnnxRuntimeException) when (run.Preempted)
                {
                    // A query terminated this document run; re-run it once the query is done.
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

    private float[][] Execute(InferenceSession session, RunOptions runOptions, OrtValue idsValue, OrtValue maskValue, int batch)
    {
        var results = new float[batch][];
        using var outputs = session.Run(runOptions, [InputIdsName, AttentionMaskName], [idsValue, maskValue], [SentenceEmbeddingName]);
        var output = outputs[0];
        var outShape = output.GetTensorTypeAndShape().Shape;
        if (outShape.Length != 2 || outShape[0] != batch || outShape[1] < Dimensions)
            throw new InvalidDataException($"Unexpected {SentenceEmbeddingName} shape [{string.Join(", ", outShape)}].");
        var width = (int)outShape[1];
        var data = output.GetTensorDataAsSpan<float>();
        for (var b = 0; b < batch; b++)
            results[b] = Matryoshka.TruncateAndNormalize(data.Slice(b * width, width), Dimensions);
        return results;
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
    /// Serialises inference on the single session with query priority: a query that arrives while a document
    /// batch runs terminates that run and goes next; document batches wait while any query is pending.
    /// </summary>
    internal sealed class RunScheduler
    {
        // Monitor (not System.Threading.Lock) because waiting needs Monitor.Wait/PulseAll.
        private readonly object _gate = new();
        private bool _busy;
        private int _pendingInteractive;
        private ActiveRun? _background;

        public bool IsBackgroundRunActive
        {
            get
            {
                lock (_gate)
                    return _background is not null;
            }
        }

        public void EnterInteractive(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _pendingInteractive++;
                try
                {
                    if (_background is { } running)
                    {
                        running.Preempted = true;
                        running.Options.Terminate = true;
                    }
                    WaitUntil(() => !_busy, cancellationToken);
                    _busy = true;
                }
                finally
                {
                    _pendingInteractive--;
                    if (_pendingInteractive == 0)
                        Monitor.PulseAll(_gate);
                }
            }
        }

        public void EnterBackground(ActiveRun run, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                WaitUntil(() => !_busy && _pendingInteractive == 0, cancellationToken);
                _busy = true;
                _background = run;
            }
        }

        /// <summary>Waits for the session to be idle (used by Dispose) and keeps it reserved.</summary>
        public void EnterExclusive()
        {
            lock (_gate)
            {
                WaitUntil(() => !_busy, CancellationToken.None);
                _busy = true;
            }
        }

        public void Exit(ActiveRun? run)
        {
            lock (_gate)
            {
                if (run is not null && ReferenceEquals(_background, run))
                    _background = null;
                _busy = false;
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

            var options = new SessionOptions
            {
                IntraOpNumThreads = _intraOpThreads,
                InterOpNumThreads = 1,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            };
            // Idle ORT pool threads must sleep, not spin: spinning burns CPU between ops and batches, and that CPU is
            // invisible to the throttle gate (it subtracts the process's own usage).
            options.AddSessionConfigEntry(IntraOpSpinningKey, "0");
            options.AddSessionConfigEntry(InterOpSpinningKey, "0");
            InferenceSession? created = null;
            try
            {
                created = new InferenceSession(_modelPath, options);
                ValidateSignature(created);
                _sessionOptions = options;
                _session = created;
                return created;
            }
            catch
            {
                created?.Dispose();
                options.Dispose();
                throw;
            }
        }
    }

    private void ValidateSignature(InferenceSession session)
    {
        var inputs = session.InputMetadata.Keys.ToHashSet(StringComparer.Ordinal);
        if (!inputs.SetEquals([InputIdsName, AttentionMaskName]))
            throw new InvalidDataException($"Model inputs [{string.Join(", ", inputs)}] are not [{InputIdsName}, {AttentionMaskName}].");
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
                _session?.Dispose();
                _session = null;
                _sessionOptions?.Dispose();
                _sessionOptions = null;
                _tokenizer = null;
            }
            finally
            {
                _scheduler.Exit(null);
            }
        }
    }
}
