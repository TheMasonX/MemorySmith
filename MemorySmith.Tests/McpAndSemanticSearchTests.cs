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
        Assert.That(toolNames, Does.Contain("memorysmith_context_pack"));
        Assert.That(toolNames, Does.Contain("memorysmith_get"));

        var contextPackTool = document.RootElement
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "memorysmith_context_pack");
        var contextPackProperties = contextPackTool.GetProperty("inputSchema").GetProperty("properties");
        Assert.Multiple(() =>
        {
            Assert.That(contextPackProperties.TryGetProperty("maxRecords", out _), Is.True);
            Assert.That(contextPackProperties.TryGetProperty("format", out _), Is.True);
        });
    }

    [Test]
    public async Task McpContextPackTool_ReturnsHybridResultsWithLinkedContext()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "context-pack",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_context_pack",
                Arguments = new
                {
                    Query = "useful MCP context pack larger knowledge base",
                    Tags = "project-wiki",
                    Limit = 2,
                    ReferenceDepth = 1,
                    MaxContentChars = 900
                }
            }
        }, JsonSerializerOptions.Web);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var text = await ExtractFirstToolTextAsync(response);
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("Context Pack"));
            Assert.That(text, Does.Contain("project-wiki"));
            Assert.That(text, Does.Contain("Relationship"));
            Assert.That(text, Does.Contain("RRF"));
        });
    }

    [Test]
    public async Task McpContextPackTool_AcceptsExplicitIds()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "context-pack-by-id",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_context_pack",
                Arguments = new
                {
                    Ids = "project-wiki-mcp-context-pack",
                    ReferenceDepth = 1,
                    MaxContentChars = 600
                }
            }
        }, JsonSerializerOptions.Web);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var text = await ExtractFirstToolTextAsync(response);
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("project-wiki-mcp-context-pack"));
            Assert.That(text, Does.Contain("Explicit root id"));
            Assert.That(text, Does.Contain("reference of project-wiki-mcp-context-pack"));
        });
    }

    [Test]
    public async Task McpContextPackTool_ReportsMissingExplicitIds()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "context-pack-missing-id",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_context_pack",
                Arguments = new
                {
                    Ids = "project-wiki-mcp-context-pack,missing-project-memory",
                    ReferenceDepth = 0
                }
            }
        }, JsonSerializerOptions.Web);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var text = await ExtractFirstToolTextAsync(response);
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("Warnings"));
            Assert.That(text, Does.Contain("missing-project-memory"));
        });
    }

    [Test]
    public async Task McpContextPackTool_HonorsMaxRecordsBudget()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "context-pack-budget",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_context_pack",
                Arguments = new
                {
                    Ids = "project-wiki-mcp-context-pack",
                    ReferenceDepth = 1,
                    MaxRecords = 2,
                    MaxContentChars = 400
                }
            }
        }, JsonSerializerOptions.Web);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var text = await ExtractFirstToolTextAsync(response);
        var recordCount = text.Split("## ").Length - 1;
        Assert.Multiple(() =>
        {
            Assert.That(recordCount, Is.EqualTo(2));
            Assert.That(text, Does.Contain("maxRecords 2"));
        });
    }

    [Test]
    public async Task McpContextPackTool_ReturnsJsonWhenRequested()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "context-pack-json",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_context_pack",
                Arguments = new
                {
                    Ids = "project-wiki-mcp-context-pack,missing-project-memory",
                    ReferenceDepth = 0,
                    MaxRecords = 3,
                    Format = "json"
                }
            }
        }, JsonSerializerOptions.Web);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var text = await ExtractFirstToolTextAsync(response);
        using var document = JsonDocument.Parse(text);
        var records = document.RootElement.GetProperty("records").EnumerateArray().ToList();
        var warnings = document.RootElement.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(document.RootElement.GetProperty("schemaVersion").GetString(), Is.EqualTo("memorysmith.context-pack.v1"));
            Assert.That(document.RootElement.GetProperty("query").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(records.Single().GetProperty("id").GetString(), Is.EqualTo("project-wiki-mcp-context-pack"));
            Assert.That(records.Single().TryGetProperty("diagnostics", out _), Is.True);
            Assert.That(warnings, Has.Some.Contains("missing-project-memory"));
        });
    }

    [Test]
    public async Task McpContextPackTool_ReturnsPurposeBuiltFixtureGraphAsJson()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "context-pack-fixture-json",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_context_pack",
                Arguments = new
                {
                    Ids = "project-wiki-test-fixture-context-root",
                    ReferenceDepth = 1,
                    IncludeBacklinks = true,
                    MaxRecords = 10,
                    MaxContentChars = 500,
                    Format = "json"
                }
            }
        }, JsonSerializerOptions.Web);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var text = await ExtractFirstToolTextAsync(response);
        using var document = JsonDocument.Parse(text);
        var records = document.RootElement.GetProperty("records").EnumerateArray().ToList();
        var relationships = records.ToDictionary(
            record => record.GetProperty("id").GetString()!,
            record => record.GetProperty("relationship").GetString(),
            StringComparer.OrdinalIgnoreCase);

        Assert.Multiple(() =>
        {
            Assert.That(relationships.Keys, Does.Contain("project-wiki-test-fixture-context-root"));
            Assert.That(relationships["project-wiki-test-fixture-reference-child"], Is.EqualTo("reference of project-wiki-test-fixture-context-root"));
            Assert.That(relationships["project-wiki-test-fixture-conflict-note"], Is.EqualTo("conflict of project-wiki-test-fixture-context-root"));
            Assert.That(relationships["project-wiki-test-fixture-backlink-source"], Is.EqualTo("references project-wiki-test-fixture-context-root"));
            Assert.That(document.RootElement.GetProperty("warnings").EnumerateArray(), Is.Empty);
        });
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
    public async Task McpSearchTool_WithJsonFormat_ReturnsRetrievalEnvelope()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "lexical-search-json",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_search",
                Arguments = new
                {
                    Query = "model context protocol search integration",
                    Tags = "project-wiki",
                    Limit = 5,
                    Format = "json"
                }
            }
        }, JsonSerializerOptions.Web);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var text = await ExtractFirstToolTextAsync(response);
        using var document = JsonDocument.Parse(text);
        var results = document.RootElement.GetProperty("results").EnumerateArray().ToList();

        Assert.Multiple(() =>
        {
            Assert.That(document.RootElement.GetProperty("schemaVersion").GetString(), Is.EqualTo("memorysmith.retrieval-results.v1"));
            Assert.That(document.RootElement.GetProperty("mode").GetString(), Is.EqualTo("lexical"));
            Assert.That(document.RootElement.GetProperty("provider").GetProperty("kind").GetString(), Is.EqualTo("lexical"));
            Assert.That(results, Is.Not.Empty);
            Assert.That(results[0].TryGetProperty("diagnostics", out _), Is.True);
            Assert.That(document.RootElement.TryGetProperty("warnings", out _), Is.True);
        });
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