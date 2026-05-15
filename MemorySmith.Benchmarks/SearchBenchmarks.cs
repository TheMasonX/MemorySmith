using BenchmarkDotNet.Attributes;
using MemorySmith.App.Services;
using MemorySmith.Core.Indexing;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using Microsoft.Extensions.Options;

namespace MemorySmith.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class SearchBenchmarks
{
    private MemoryApplicationService _service = null!;

    private readonly HybridMemorySearchQuery _hybridQuery = new("hybrid search lucene rrf fusion", Tags: "project-wiki", Limit: 10);
    private readonly SemanticMemorySearchQuery _semanticQuery = new("model context protocol tool calling", Tags: "project-wiki", Limit: 10);
    private readonly MemorySearchQuery _keywordQuery = new("Data Folder", Tags: "project-wiki", Limit: 10);
    private readonly MemoryContextPackQuery _contextPackQuery = new("mcp context pack format json markdown", Tags: "project-wiki", Limit: 3, ReferenceDepth: 1, MaxRecords: 12);

    [GlobalSetup]
    public void Setup()
    {
        _service = BenchmarkServiceFactory.Create();
    }

    [Benchmark]
    public async Task<int> KeywordSearch()
    {
        var results = await _service.SearchAsync(_keywordQuery, CancellationToken.None);
        return results.Count;
    }

    [Benchmark]
    public async Task<int> SemanticSearch()
    {
        var results = await _service.SemanticSearchAsync(_semanticQuery, CancellationToken.None);
        return results.Count;
    }

    [Benchmark]
    public async Task<int> HybridSearch()
    {
        var results = await _service.HybridSearchAsync(_hybridQuery, CancellationToken.None);
        return results.Count;
    }

    [Benchmark]
    public async Task<int> ContextPack()
    {
        var pack = await _service.BuildContextPackAsync(_contextPackQuery, CancellationToken.None);
        return pack.Records.Count;
    }

    public static async Task RunSmokeAsync()
    {
        var benchmarks = new SearchBenchmarks();
        benchmarks.Setup();

        Console.WriteLine($"KeywordSearch: {await benchmarks.KeywordSearch()} results");
        Console.WriteLine($"SemanticSearch: {await benchmarks.SemanticSearch()} results");
        Console.WriteLine($"HybridSearch: {await benchmarks.HybridSearch()} results");
        Console.WriteLine($"ContextPack: {await benchmarks.ContextPack()} records");
    }
}

internal static class BenchmarkServiceFactory
{
    public static MemoryApplicationService Create()
    {
        var dataPath = Path.Combine(FindRepositoryRoot(), "Data", "Memories");
        var store = new FileMemoryStore(dataPath, new StorageDiagnostics());
        var index = new MemoryIndex();
        foreach (var record in store.LoadAll())
        {
            index.Add(record);
        }

        return new MemoryApplicationService(
            store,
            new NoOpEventStore(),
            index,
            new BackgroundServiceTelemetryTracker(),
            new NoOpPublisher(),
            Options.Create(new MemorySmithOptions()));
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

        throw new DirectoryNotFoundException("Could not locate MemorySmith.slnx from the benchmark output directory.");
    }
}

internal sealed class NoOpEventStore : IEventStore
{
    public void AppendEvent(MemoryEvent @event)
    {
    }

    public IEnumerable<MemoryEvent> GetEvents(string? memoryId = null, DateTime? since = null) => [];
}

internal sealed class NoOpPublisher : IMemoryChangePublisher
{
#pragma warning disable CS0067
    public event Func<MemoryUpdateEvent, Task>? MemoryChanged;
    public event Func<StatsSnapshot, Task>? StatsChanged;
#pragma warning restore CS0067

    public Task PublishMemoryChangedAsync(MemoryUpdateEvent update) => Task.CompletedTask;

    public Task PublishStatsChangedAsync(StatsSnapshot stats) => Task.CompletedTask;
}