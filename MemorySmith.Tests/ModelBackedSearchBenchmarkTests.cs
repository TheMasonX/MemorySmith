using System.Diagnostics;
using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using Microsoft.Extensions.Options;

namespace MemorySmith.Tests;

[TestFixture]
[Category("Benchmark")]
[NonParallelizable]
public sealed class ModelBackedSearchBenchmarkTests
{
    private string _tempRoot = null!;
    private FileMemoryStore _store = null!;
    private MemoryApplicationService _service = null!;
    private OnnxTextEmbeddingProvider? _provider;
    private RetrievalProviderMetadata _providerMetadata = null!;

    private static readonly ModelSearchProbe[] SemanticProbes =
    [
        new("context pack agent readiness knowledge base", "project-wiki-mcp-context-pack", 6, 500),
        new("json rpc tool calls local wiki search intercept", "project-wiki-mcp-search-tools-current", 6, 500),
        new("copied screenshot html img data url clipboard", "project-wiki-chat-image-attachments", 6, 500),
        new("single deployable host removed worker dashboard", "project-wiki-active-architecture", 6, 500),
        new("percent var tokens vars json source bundle line ranges", "project-wiki-source-links-feature", 6, 500)
    ];

    private static readonly ModelSearchProbe[] HybridProbes =
    [
        new("context pack agent readiness knowledge base", "project-wiki-mcp-context-pack", 4, 750),
        new("json rpc tool calls local wiki search intercept", "project-wiki-mcp-search-tools-current", 4, 750),
        new("copied screenshot html img data url clipboard", "project-wiki-chat-image-attachments", 4, 750),
        new("single deployable host removed worker dashboard", "project-wiki-active-architecture", 4, 750),
        new("percent var tokens vars json source bundle line ranges", "project-wiki-source-links-feature", 4, 750)
    ];

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"memorysmith-model-backed-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);

        var dataPath = CopyFixtureWithModels(_tempRoot);
        _store = new FileMemoryStore(dataPath, new StorageDiagnostics());

        var options = Options.Create(new MemorySmithOptions
        {
            DataPath = dataPath,
            SemanticSearch = new SemanticSearchOptions
            {
                EmbeddingsEnabled = true,
                ModelPath = Path.Combine("Models", "embedding-model.onnx"),
                VocabularyPath = Path.Combine("Models", "vocab.txt"),
                MaxInputTokens = 512,
                MaxIndexedTextCharacters = 6000,
                QueryPrefix = "query: ",
                DocumentPrefix = "passage: "
            }
        });

        _provider = new OnnxTextEmbeddingProvider(options);
        var embeddingSearch = new SemanticEmbeddingSearchService(_provider, options);
        _service = CreateMemoryApplicationService(_store, _tempRoot, options, embeddingSearch);
        _providerMetadata = _service.GetSemanticProviderMetadata();

        if (!_providerMetadata.Available || !string.Equals(_providerMetadata.Mode, "onnx-embedding", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Ignore($"Model-backed harness requires an available ONNX provider. Status: {_providerMetadata.Reason}");
        }

        await _service.SemanticSearchAsync(
            new SemanticMemorySearchQuery("warm up semantic provider", Tags: "project-wiki", Limit: 3),
            CancellationToken.None);
        await _service.HybridSearchAsync(
            new HybridMemorySearchQuery("warm up semantic provider", Tags: "project-wiki", Limit: 3),
            CancellationToken.None);

        _providerMetadata = _service.GetSemanticProviderMetadata();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _provider?.Dispose();
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Test]
    public void SemanticProviderMetadata_UsesOnnxEmbeddingMode()
    {
        var providerMetadata = _service.GetSemanticProviderMetadata();

        Assert.Multiple(() =>
        {
            Assert.That(providerMetadata.Available, Is.True, providerMetadata.Reason);
            Assert.That(providerMetadata.Mode, Is.EqualTo("onnx-embedding"));
            Assert.That(providerMetadata.ModelPath, Is.Not.Null);
            Assert.That(providerMetadata.ModelPath!, Does.EndWith(Path.Combine("Models", "embedding-model.onnx")));
            Assert.That(providerMetadata.VocabularyPath, Is.Not.Null);
            Assert.That(providerMetadata.VocabularyPath!, Does.EndWith(Path.Combine("Models", "vocab.txt")));
            Assert.That(providerMetadata.Dimension, Is.Not.Null);
            Assert.That(providerMetadata.Dimension!.Value, Is.GreaterThan(0));
        });
    }

    [Test]
    public async Task SemanticSearch_ModelBackedProjectWikiProbes_MeetRelevanceAndLatencyThresholds()
    {
        var reciprocalRanks = new List<double>();

        foreach (var probe in SemanticProbes)
        {
            var stopwatch = Stopwatch.StartNew();
            var results = await _service.SemanticSearchAsync(
                new SemanticMemorySearchQuery(probe.Query, Tags: "project-wiki", Limit: 10),
                CancellationToken.None);
            stopwatch.Stop();

            var ids = results.Select(result => result.Id).ToList();
            var rank = ids.FindIndex(id => string.Equals(id, probe.ExpectedId, StringComparison.OrdinalIgnoreCase)) + 1;
            reciprocalRanks.Add(rank == 0 ? 0 : 1.0 / rank);

            Assert.Multiple(() =>
            {
                Assert.That(rank, Is.GreaterThan(0), $"Query '{probe.Query}' did not return expected id '{probe.ExpectedId}'. Results: {string.Join(", ", ids)}");
                Assert.That(rank, Is.LessThanOrEqualTo(probe.MaxRank), $"Query '{probe.Query}' ranked '{probe.ExpectedId}' at {rank}; expected <= {probe.MaxRank}.");
                Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThanOrEqualTo(probe.MaxMs),
                    $"Query '{probe.Query}' took {stopwatch.ElapsedMilliseconds} ms; expected <= {probe.MaxMs} ms.");
                Assert.That(results[rank - 1].MatchReason, Does.Contain("Embedding cosine similarity"));
            });

            TestContext.Out.WriteLine($"[semantic model {stopwatch.ElapsedMilliseconds,4} ms] {probe.Query} -> rank {rank}");
        }

        Assert.That(reciprocalRanks.Average(), Is.GreaterThanOrEqualTo(0.3),
            $"Model-backed semantic MRR was {reciprocalRanks.Average():0.###}; expected >= 0.3.");
    }

    [Test]
    public async Task HybridSearch_ModelBackedProjectWikiProbes_MeetRelevanceAndLatencyThresholds()
    {
        var reciprocalRanks = new List<double>();

        foreach (var probe in HybridProbes)
        {
            var stopwatch = Stopwatch.StartNew();
            var results = await _service.HybridSearchAsync(
                new HybridMemorySearchQuery(probe.Query, Tags: "project-wiki", Limit: 10),
                CancellationToken.None);
            stopwatch.Stop();

            var ids = results.Select(result => result.Id).ToList();
            var rank = ids.FindIndex(id => string.Equals(id, probe.ExpectedId, StringComparison.OrdinalIgnoreCase)) + 1;
            reciprocalRanks.Add(rank == 0 ? 0 : 1.0 / rank);

            Assert.Multiple(() =>
            {
                Assert.That(rank, Is.GreaterThan(0), $"Query '{probe.Query}' did not return expected id '{probe.ExpectedId}'. Results: {string.Join(", ", ids)}");
                Assert.That(rank, Is.LessThanOrEqualTo(probe.MaxRank), $"Query '{probe.Query}' ranked '{probe.ExpectedId}' at {rank}; expected <= {probe.MaxRank}.");
                Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThanOrEqualTo(probe.MaxMs),
                    $"Query '{probe.Query}' took {stopwatch.ElapsedMilliseconds} ms; expected <= {probe.MaxMs} ms.");
                Assert.That(results[rank - 1].MatchReason, Does.Contain("Embedding cosine similarity"));
                Assert.That(results[rank - 1].MatchReason, Does.Contain("semantic rank"));
            });

            TestContext.Out.WriteLine($"[hybrid model   {stopwatch.ElapsedMilliseconds,4} ms] {probe.Query} -> rank {rank}");
        }

        Assert.That(reciprocalRanks.Average(), Is.GreaterThanOrEqualTo(0.35),
            $"Model-backed hybrid MRR was {reciprocalRanks.Average():0.###}; expected >= 0.35.");
    }

    [Test]
    public async Task SemanticSearch_ModelBackedThroughputBaseline_5QueriesUnder1500Ms()
    {
        var queries = SemanticProbes.Select(probe => probe.Query).ToArray();

        var stopwatch = Stopwatch.StartNew();
        foreach (var query in queries)
        {
            await _service.SemanticSearchAsync(
                new SemanticMemorySearchQuery(query, Tags: "project-wiki", Limit: 5),
                CancellationToken.None);
        }

        stopwatch.Stop();

        Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(1500),
            $"5 model-backed semantic queries took {stopwatch.ElapsedMilliseconds} ms; expected under 1 500 ms.");
        TestContext.Out.WriteLine($"5 model-backed semantic queries: {stopwatch.ElapsedMilliseconds} ms ({stopwatch.ElapsedMilliseconds / queries.Length} ms avg)");
    }

    private static MemoryApplicationService CreateMemoryApplicationService(
        FileMemoryStore store,
        string tempRoot,
        IOptions<MemorySmithOptions> options,
        SemanticEmbeddingSearchService embeddingSearch)
    {
        var index = new MemorySmith.Core.Indexing.MemoryIndex();
        foreach (var record in store.LoadAll())
        {
            index.Add(record);
        }

        var diagnostics = new MemoryDiagnosticsService(
            new TagPolicyService(options),
            new VarResolver(new EmptyVarStore(), options),
            store,
            options);

        return new MemoryApplicationService(
            store,
            new FileEventStore(Path.Combine(tempRoot, "events.jsonl")),
            index,
            new BackgroundServiceTelemetryTracker(),
            new ModelBackedNoOpPublisher(),
            options,
            embeddingSearch,
            diagnostics: diagnostics);
    }

    private static string CopyFixtureWithModels(string tempRoot)
    {
        var memoriesPath = ProjectWikiFixture.CopyToTemp(tempRoot);
        var dataRoot = Path.GetDirectoryName(memoriesPath) ?? tempRoot;
        Directory.CreateDirectory(Path.Combine(dataRoot, "Events"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "Graph"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "Models"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "Pages"));
        var modelsSource = Path.Combine(FindRepositoryRoot(), "Data", "Models");
        if (Directory.Exists(modelsSource))
        {
            CopyDirectory(modelsSource, Path.Combine(dataRoot, "Models"));
        }

        return memoriesPath;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MemorySmith.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate MemorySmith.slnx from the test output directory.");
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var targetFile = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(file, targetFile, overwrite: true);
        }
    }
}

internal sealed record ModelSearchProbe(string Query, string ExpectedId, int MaxRank, int MaxMs);

internal sealed class ModelBackedNoOpPublisher : IMemoryChangePublisher
{
#pragma warning disable CS0067
    public event Func<MemoryUpdateEvent, Task>? MemoryChanged;
    public event Func<StatsSnapshot, Task>? StatsChanged;
#pragma warning restore CS0067

    public Task PublishMemoryChangedAsync(MemoryUpdateEvent update) => Task.CompletedTask;

    public Task PublishStatsChangedAsync(StatsSnapshot stats) => Task.CompletedTask;
}