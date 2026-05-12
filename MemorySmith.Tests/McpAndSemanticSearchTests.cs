using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MemorySmith.Tests;

[TestFixture]
public class McpAndSemanticSearchTests
{
    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"memorysmith-mcp-search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
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
    public async Task SemanticSearchApi_RanksProjectWikiConceptMatches()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/memories/search/semantic", new
        {
            Query = "model context protocol search integration",
            Tags = "project-wiki",
            Limit = 5
        });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var results = document.RootElement.EnumerateArray().ToList();

        Assert.That(results, Is.Not.Empty);
        Assert.That(results[0].GetProperty("score").GetDouble(), Is.GreaterThan(0));
        Assert.That(results[0].GetProperty("matchReason").GetString(), Is.Not.Empty);
        Assert.That(results.Select(result => result.GetProperty("id").GetString()), Does.Contain("project-wiki-mcp-integration"));
        Assert.That(results[0].GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()), Does.Contain("mcp"));
    }

    [Test]
    public async Task HybridSearchApi_ReturnsRrfRankedProjectWikiMatches()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/memories/search/hybrid", new
        {
            Query = "lucene vector rrf hybrid search",
            Tags = "project-wiki",
            Limit = 5
        });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var results = document.RootElement.EnumerateArray().ToList();

        Assert.That(results, Is.Not.Empty);
        Assert.Multiple(() =>
        {
            Assert.That(results[0].GetProperty("id").GetString(), Is.EqualTo("project-wiki-hybrid-search-rrf"));
            Assert.That(results[0].GetProperty("score").GetDouble(), Is.GreaterThan(0));
            Assert.That(results[0].GetProperty("matchReason").GetString(), Does.Contain("RRF"));
        });
    }

    [Test]
    public async Task McpToolsList_ExposesWikiSearchTools()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = 1,
            Method = "tools/list"
        }, JsonSerializerOptions.Web);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var toolNames = document.RootElement
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToList();

        Assert.That(toolNames, Does.Contain("memorysmith_search"));
        Assert.That(toolNames, Does.Contain("memorysmith_semantic_search"));
        Assert.That(toolNames, Does.Contain("memorysmith_hybrid_search"));
        Assert.That(toolNames, Does.Contain("memorysmith_get"));
    }

    [Test]
    public async Task McpHybridSearchTool_ReturnsProjectWikiRecord()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "hybrid-search",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_hybrid_search",
                Arguments = new
                {
                    Query = "lucene vector rrf hybrid search",
                    Tags = "project-wiki",
                    Limit = 5
                }
            }
        }, JsonSerializerOptions.Web);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var text = await ExtractFirstToolTextAsync(response);
        Assert.That(text, Does.Contain("project-wiki-hybrid-search-rrf"));
        Assert.That(text, Does.Contain("RRF"));
    }

    [Test]
    public async Task McpSemanticSearchTool_ReturnsProjectWikiRecord()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "semantic-search",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_semantic_search",
                Arguments = new
                {
                    Query = "semantic search embeddings",
                    Tags = "project-wiki",
                    Limit = 5
                }
            }
        }, JsonSerializerOptions.Web);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var text = await ExtractFirstToolTextAsync(response);
        Assert.That(text, Does.Contain("project-wiki-semantic-search-gap"));
        Assert.That(text, Does.Contain("Score"));
    }

    [Test]
    public async Task McpGetTool_ReturnsSingleWikiRecord()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = 3,
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_get",
                Arguments = new
                {
                    Id = "project-wiki-search-roadmap"
                }
            }
        }, JsonSerializerOptions.Web);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var text = await ExtractFirstToolTextAsync(response);
        Assert.That(text, Does.Contain("Search Tools Roadmap"));
        Assert.That(text, Does.Contain("semantic search"));
    }

    private WebApplicationFactory<Program> CreateFactory(string memoryPath) =>
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