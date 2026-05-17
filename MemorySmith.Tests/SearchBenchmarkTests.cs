using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace MemorySmith.Tests;

/// <summary>
/// Testbench for search quality and latency across all three search modes.
///
/// Each "query probe" asserts both relevance (expected top-hit id or must-contain id)
/// and latency (max allowed milliseconds). Failures are reported individually so every
/// probe gets a result in one pass.
///
/// Thresholds are deliberately loose (500 ms) to stay green on CI; tighten per probe
/// once a baseline is established.
/// </summary>
[TestFixture]
[Category("Benchmark")]
public class SearchBenchmarkTests
{
    private string _tempRoot = null!;
    private FileMemoryStore _store = null!;
    private MemoryApplicationService _service = null!;

    // ── Testbench probe definitions ───────────────────────────────────────────

    private static readonly SearchProbe[] LexicalProbes =
    [
        // Lexical search uses Lucene tokenization and scoring; use MustContainId for ranking flexibility.
        new("Data Folder", MustContainId: "project-wiki-data-folder-policy", MaxMs: 100),
        new("Validation Command", MustContainId: "project-wiki-validation-command", MaxMs: 100),
        new("Hybrid Search", MustContainId: "project-wiki-hybrid-search-rrf", MaxMs: 100),
        new("project-wiki", MustContainId: "project-wiki-active-architecture", MaxMs: 100, Limit: 100),
        new("Windows Service", MustContainId: "project-wiki-windows-service-operations", MaxMs: 100),
    ];

    private static readonly SearchProbe[] SemanticProbes =
    [
        new("model context protocol tool calling", MustContainId: "project-wiki-mcp-integration", MaxMs: 200),
        new("vector embeddings semantic gap local scoring", MustContainId: "project-wiki-semantic-search-gap", MaxMs: 200),
        new("context pack agent readiness knowledge base", MustContainId: "project-wiki-mcp-context-pack", MaxMs: 200),
        new("search architecture roadmap improvements", MustContainId: "project-wiki-search-roadmap", MaxMs: 200),
        new("blazor server UI single host deployment", MustContainId: "project-wiki-active-architecture", MaxMs: 200),
        new("chat model used stop generating streaming", MustContainId: "project-wiki-chat-streaming-thinking", MaxMs: 200),
        new("github free gpt claude haiku sonnet model preference", MustContainId: "project-wiki-chat-agent-provider", MaxMs: 200),
        new("chat context window usage rate limits mcp tools role", MustContainId: "project-wiki-chat-agent-provider", MaxMs: 200),
        new("chat intercepts mcp tool calls hybrid search same prompt", MustContainId: "project-wiki-chat-agent-provider", MaxMs: 200),
    ];

    private static readonly SearchProbe[] HybridProbes =
    [
        new("hybrid search lucene rrf fusion", TopId: "project-wiki-hybrid-search-rrf", MaxMs: 300),
        new("mcp context pack format json markdown", TopId: "project-wiki-mcp-context-pack", MaxMs: 300),
        new("source links file references path variables", MustContainId: "project-wiki-source-links-feature", MaxMs: 300),
        new("semantic search token scoring local", MustContainId: "project-wiki-semantic-search-gap", MaxMs: 300),
        new("storage data folder json lifecycle", MustContainId: "project-wiki-data-folder-policy", MaxMs: 300),
        new("single host blazor app architecture deployment", MustContainId: "project-wiki-active-architecture", MaxMs: 300),
        new("scope boundary generalization friction", MustContainId: "project-wiki-scope-boundaries", MaxMs: 300),
        new("chat compact history titles model metadata local storage", MustContainId: "project-wiki-chat-local-storage-persistence", MaxMs: 300),
        new("chat stop button cancellation partial response model used", MustContainId: "project-wiki-chat-streaming-thinking", MaxMs: 300),
        new("github copilot auth model unavailable haiku before sonnet", MustContainId: "project-wiki-chat-agent-provider", MaxMs: 300),
        new("application intercepted wiki tool calls context pack get search", MustContainId: "project-wiki-chat-agent-provider", MaxMs: 300),
        new("ctrl v copied image paste html data url clipboard", MustContainId: "project-wiki-chat-image-attachments", MaxMs: 300),
    ];

    // ── Setup / Teardown ──────────────────────────────────────────────────────

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"memorysmith-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        _store = new FileMemoryStore(dataPath, new StorageDiagnostics());
        _service = ServiceFactory.Build(_store, _tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    // ── Lexical search probes ────────────────────────────────────────────────

    [TestCaseSource(nameof(LexicalProbes))]
    public async Task Lexical_Probe(SearchProbe probe)
    {
        var sw = Stopwatch.StartNew();
        var results = await _service.SearchAsync(
            new MemorySearchQuery(probe.Query, Status: null, Tags: "project-wiki", Limit: probe.Limit),
            CancellationToken.None);
        sw.Stop();

        AssertProbeResult(probe, results.Select(r => r.Id).ToList(), sw.ElapsedMilliseconds);
    }

    // ── Semantic search probes ────────────────────────────────────────────────

    [TestCaseSource(nameof(SemanticProbes))]
    public async Task Semantic_Probe(SearchProbe probe)
    {
        var sw = Stopwatch.StartNew();
        var results = await _service.SemanticSearchAsync(
            new SemanticMemorySearchQuery(probe.Query, Status: null, Tags: "project-wiki", Limit: probe.Limit),
            CancellationToken.None);
        sw.Stop();

        AssertProbeResult(probe, results.Select(r => r.Id).ToList(), sw.ElapsedMilliseconds);
    }

    // ── Hybrid search probes ──────────────────────────────────────────────────

    [TestCaseSource(nameof(HybridProbes))]
    public async Task Hybrid_Probe(SearchProbe probe)
    {
        var sw = Stopwatch.StartNew();
        var results = await _service.HybridSearchAsync(
            new HybridMemorySearchQuery(probe.Query, Status: null, Tags: "project-wiki", Limit: probe.Limit),
            CancellationToken.None);
        sw.Stop();

        AssertProbeResult(probe, results.Select(r => r.Id).ToList(), sw.ElapsedMilliseconds);
    }

    // ── Throughput / warm-path benchmark ─────────────────────────────────────

    [Test]
    public async Task Hybrid_ThroughputBaseline_20QueriesUnder5s()
    {
        var queries = new[]
        {
            "mcp context pack", "hybrid search rrf", "single host architecture",
            "storage data folder", "semantic scoring", "blazor ui source links",
            "validation test command", "scope boundaries", "search roadmap",
            "windows service", "lucene indexing", "source bundle retrieval",
            "find by source back mapping", "variable expansion path", "confidence scoring",
            "memory lifecycle state machine", "search quality relevance", "wiki test fixture",
            "event store audit log", "api extensions crud"
        };

        var sw = Stopwatch.StartNew();
        foreach (var q in queries)
        {
            await _service.HybridSearchAsync(
                new HybridMemorySearchQuery(q, Status: null, Tags: null, Limit: 5),
                CancellationToken.None);
        }
        sw.Stop();

        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(5000),
            $"20 hybrid queries took {sw.ElapsedMilliseconds} ms; expected under 5 000 ms.");
        TestContext.Out.WriteLine($"20 hybrid queries: {sw.ElapsedMilliseconds} ms ({sw.ElapsedMilliseconds / queries.Length} ms avg)");
    }

    // ── Source link tools ─────────────────────────────────────────────────────

    [Test]
    public async Task FindBySource_LocatesWikiRecordsWithMatchingSourceLinks()
    {
        // The fixture records for active-architecture have MemorySmithRepo source links.
        var matches = await _service.FindBySourceAsync(
            "Program.cs",
            resolveUri: null,
            CancellationToken.None);

        Assert.That(matches, Is.Not.Empty, "Expected at least one record with a Program.cs source link.");
        Assert.That(matches.Select(r => r.Id), Does.Contain("project-wiki-active-architecture"));
    }

    [Test]
    public async Task FindBySource_ReturnsEmptyForUnknownPattern()
    {
        var matches = await _service.FindBySourceAsync(
            "this-file-does-not-exist-anywhere.xyz",
            resolveUri: null,
            CancellationToken.None);

        Assert.That(matches, Is.Empty);
    }

    // ── MCP source_bundle and find_by_source via HTTP ─────────────────────────

    [Test]
    public async Task McpSourceBundleTool_ReturnsEntries_ForKnownRecord()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateHttpFactory(dataPath);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "source-bundle",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_source_bundle",
                Arguments = new
                {
                    Ids = "project-wiki-active-architecture",
                    Format = "json"
                }
            }
        }, JsonSerializerOptions.Web);

        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK));

        var text = await ExtractFirstToolTextAsync(response);
        // The active-architecture record has source links; expect JSON bundle output.
        using var doc = JsonDocument.Parse(text);
        Assert.That(doc.RootElement.GetProperty("sourceCount").GetInt32(), Is.GreaterThan(0));
        Assert.That(doc.RootElement.GetProperty("entries").EnumerateArray().First()
            .GetProperty("memoryId").GetString(), Is.EqualTo("project-wiki-active-architecture"));
    }

    [Test]
    public async Task McpFindBySourceTool_ReturnsMappedRecords()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateHttpFactory(dataPath);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "find-by-source",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_find_by_source",
                Arguments = new { Pattern = "Program.cs" }
            }
        }, JsonSerializerOptions.Web);

        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK));

        var text = await ExtractFirstToolTextAsync(response);
        using var doc = JsonDocument.Parse(text);
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();
        Assert.That(ids, Does.Contain("project-wiki-active-architecture"));
    }

    [Test]
    public async Task McpToolsList_IncludesSourceBundleAndFindBySource()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateHttpFactory(dataPath);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = 99,
            Method = "tools/list"
        }, JsonSerializerOptions.Web);

        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK));

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var toolNames = document.RootElement
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();

        Assert.That(toolNames, Does.Contain("memorysmith_source_bundle"));
        Assert.That(toolNames, Does.Contain("memorysmith_find_by_source"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AssertProbeResult(SearchProbe probe, List<string> ids, long elapsedMs)
    {
        Assert.Multiple(() =>
        {
            if (probe.TopId is not null)
                Assert.That(ids.FirstOrDefault(), Is.EqualTo(probe.TopId),
                    $"Query '{probe.Query}' — expected top result '{probe.TopId}', got [{string.Join(", ", ids.Take(3))}]");

            if (probe.MustContainId is not null)
                Assert.That(ids, Does.Contain(probe.MustContainId),
                    $"Query '{probe.Query}' — expected '{probe.MustContainId}' in results [{string.Join(", ", ids)}]");

            Assert.That(elapsedMs, Is.LessThanOrEqualTo(probe.MaxMs),
                $"Query '{probe.Query}' — latency {elapsedMs} ms exceeded {probe.MaxMs} ms threshold.");
        });
        TestContext.Out.WriteLine($"  [{elapsedMs,4} ms] {probe.Query}");
    }

    private WebApplicationFactory<Program> CreateHttpFactory(string memoryPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MemorySmith:DataPath"] = memoryPath,
                    ["MemorySmith:EventLogPath"] = Path.Combine(_tempRoot, "Events", "audit.log"),
                    ["MemorySmith:Maintenance:Enabled"] = "false"
                });
            });
        });

    private static async Task<string> ExtractFirstToolTextAsync(HttpResponseMessage response)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;
    }
}

/// <summary>A single search quality + latency probe for parameterized test runs.</summary>
public record SearchProbe(
    string Query,
    string? TopId = null,
    string? MustContainId = null,
    int MaxMs = 500,
    int Limit = 10)
{
    public override string ToString() => Query;
}

/// <summary>Builds a <see cref="MemoryApplicationService"/> directly against a <see cref="FileMemoryStore"/>.</summary>
internal static class ServiceFactory
{
    public static MemoryApplicationService Build(FileMemoryStore store, string tempRoot)
    {
        var eventStore = new FileEventStore(Path.Combine(tempRoot, "events.jsonl"));
        var index = new MemorySmith.Core.Indexing.MemoryIndex();
        foreach (var r in store.LoadAll()) index.Add(r);
        var options = Microsoft.Extensions.Options.Options.Create(new MemorySmithOptions());
        var telemetry = new BackgroundServiceTelemetryTracker();
        var publisher = new NoOpPublisher();
        return new MemoryApplicationService(store, eventStore, index, telemetry, publisher, options);
    }
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
