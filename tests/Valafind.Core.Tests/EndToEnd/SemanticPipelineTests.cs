using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging.Abstractions;
using Valafind.Core.Abstractions;
using Valafind.Core.Configuration;
using Valafind.Core.Embedding;
using Valafind.Core.Extraction;
using Valafind.Core.Indexing;
using Valafind.Core.Search;
using Valafind.Core.Storage;
using Valafind.Core.Tests.Embedding;
using Valafind.Core.Tests.Indexing;
using Xunit;

namespace Valafind.Core.Tests.EndToEnd;

/// <summary>
/// The whole pipeline with real components: real files on disk → UserFileDiscovery → DocumentReader → TextChunker →
/// EmbeddingGemma (ONNX) → SqliteIndexStore → SearchService. Only system pressure is faked (always calm).
/// Needs VALAFIND_TEST_MODEL_DIR; skipped otherwise.
/// </summary>
public sealed class SemanticPipelineTests : IDisposable
{
    private readonly TempTree _home = new();
    private readonly string _data = Path.Combine(Path.GetTempPath(), "valafind-e2e-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _home.Dispose();
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Indexes_real_files_and_finds_them_by_meaning_not_by_name()
    {
        var modelDir = Environment.GetEnvironmentVariable(ModelFixture.EnvironmentVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(modelDir) || !ModelLocator.IsValid(modelDir, out _),
            $"{ModelFixture.EnvironmentVariable} not set; skipping end-to-end semantic test.");
        ModelLocator.IsValid(modelDir!, out var modelFile);

        // File names deliberately carry no hint of their content, so only the embedded text can match.
        var lease = WriteDocx(_home.P("Documents/scan_0042.docx"),
            "Lejekontrakt for lejligheden på Nørrebrogade 12, 2. th.",
            "§ 7. Depositum svarende til 3 måneders husleje, i alt 42.000 kr., betales ved underskrift. " +
            "Udlejer tilbagebetaler depositum senest 14 dage efter fraflytning, fratrukket eventuelle istandsættelsesudgifter.");
        var bread = _home.File("Documents/notes/n1.md",
            "# Weekend baking\n\nFeed the starter the night before. Mix 500 g flour, 350 g water and 10 g salt, " +
            "stretch and fold every 30 minutes, then proof overnight in the fridge and bake in a Dutch oven at 250 °C.");
        var churn = _home.File("Work/q3.txt",
            "Board update: customer retention fell in Q3. Monthly churn rose from 2.1% to 3.4%, driven by annual plan " +
            "cancellations after the price change. Proposed actions: win-back campaign and a cheaper tier.");
        var code = _home.File("Projects/tool/retry.py",
            "def retry(fn, attempts=5):\n    for i in range(attempts):\n        try:\n            return fn()\n" +
            "        except IOError:\n            time.sleep(2 ** i)  # exponential backoff\n    raise RuntimeError('gave up')\n");
        _home.File("Projects/tool/node_modules/dep/index.js", "module.exports = 'should never be indexed';");

        var settings = new ValafindSettings { Roots = [_home.Root], EmbeddingDimensions = 256 };
        using var model = new EmbeddingGemmaModel(new EmbeddingOptions
        {
            ModelDirectory = modelDir!, ModelFile = modelFile, Dimensions = 256, IntraOpThreads = 2,
        });
        using var store = new SqliteIndexStore(Path.Combine(_data, "index.db"));
        store.Open(model.ModelId, model.Dimensions);
        var discovery = new UserFileDiscovery(settings, _home.Root, _data, TimeProvider.System, _ => null);
        var chunker = new TextChunker(new ChunkerOptions { TargetTokens = Math.Min(320, model.MaxInputTokens) }, model.CountTokens);

        await using (var coordinator = new IndexingCoordinator(discovery, new DocumentReader(), chunker, model, store,
                         new FakeMonitor { Cpu = 1, DiskBusy = 0, DiskBytes = 0 }, settings,
                         NullLogger<IndexingCoordinator>.Instance, null,
                         CoordinatorTuning.Default with
                         {
                             Debounce = TimeSpan.FromMilliseconds(100),
                             IdlePoll = TimeSpan.FromMilliseconds(200),
                             Throttle = new ThrottleOptions(30, 25, TimeSpan.Zero, true, TimeSpan.FromMilliseconds(25)),
                         }))
        {
            await coordinator.StartAsync(TestContext.Current.CancellationToken);
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (coordinator.Status.State != IndexingState.UpToDate)
            {
                Assert.True(DateTime.UtcNow < deadline, $"indexing did not finish: {coordinator.Status}");
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }

        Assert.Equal(4, store.GetStats().Files);
        Assert.Null(store.GetFingerprint(_home.P("Projects/tool/node_modules/dep/index.js")));

        var search = new SearchService(model, store);
        async Task<IReadOnlyList<SearchResult>> Ask(string q) =>
            await search.SearchAsync(q, SearchFilter.None, 10, TestContext.Current.CancellationToken);

        // Cross-lingual: English query, Danish document named scan_0042.docx.
        var r = await Ask("the lease agreement where we negotiated the deposit");
        Assert.Equal(lease, r[0].Path);
        Assert.NotEmpty(r[0].Highlights);
        Assert.All(r[0].Highlights, h => Assert.InRange(h.Start + h.Length, 1, r[0].Snippet.Length));

        Assert.Equal(bread, (await Ask("how do I make sourdough bread"))[0].Path);
        Assert.Equal(churn, (await Ask("why are customers cancelling their subscriptions"))[0].Path);
        Assert.Equal(code, (await Ask("function that retries with exponential backoff"))[0].Path);

        // Kind filters apply.
        var onlyCode = await search.SearchAsync("deposit", new SearchFilter(new HashSet<FileKind> { FileKind.Code }), 10,
            TestContext.Current.CancellationToken);
        Assert.All(onlyCode, x => Assert.Equal(FileKind.Code, x.Kind));
    }

    private static string WriteDocx(string path, params string[] paragraphs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body(paragraphs.Select(p => new Paragraph(new Run(new Text(p))))));
        main.Document.Save();
        return path;
    }
}
