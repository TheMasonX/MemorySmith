using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MemorySmith.Tests;

[TestFixture]
public class SemanticToolQualityTests
{
    private string _tempRoot = null!;
    private FileMemoryStore _store = null!;
    private MemoryApplicationService _service = null!;

    private static readonly SearchQualityProbe[] SemanticProbes =
    [
        new("model context protocol tool calling", "project-wiki-mcp-integration", 6),
        new("context pack agent readiness knowledge base", "project-wiki-mcp-context-pack", 3),
        new("vector embeddings semantic gap local scoring", "project-wiki-semantic-search-gap", 3),
        new("source links file references path variables", "project-wiki-source-link-configuration-current", 3),
        new("blazor server UI single host deployment", "project-wiki-active-architecture", 5)
    ];

    private static readonly SearchQualityProbe[] HybridProbes =
    [
        new("hybrid search lucene rrf fusion", "project-wiki-hybrid-search-rrf", 1),
        new("mcp context pack format json markdown", "project-wiki-mcp-context-pack", 1),
        new("source links allowed roots max read bytes var resolver", "project-wiki-source-link-security-boundaries", 2),
        new("single host blazor app architecture deployment", "project-wiki-active-architecture", 3)
    ];

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"memorysmith-quality-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        _store = new FileMemoryStore(dataPath, new StorageDiagnostics());
        _service = ServiceFactory.Build(_store, _tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Test]
    public async Task SemanticSearch_ProjectWikiProbes_MeetRelevanceThresholds()
    {
        var reciprocalRanks = new List<double>();

        foreach (var probe in SemanticProbes)
        {
            var results = await _service.SemanticSearchAsync(
                new SemanticMemorySearchQuery(probe.Query, Tags: "project-wiki", Limit: 10),
                CancellationToken.None);

            var ids = results.Select(result => result.Id).ToList();
            var rank = ids.FindIndex(id => string.Equals(id, probe.ExpectedId, StringComparison.OrdinalIgnoreCase)) + 1;
            reciprocalRanks.Add(rank == 0 ? 0 : 1.0 / rank);

            Assert.Multiple(() =>
            {
                Assert.That(rank, Is.GreaterThan(0), $"Query '{probe.Query}' did not return expected id '{probe.ExpectedId}'. Results: {string.Join(", ", ids)}");
                Assert.That(rank, Is.LessThanOrEqualTo(probe.MaxRank), $"Query '{probe.Query}' ranked '{probe.ExpectedId}' at {rank}; expected <= {probe.MaxRank}.");
                Assert.That(results[rank - 1].Score, Is.GreaterThan(0));
                Assert.That(results[rank - 1].MatchReason, Is.Not.EqualTo("No semantic token overlap."));
                Assert.That(results[rank - 1].Snippet, Is.Not.Empty);
            });
        }

        Assert.That(reciprocalRanks.Average(), Is.GreaterThanOrEqualTo(0.55),
            $"Semantic MRR was {reciprocalRanks.Average():0.###}; expected >= 0.55.");
    }

    [Test]
    public async Task HybridSearch_ProjectWikiProbes_MeetRelevanceThresholds()
    {
        foreach (var probe in HybridProbes)
        {
            var results = await _service.HybridSearchAsync(
                new HybridMemorySearchQuery(probe.Query, Tags: "project-wiki", Limit: 10),
                CancellationToken.None);

            var ids = results.Select(result => result.Id).ToList();
            var rank = ids.FindIndex(id => string.Equals(id, probe.ExpectedId, StringComparison.OrdinalIgnoreCase)) + 1;

            Assert.Multiple(() =>
            {
                Assert.That(rank, Is.GreaterThan(0), $"Query '{probe.Query}' did not return expected id '{probe.ExpectedId}'. Results: {string.Join(", ", ids)}");
                Assert.That(rank, Is.LessThanOrEqualTo(probe.MaxRank), $"Query '{probe.Query}' ranked '{probe.ExpectedId}' at {rank}; expected <= {probe.MaxRank}.");
                Assert.That(results[rank - 1].Score, Is.GreaterThan(0));
                Assert.That(results[rank - 1].MatchReason, Does.Contain("RRF"));
                Assert.That(results[rank - 1].MatchReason, Does.Contain("lexical rank"));
                Assert.That(results[rank - 1].MatchReason, Does.Contain("semantic rank"));
            });
        }
    }

    [Test]
    public async Task SearchQuality_IsDeterministicAcrossRepeatedRuns()
    {
        var first = await _service.HybridSearchAsync(
            new HybridMemorySearchQuery("source links file references path variables", Tags: "project-wiki", Limit: 8),
            CancellationToken.None);
        var second = await _service.HybridSearchAsync(
            new HybridMemorySearchQuery("source links file references path variables", Tags: "project-wiki", Limit: 8),
            CancellationToken.None);

        Assert.That(second.Select(result => result.Id), Is.EqualTo(first.Select(result => result.Id)));
    }

    [Test]
    public async Task McpTools_ExposeSchemasAndUsefulOutputs()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath);
        using var client = factory.CreateClient();

        var toolsList = await PostJsonRpcAsync(client, new
        {
            JsonRpc = "2.0",
            Id = "tools",
            Method = "tools/list"
        });

        var tools = toolsList.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
        var toolNames = tools.Select(tool => tool.GetProperty("name").GetString()).ToList();
        var sourceBundleSchema = tools.Single(tool => tool.GetProperty("name").GetString() == "memorysmith_source_bundle")
            .GetProperty("inputSchema")
            .GetProperty("properties")
            .GetProperty("maxFileBytes")
            .GetProperty("description")
            .GetString();

        Assert.Multiple(() =>
        {
            Assert.That(toolNames, Does.Contain("memorysmith_semantic_search"));
            Assert.That(toolNames, Does.Contain("memorysmith_hybrid_search"));
            Assert.That(toolNames, Does.Contain("memorysmith_context_pack"));
            Assert.That(toolNames, Does.Contain("memorysmith_source_bundle"));
            Assert.That(sourceBundleSchema, Does.Contain("clamped"));
        });

        var semanticText = await CallToolTextAsync(client, "memorysmith_semantic_search", new
        {
            Query = "model context protocol tool calling",
            Tags = "project-wiki",
            Limit = 5
        });
        Assert.Multiple(() =>
        {
            Assert.That(semanticText, Does.Contain("project-wiki-mcp-context-pack"));
            Assert.That(semanticText, Does.Contain("Score:"));
            Assert.That(semanticText, Does.Contain("Match:"));
        });

        var contextPackText = await CallToolTextAsync(client, "memorysmith_context_pack", new
        {
            Query = "source links file references path variables",
            Tags = "project-wiki",
            Limit = 1,
            ReferenceDepth = 1,
            Format = "json"
        });
        using var contextPack = JsonDocument.Parse(contextPackText);
        var contextRecordIds = contextPack.RootElement.GetProperty("records").EnumerateArray()
            .Select(record => record.GetProperty("id").GetString())
            .ToList();
        Assert.That(contextRecordIds, Does.Contain("project-wiki-source-links-feature"));

        var sourceBundleText = await CallToolTextAsync(client, "memorysmith_source_bundle", new
        {
            Ids = "project-wiki-source-links-feature",
            MaxFileBytes = 500,
            Format = "json"
        });
        using var sourceBundle = JsonDocument.Parse(sourceBundleText);
        Assert.Multiple(() =>
        {
            Assert.That(sourceBundle.RootElement.GetProperty("sourceCount").GetInt32(), Is.GreaterThan(0));
            Assert.That(sourceBundle.RootElement.GetProperty("entries")[0].GetProperty("exists").GetBoolean(), Is.True);
        });

        var findBySourceText = await CallToolTextAsync(client, "memorysmith_find_by_source", new { Pattern = "VarResolver.cs" });
        Assert.That(findBySourceText, Does.Contain("project-wiki-source-links-feature"));
    }

    [Test]
    public async Task McpBatch_MixesNotificationsAndRequestsDeterministically()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath);
        using var client = factory.CreateClient();

        using var response = await PostJsonRpcAsync(client, new object[]
        {
            new { JsonRpc = "2.0", Method = "initialized" },
            new { JsonRpc = "2.0", Id = "tools", Method = "tools/list" }
        });

        Assert.Multiple(() =>
        {
            Assert.That(response.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Array));
            Assert.That(response.RootElement.GetArrayLength(), Is.EqualTo(1));
            Assert.That(response.RootElement[0].GetProperty("id").GetString(), Is.EqualTo("tools"));
        });
    }

    private WebApplicationFactory<Program> CreateFactory(string memoryPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            var varsPath = Path.Combine(_tempRoot, "vars.json");
            var repoRoot = FindRepositoryRoot();
            var repoRootWithSeparator = Path.TrimEndingDirectorySeparator(repoRoot) + Path.DirectorySeparatorChar;
            File.WriteAllText(
                varsPath,
                JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["MemorySmithRepo"] = repoRootWithSeparator
                }));

            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MemorySmith:DataPath"] = memoryPath,
                    ["MemorySmith:EventLogPath"] = Path.Combine(_tempRoot, "Events", "quality-audit.log"),
                    ["MemorySmith:VarsPath"] = varsPath,
                    ["MemorySmith:Maintenance:Enabled"] = "false"
                });
            });
        });

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

    private static async Task<string> CallToolTextAsync(HttpClient client, string toolName, object arguments)
    {
        using var document = await PostJsonRpcAsync(client, new
        {
            JsonRpc = "2.0",
            Id = toolName,
            Method = "tools/call",
            Params = new
            {
                Name = toolName,
                Arguments = arguments
            }
        });

        return document.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;
    }

    private static async Task<JsonDocument> PostJsonRpcAsync(HttpClient client, object payload)
    {
        var response = await client.PostAsJsonAsync("/mcp", payload, JsonSerializerOptions.Web);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    }
}

public sealed record SearchQualityProbe(string Query, string ExpectedId, int MaxRank);
