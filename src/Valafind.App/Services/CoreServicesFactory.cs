using Microsoft.Extensions.Logging;
using Valafind.Core.Abstractions;
using Valafind.Core.Configuration;
using Valafind.Core.Embedding;
using Valafind.Core.Extraction;
using Valafind.Core.Indexing;
using Valafind.Core.Platform;
using Valafind.Core.Search;
using Valafind.Core.Storage;

namespace Valafind.App.Services;

/// <summary>The running Core services; disposed together, indexing first.</summary>
internal sealed class CoreServices(
    IEmbeddingModel model,
    IIndexStore store,
    ISearchService search,
    IIndexingService indexing,
    ISystemPressureMonitor monitor,
    string device = "CPU") : IAsyncDisposable
{
    public IEmbeddingModel Model { get; } = model;

    /// <summary>Where the model runs, e.g. "CPU" or "GPU: NVIDIA (WebGPU)" (with the reason when the GPU was not usable).</summary>
    public string Device { get; } = device;
    public IIndexStore Store { get; } = store;
    public ISearchService Search { get; } = search;
    public IIndexingService Indexing { get; } = indexing;
    public ISystemPressureMonitor Monitor { get; } = monitor;

    public async ValueTask DisposeAsync()
    {
        // Stop the indexer before tearing down what it uses. Dispose calls are expected to be idempotent in case the
        // coordinator already disposed something it owns.
        try
        {
            await Indexing.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            Monitor.Dispose();
            Store.Dispose();
            Model.Dispose();
        }
    }
}

/// <summary>
/// The only place that names concrete Core implementations. Everything else in the App talks to the contracts in
/// <c>Valafind.Core.Abstractions</c>.
/// </summary>
internal static class CoreServicesFactory
{
    /// <summary>True when this build carries the model inside the executable (release builds).</summary>
    public static bool IsModelBundled => BundledModel.IsBundled(Valafind.Model.ModelResources.Assembly);

    /// <summary>
    /// Unpacks the model embedded in release builds into <see cref="DefaultModelDirectory"/> (first start, or after the
    /// files there were damaged). Skipped when nothing is bundled or when another valid model is in use (Settings,
    /// <c>VALAFIND_MODEL_DIR</c>, a copy next to the app), so no disk space goes to an unused copy. Slow on first start
    /// (~850 MB written): call off the UI thread. Returns the number of files written. Also removes the previous
    /// model (EmbeddingGemma-300m) that earlier versions unpacked next to it: it is never loaded again.
    /// </summary>
    public static int InstallBundledModel(string? configuredDirectory, CancellationToken cancellationToken)
    {
        var assembly = Valafind.Model.ModelResources.Assembly;
        if (!BundledModel.IsBundled(assembly))
            return 0;
        var found = ModelLocator.Find(configuredDirectory);
        if (found is not null && !string.Equals(Path.GetFullPath(found), Path.GetFullPath(DefaultModelDirectory),
                OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            return 0;
        var written = BundledModel.EnsureInstalled(assembly, DefaultModelDirectory, cancellationToken);
        RemoveLegacyModel();
        return written;
    }

    /// <summary>Deletes <c>models/embeddinggemma-300m</c> in the data folder (only that folder, never a user-chosen one).</summary>
    internal static void RemoveLegacyModel()
    {
        var legacy = Path.Combine(AppPaths.ModelsDirectory, ModelLocator.LegacyModelFolderName);
        try
        {
            if (Directory.Exists(legacy))
                Directory.Delete(legacy, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // In use or locked: tried again on the next start.
        }
    }

    /// <summary>Finds the installed EmbeddingGemma 2 model directory, honouring the configured override.</summary>
    public static string? LocateModel(string? configuredDirectory) => ModelLocator.Find(configuredDirectory);

    /// <summary>
    /// Identifies the model install that <see cref="LocateModel"/> would load right now: its folder plus the size and
    /// time stamp of the files that matter, or null when no valid model is installed. Cheap (a few file probes); used
    /// to notice an install (or a repaired one) without restarting.
    /// </summary>
    public static string? ModelSignature(string? configuredDirectory) => SignatureOf(ModelLocator.Find(configuredDirectory));

    /// <summary>Signature of the model in <paramref name="directory"/>, or null when it is not a valid model folder.</summary>
    internal static string? SignatureOf(string? directory)
    {
        if (directory is null || !ModelLocator.IsValid(directory, out var modelFile))
            return null;
        var sb = new System.Text.StringBuilder(directory).Append('|').Append(modelFile);
        var model = Path.Combine(directory, modelFile.Replace('/', Path.DirectorySeparatorChar));
        foreach (var file in new[] { model, model + "_data", Path.Combine(directory, "tokenizer.json"), Path.Combine(directory, "tokenizer.model") })
        {
            var info = new FileInfo(file);
            sb.Append('|');
            if (info.Exists)
                sb.Append(info.Length).Append('@').Append(info.LastWriteTimeUtc.Ticks);
        }
        return sb.ToString();
    }

    /// <summary>Where the user should install the model (configured directory or the default models folder).</summary>
    public static string ExpectedModelDirectory(ValafindSettings settings) =>
        string.IsNullOrWhiteSpace(settings.ModelDirectory) ? DefaultModelDirectory : settings.ModelDirectory;

    /// <summary>The folder tools/fetch-model.* installs into by default.</summary>
    public static string DefaultModelDirectory => Path.Combine(AppPaths.ModelsDirectory, ModelLocator.ModelFolderName);

    /// <summary>
    /// Loads the model and opens the index (slow: seconds). Call off the UI thread. On failure every partially
    /// created service is disposed and the exception propagates.
    /// </summary>
    public static CoreServices Create(string modelDirectory, ValafindSettings settings, ILoggerFactory loggerFactory)
    {
        // Typed as the contracts on purpose: this file is the only one naming the implementations.
#pragma warning disable CA1859
        IEmbeddingModel? model = null;
        IIndexStore? store = null;
#pragma warning restore CA1859
        ISystemPressureMonitor? monitor = null;
        try
        {
            var gemma = new EmbeddingGemmaModel(new EmbeddingOptions
            {
                ModelDirectory = modelDirectory,
                Dimensions = settings.EmbeddingDimensions,
                Device = settings.UseGpu ? EmbeddingDevice.Auto : EmbeddingDevice.Cpu,
                GpuCrashMarker = Path.Combine(AppPaths.DataDirectory, "gpu-check.pending"),
            });
            model = gemma;
            // Load now (we are off the UI thread) rather than on the first keystroke: a corrupt model surfaces as
            // "model failed" at startup, and the first search is as fast as the rest (~0.25 s instead of ~2.5 s).
            gemma.EnsureLoaded();
            _ = gemma.EmbedQuery("warm up");
            IDocumentReader reader = new DocumentReader();
            ITextChunker chunker = new TextChunker(new ChunkerOptions(), model.CountTokens);

            AppPaths.EnsurePrivateDirectory(AppPaths.DataDirectory);
            store = new SqliteIndexStore(AppPaths.IndexDatabase);
            store.Open(model.ModelId, model.Dimensions);

            ISearchService search = new SearchService(model, store);
            monitor = SystemPressureMonitor.Create();
            IFileDiscovery discovery = new UserFileDiscovery(settings);
            IMediaDecoder mediaDecoder = new Valafind.Core.Extraction.Media.MediaDecoder(loggerFactory.CreateLogger<IMediaDecoder>());
            IIndexingService indexing = new IndexingCoordinator(discovery, reader, chunker, model, store, monitor, settings,
                loggerFactory.CreateLogger<IndexingCoordinator>(), null, mediaDecoder);

            var device = gemma.ActiveDevice ?? "CPU";
            if (gemma.GpuUnavailableReason is { } why)
                device += $" ({why})";
            return new CoreServices(model, store, search, indexing, monitor, device);
        }
        catch
        {
            monitor?.Dispose();
            store?.Dispose();
            model?.Dispose();
            throw;
        }
    }
}
