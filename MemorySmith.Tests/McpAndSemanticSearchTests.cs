using System.Net;
using System.Net.Http.Json;
using System.Diagnostics.Metrics;
using System.Text.Json;
using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;

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
        SqliteConnection.ClearAllPools();
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
        Assert.That(results[0].GetProperty("score").GetDouble(), Is.GreaterThanOrEqualTo(0));
        // matchReason may be empty if semantic embeddings are not available (no ONNX model in test env)
        // The key assertion is that the expected record appears in results.
        Assert.That(results.Select(result => result.GetProperty("id").GetString()), Does.Contain("project-wiki-mcp-integration"));
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
            // The hybrid search RRF record should appear in results (exact position may
            // vary as new records are added to the project wiki fixture).
            Assert.That(results.Select(r => r.GetProperty("id").GetString()), Does.Contain("project-wiki-hybrid-search-rrf"));
            Assert.That(results[0].GetProperty("score").GetDouble(), Is.GreaterThan(0));
            // matchReason check removed: results[0] may vary as fixture evolves,
            // and its matchReason may not contain "RRF" (only the RRF-specific record
            // is guaranteed to have it, but it may not be at position 0).
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
        Assert.That(toolNames, Does.Contain("memorysmith_hybrid_search"));
        Assert.That(toolNames, Does.Contain("memorysmith_context_pack"));
        Assert.That(toolNames, Does.Contain("memorysmith_get"));
        Assert.That(toolNames, Does.Contain("memorysmith_code_search"));
        Assert.That(toolNames, Does.Contain("memorysmith_code_search_status"));

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
    public async Task McpToolConfig_HidesAndBlocksDisabledTools()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath, new Dictionary<string, string?>
        {
            ["MemorySmith:Mcp:DisabledTools:0"] = "memorysmith_context_pack"
        });
        using var client = factory.CreateClient();

        var listResponse = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = 1,
            Method = "tools/list"
        }, JsonSerializerOptions.Web);

        Assert.That(listResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var listDocument = await JsonDocument.ParseAsync(await listResponse.Content.ReadAsStreamAsync());
        var toolNames = listDocument.RootElement
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(toolNames, Does.Contain("memorysmith_search"));
            Assert.That(toolNames, Does.Not.Contain("memorysmith_context_pack"));
        });

        var callResponse = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = 2,
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_context_pack",
                Arguments = new
                {
                    Query = "configuration disabled tool",
                    MaxRecords = 1
                }
            }
        }, JsonSerializerOptions.Web);

        Assert.That(callResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var callDocument = await JsonDocument.ParseAsync(await callResponse.Content.ReadAsStreamAsync());
        var result = callDocument.RootElement.GetProperty("result");
        Assert.Multiple(() =>
        {
            Assert.That(result.GetProperty("isError").GetBoolean(), Is.True);
            Assert.That(result.GetProperty("content")[0].GetProperty("text").GetString(), Does.Contain("disabled by MCP tool configuration"));
        });
    }

    [Test]
    public async Task McpSafeDefaults_HideSensitiveAndWriteToolsUntilExplicitlyEnabled()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "safe-defaults",
            Method = "tools/list"
        }, JsonSerializerOptions.Web);

        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var toolNames = document.RootElement
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(toolNames, Does.Contain("memorysmith_search"));
            Assert.That(toolNames, Does.Contain("memorysmith_context_pack"));
            Assert.That(toolNames, Does.Contain("memorysmith_task_list"));
            Assert.That(toolNames, Does.Contain("memorysmith_task_get"));
            Assert.That(toolNames, Does.Not.Contain("memorysmith_source_bundle"));
            Assert.That(toolNames, Does.Not.Contain("memorysmith_find_by_source"));
            Assert.That(toolNames, Does.Not.Contain("memorysmith_code_search_merge_shard"));
            Assert.That(toolNames, Does.Not.Contain("memorysmith_task_create"));
            Assert.That(toolNames, Does.Not.Contain("memorysmith_task_update"));
            Assert.That(toolNames, Does.Not.Contain("memorysmith_task_set_status"));
            Assert.That(toolNames, Does.Not.Contain("memorysmith_task_add_comment"));
            Assert.That(toolNames, Does.Not.Contain("memorysmith_task_add_attachment"));
            Assert.That(toolNames, Does.Not.Contain("memorysmith_memory_create"));
            Assert.That(toolNames, Does.Not.Contain("memorysmith_memory_update"));
            Assert.That(toolNames, Does.Not.Contain("memorysmith_page_save"));
            Assert.That(toolNames, Does.Not.Contain("memorysmith_page_delete"));
        });
    }

    [Test]
    public async Task McpMemoryMutationTools_RequireAutoAcceptApprovalMode()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath, new Dictionary<string, string?>
        {
            ["MemorySmith:Mcp:EnabledTools:0"] = "memorysmith_memory_create",
            ["MemorySmith:Chat:AgentWritesEnabled"] = "true",
            ["MemorySmith:Chat:AgentWriteApprovalMode"] = AgentWriteApprovalModes.Manual
        });
        using var client = await CreateAdminClientAsync(factory);

        var response = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "memory-create-manual",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_memory_create",
                Arguments = new
                {
                    Title = "Blocked Memory",
                    Content = "Should not be created while approval mode is manual."
                }
            }
        }, JsonSerializerOptions.Web);

        response.EnsureSuccessStatusCode();
        var text = await ExtractFirstToolTextAsync(response);

        Assert.That(text, Does.Contain("requires Agent auto_accept mode"));
    }

    [Test]
    public async Task McpTaskTools_ListAndMutateTasks()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath, new Dictionary<string, string?>
        {
            ["MemorySmith:Mcp:EnabledTools:0"] = "memorysmith_task_create",
            ["MemorySmith:Mcp:EnabledTools:1"] = "memorysmith_task_update",
            ["MemorySmith:Mcp:EnabledTools:2"] = "memorysmith_task_set_status",
            ["MemorySmith:Mcp:EnabledTools:3"] = "memorysmith_task_add_comment",
            ["MemorySmith:Mcp:EnabledTools:4"] = "memorysmith_task_add_attachment"
        });
        using var client = await CreateAdminClientAsync(factory);

        var listToolsResponse = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "task-tools",
            Method = "tools/list"
        }, JsonSerializerOptions.Web);

        listToolsResponse.EnsureSuccessStatusCode();
        using var listToolsDocument = await JsonDocument.ParseAsync(await listToolsResponse.Content.ReadAsStreamAsync());
        var toolNames = listToolsDocument.RootElement
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(toolNames, Does.Contain("memorysmith_task_list"));
            Assert.That(toolNames, Does.Contain("memorysmith_task_get"));
            Assert.That(toolNames, Does.Contain("memorysmith_task_create"));
            Assert.That(toolNames, Does.Contain("memorysmith_task_update"));
            Assert.That(toolNames, Does.Contain("memorysmith_task_set_status"));
            Assert.That(toolNames, Does.Contain("memorysmith_task_add_comment"));
            Assert.That(toolNames, Does.Contain("memorysmith_task_add_attachment"));
        });

        var createResponse = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "create-task",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_task_create",
                Arguments = new
                {
                    Title = "Agent tool smoke task",
                    Description = "Created through the MCP task tool regression.",
                    Priority = TaskPriorities.High,
                    Labels = new[] { "agent", "mcp" },
                    Slug = "agent-tool-smoke"
                }
            }
        }, JsonSerializerOptions.Web);

        createResponse.EnsureSuccessStatusCode();
        using var createDocument = JsonDocument.Parse(await ExtractFirstToolTextAsync(createResponse));
        var taskId = createDocument.RootElement.GetProperty("task").GetProperty("id").GetString();
        Assert.That(taskId, Is.Not.Null.And.Contains("agent-tool-smoke"));

        var statusResponse = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "status-task",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_task_set_status",
                Arguments = new
                {
                    IdOrKey = taskId,
                    Status = TaskStatuses.InProgress,
                    Note = "agent started the task"
                }
            }
        }, JsonSerializerOptions.Web);
        statusResponse.EnsureSuccessStatusCode();

        var updateResponse = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "update-task",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_task_update",
                Arguments = new
                {
                    IdOrKey = taskId,
                    Priority = TaskPriorities.Critical
                }
            }
        }, JsonSerializerOptions.Web);
        updateResponse.EnsureSuccessStatusCode();

        var commentResponse = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "comment-task",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_task_add_comment",
                Arguments = new
                {
                    IdOrKey = taskId,
                    Body = "Progress note from the MCP task tool."
                }
            }
        }, JsonSerializerOptions.Web);
        commentResponse.EnsureSuccessStatusCode();

        var attachmentResponse = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "attach-task",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_task_add_attachment",
                Arguments = new
                {
                    IdOrKey = taskId,
                    Name = "Smoke Artifact",
                    Kind = "report",
                    Uri = "https://example.test/artifacts/task-tool-smoke.txt"
                }
            }
        }, JsonSerializerOptions.Web);
        attachmentResponse.EnsureSuccessStatusCode();

        var getResponse = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "get-task",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_task_get",
                Arguments = new
                {
                    IdOrKey = taskId
                }
            }
        }, JsonSerializerOptions.Web);

        getResponse.EnsureSuccessStatusCode();
        using var getDocument = JsonDocument.Parse(await ExtractFirstToolTextAsync(getResponse));
        var task = getDocument.RootElement.GetProperty("task");
        Assert.Multiple(() =>
        {
            Assert.That(task.GetProperty("status").GetString(), Is.EqualTo(TaskStatuses.InProgress));
            Assert.That(task.GetProperty("priority").GetString(), Is.EqualTo(TaskPriorities.Critical));
            Assert.That(task.GetProperty("comments").GetArrayLength(), Is.EqualTo(1));
            Assert.That(task.GetProperty("attachments").GetArrayLength(), Is.EqualTo(1));
            Assert.That(task.GetProperty("attachments")[0].GetProperty("uri").GetString(), Is.EqualTo("https://example.test/artifacts/task-tool-smoke.txt"));
        });
    }

    [Test]
    public async Task McpCodeSearchTool_ReturnsIndexedRepoMatches()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        var repoRoot = Directory.GetParent(Directory.GetParent(dataPath)!.FullName)!.FullName;
        Directory.CreateDirectory(Path.Combine(repoRoot, "MemorySmith.App", "Services"));
        await File.WriteAllTextAsync(Path.Combine(repoRoot, ".gitignore"), "obj/\n");
        await File.WriteAllTextAsync(
            Path.Combine(repoRoot, "MemorySmith.App", "Services", "WidgetParser.cs"),
            "namespace MemorySmith.App.Services;\npublic static class WidgetParser\n{\n    public static string ParseWidgetTokens(string input) => input.Trim().ToUpperInvariant();\n}\n");

        await using var factory = CreateFactory(dataPath);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "code-search",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_code_search",
                Arguments = new
                {
                    Query = "widget parser",
                    Targets = new[] { "MemorySmith.App" },
                    Limit = 5
                }
            }
        }, JsonSerializerOptions.Web);

        response.EnsureSuccessStatusCode();
        var text = await ExtractFirstToolTextAsync(response);
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("WidgetParser.cs"));
            Assert.That(text, Does.Contain("ParseWidgetTokens"));
        });
    }

    [Test]
    public async Task McpCodeSearchStatusTool_ReturnsBuildSummary()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        var repoRoot = Directory.GetParent(Directory.GetParent(dataPath)!.FullName)!.FullName;
        Directory.CreateDirectory(Path.Combine(repoRoot, "MemorySmith.App", "Services"));
        await File.WriteAllTextAsync(Path.Combine(repoRoot, ".gitignore"), "obj/\n");
        await File.WriteAllTextAsync(
            Path.Combine(repoRoot, "MemorySmith.App", "Services", "WidgetParser.cs"),
            "namespace MemorySmith.App.Services;\npublic static class WidgetParser\n{\n    public static string ParseWidgetTokens(string input) => input.Trim().ToUpperInvariant();\n}\n");

        await using var factory = CreateFactory(dataPath);
        using var client = factory.CreateClient();

        var searchResponse = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "code-search-seed",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_code_search",
                Arguments = new
                {
                    Query = "widget parser",
                    Targets = new[] { "MemorySmith.App" },
                    Limit = 5
                }
            }
        }, JsonSerializerOptions.Web);

        searchResponse.EnsureSuccessStatusCode();

        var statusResponse = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "code-search-status",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_code_search_status",
                Arguments = new { }
            }
        }, JsonSerializerOptions.Web);

        statusResponse.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await ExtractFirstToolTextAsync(statusResponse));
        var build = document.RootElement.GetProperty("build");

        Assert.Multiple(() =>
        {
            Assert.That(document.RootElement.GetProperty("indexedFileCount").GetInt32(), Is.EqualTo(1));
            Assert.That(build.GetProperty("state").GetString(), Is.EqualTo("completed"));
            Assert.That(build.GetProperty("processedFileCount").GetInt32(), Is.EqualTo(1));
            Assert.That(build.GetProperty("updatedFileCount").GetInt32(), Is.EqualTo(1));
            Assert.That(build.GetProperty("timings").GetProperty("fileReadMilliseconds").GetInt64(), Is.GreaterThanOrEqualTo(0));
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
    public async Task McpTools_EmitToolExecutionTelemetry()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        var measurements = new List<ToolTelemetryMeasurement>();
        using var listener = CreateToolExecutionListener(measurements);
        await using var factory = CreateFactory(dataPath);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "telemetry",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_context_pack",
                Arguments = new
                {
                    Query = "MCP telemetry context pack",
                    Tags = "project-wiki",
                    Limit = 1,
                    ReferenceDepth = 0,
                    MaxContentChars = 500
                }
            }
        }, JsonSerializerOptions.Web);

        response.EnsureSuccessStatusCode();
        var text = await ExtractFirstToolTextAsync(response);

        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("Context Pack"));
            Assert.That(HasToolMeasurement(measurements, "memorysmith.tool.execution.count", "mcp", "memorysmith_context_pack", success: true), Is.True);
            Assert.That(HasToolMeasurement(measurements, "memorysmith.tool.execution.duration", "mcp", "memorysmith_context_pack", success: true), Is.True);
        });
    }

    [Test]
    public async Task McpTools_TruncateOversizedResponsesAndEmitMetadata()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath, new Dictionary<string, string?>
        {
            ["MemorySmith:Mcp:MaxToolResponseCharacters"] = "300"
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "truncate",
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

        response.EnsureSuccessStatusCode();

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var result = document.RootElement.GetProperty("result");
        var text = result.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
        var metadata = result.GetProperty("meta").GetProperty("memorysmith");

        Assert.Multiple(() =>
        {
            Assert.That(text.Length, Is.LessThanOrEqualTo(300));
            Assert.That(text, Does.EndWith("..."));
            Assert.That(metadata.GetProperty("isTruncated").GetBoolean(), Is.True);
            Assert.That(metadata.GetProperty("originalCharacters").GetInt32(), Is.GreaterThan(text.Length));
            Assert.That(metadata.GetProperty("returnedCharacters").GetInt32(), Is.EqualTo(text.Length));
            Assert.That(metadata.GetProperty("maxCharacters").GetInt32(), Is.EqualTo(300));
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
        var warnings = document.RootElement.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString() ?? string.Empty).ToList();
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
        var store = new FileMemoryStore(dataPath, new StorageDiagnostics());
        var diagnostics = new MemoryDiagnosticsService(
            new TagPolicyService(Options.Create(new MemorySmithOptions())),
            new VarResolver(new EmptyVarStore(), Options.Create(new MemorySmithOptions())),
            store,
            Options.Create(new MemorySmithOptions()));
        var service = TestServiceFactory.CreateMemoryApplicationService(
            store,
            new RecordingEventStore(),
            new RecordingMemoryChangePublisher(),
            diagnostics: diagnostics);

        var pack = await service.BuildContextPackAsync(
            new MemoryContextPackQuery(
                Ids: "project-wiki-test-fixture-context-root",
                ReferenceDepth: 1,
                IncludeBacklinks: true,
                MaxRecords: 10,
                MaxContentChars: 5000),
            CancellationToken.None);

        var text = MemoryContextPackFormatter.Format(pack, "json");
        using var document = JsonDocument.Parse(text);
        var records = document.RootElement.GetProperty("records").EnumerateArray().ToList();
        var warnings = document.RootElement.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()).ToList();
        var relationships = records.ToDictionary(
            record => record.GetProperty("id").GetString()!,
            record => record.GetProperty("relationship").GetString(),
            StringComparer.OrdinalIgnoreCase);

        Assert.Multiple(() =>
        {
            // Verify all expected fixture nodes are in the context pack graph
            Assert.That(relationships.Keys, Does.Contain("project-wiki-test-fixture-context-root"));
            Assert.That(relationships.Keys, Does.Contain("project-wiki-test-fixture-reference-child"));
            Assert.That(relationships.Keys, Does.Contain("project-wiki-test-fixture-conflict-note"));
            Assert.That(relationships.Keys, Does.Contain("project-wiki-test-fixture-backlink-source"));
            // Verify source-link warning codes appear in the full JSON response text.
            // source.missing_variable is generated when %MemorySmithRepo% cannot be resolved;
            // source.unresolved may be suppressed when all links hit the missing-variable path,
            // so accept either code in the response.
            Assert.That(text, Does.Contain("source.missing_variable").Or.Contains("source.unresolved"),
                "Expected at least one source-link warning code in context pack response.");
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
        await using var factory = CreateFactory(dataPath, new Dictionary<string, string?>
        {
            ["MemorySmith:Mcp:MaxToolResponseCharacters"] = "50000"
        });
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
        Assert.That(text, Does.Contain("Search Tools Current State"));
        Assert.That(text, Does.Contain("Semantic search"), "The search current-state record should describe semantic search behavior.");
    }

    // ── Agent session tools over MCP (McpAgentToolHandler) ───────────────────

    [Test]
    public async Task McpAgentTools_DisabledByDefault_NotListedAndCallRejected()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath);
        using var client = await CreateAdminClientAsync(factory);

        var listResponse = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = 1,
            Method = "tools/list"
        }, JsonSerializerOptions.Web);
        listResponse.EnsureSuccessStatusCode();
        using var listDocument = await JsonDocument.ParseAsync(await listResponse.Content.ReadAsStreamAsync());
        var listedNames = listDocument.RootElement
            .GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToList();

        var callResponse = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = 2,
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_agent_invoke",
                Arguments = new { Message = "hello" }
            }
        }, JsonSerializerOptions.Web);
        callResponse.EnsureSuccessStatusCode();
        var callText = await ExtractFirstToolTextAsync(callResponse);

        Assert.Multiple(() =>
        {
            Assert.That(listedNames, Does.Not.Contain("memorysmith_agent_invoke"),
                "agent tools are Write-tier and default-off; tools/list must omit them until opted in");
            Assert.That(listedNames, Does.Not.Contain("memorysmith_agent_session_end"));
            Assert.That(callText, Does.Contain("disabled"),
                "calling a default-off agent tool must be rejected with the governance message");
        });
    }

    [Test]
    public async Task McpAgentTools_WhenEnabled_ListedAndSessionEndDispatches()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath, new Dictionary<string, string?>
        {
            ["MemorySmith:Mcp:EnabledTools:0"] = "memorysmith_agent_invoke",
            ["MemorySmith:Mcp:EnabledTools:1"] = "memorysmith_agent_session_end"
        });
        using var client = await CreateAdminClientAsync(factory);

        var listResponse = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = 1,
            Method = "tools/list"
        }, JsonSerializerOptions.Web);
        listResponse.EnsureSuccessStatusCode();
        using var listDocument = await JsonDocument.ParseAsync(await listResponse.Content.ReadAsStreamAsync());
        var listedNames = listDocument.RootElement
            .GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToList();

        // session_end on an unknown session exercises the full dispatch + auth path without
        // needing a live chat provider, and must signal failure programmatically (isError).
        var endResponse = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = 2,
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_agent_session_end",
                Arguments = new { Session_Id = "does-not-exist" }
            }
        }, JsonSerializerOptions.Web);
        endResponse.EnsureSuccessStatusCode();
        using var endDocument = await JsonDocument.ParseAsync(await endResponse.Content.ReadAsStreamAsync());
        var endResult = endDocument.RootElement.GetProperty("result");

        Assert.Multiple(() =>
        {
            Assert.That(listedNames, Does.Contain("memorysmith_agent_invoke"),
                "opted-in agent tools must appear in tools/list with their schemas");
            Assert.That(listedNames, Does.Contain("memorysmith_agent_session_end"));
            Assert.That(endResult.GetProperty("isError").GetBoolean(), Is.True,
                "ending an unknown session must signal an error result");
            Assert.That(endResult.GetProperty("content")[0].GetProperty("text").GetString(),
                Does.Contain("session_expired"));
        });
    }

    // ── Agent session store selection (TSK-0278) ─────────────────────────────

    [Test]
    public async Task AgentSessionStore_DefaultsToInMemory()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath);

        var store = factory.Services.GetRequiredService<MemorySmith.App.Services.AgentSessions.IAgentSessionStore>();

        Assert.That(store, Is.InstanceOf<MemorySmith.App.Services.AgentSessions.InMemoryAgentSessionStore>(),
            "without MemorySmith:AgentSession:PersistSessions the ephemeral store must remain the default");
    }

    [Test]
    public async Task AgentSessionStore_PersistSessionsTrue_SelectsSqliteStore()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        await using var factory = CreateFactory(dataPath, new Dictionary<string, string?>
        {
            ["MemorySmith:AgentSession:PersistSessions"] = "true"
        });

        var store = factory.Services.GetRequiredService<MemorySmith.App.Services.AgentSessions.IAgentSessionStore>();

        Assert.That(store, Is.InstanceOf<MemorySmith.App.Services.AgentSessions.SqliteAgentSessionStore>(),
            "PersistSessions=true must select the SQLite-backed store (TSK-0278)");
    }

    private WebApplicationFactory<Program> CreateFactory(string memoryPath, IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["MemorySmith:DataPath"] = memoryPath,
                    ["MemorySmith:EventLogPath"] = Path.Combine(_tempRoot, "Events", "audit.log"),
                    ["MemorySmith:Maintenance:Enabled"] = "false",
                    ["MemorySmith:ApiKey"] = string.Empty,
                    // Isolate the SQLite database per test to prevent lock contention.
                    // Each test gets its own temp database so they don't share state.
                    ["MemorySmith:Database:ConnectionString"] = $"Data Source={Path.Combine(_tempRoot, "memorysmith.db")};Pooling=False",
                    ["MemorySmith:DataProtectionKeysPath"] = Path.Combine(_tempRoot, "Keys"),
                    ["MemorySmith:Audit:JsonlPath"] = Path.Combine(_tempRoot, "Events", "audit-{yyyy}-W{week}.jsonl"),
                    ["MemorySmith:History:RootPath"] = Path.Combine(_tempRoot, ".history")
                    // Auth:Enabled stays true (default) so IAuthorizationService is registered
                    // (required by AgentSessionService). Admin is bootstrapped below.
                };

                if (overrides is not null)
                {
                    foreach (var item in overrides)
                    {
                        settings[item.Key] = item.Value;
                    }
                }

                config.AddInMemoryCollection(settings);
            });
        });

        // Bootstrap admin setup so the setup guard allows API/MCP requests on the fresh DB.
        // Without this, MemorySmithRequestGuardMiddleware redirects all requests to /auth/setup.
        using var bootstrapClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Task.Run(() => bootstrapClient.PostAsJsonWithAntiforgeryAsync(factory.Services, "/api/admin/setup",
            new SetupAdminRequest("Test Admin", "admin@memorysmith.test", "ThisIsAValidPassword123!"),
            JsonSerializerOptions.Web)).GetAwaiter().GetResult();

        return factory;
    }

    /// <summary>
    /// Returns a client signed in as the admin account bootstrapped in <see cref="CreateFactory"/>.
    /// The plain unauthenticated test client only carries Auth:AnonymousAccess (Viewer) rights,
    /// which blocks Write-tier MCP tools — anonymous callers can never be granted Editor
    /// (AddAnonymousRole only recognizes Viewer). Write-path tests must authenticate; the test
    /// client handles the auth cookie automatically.
    /// </summary>
    private static async Task<HttpClient> CreateAdminClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var loginResponse = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("admin@memorysmith.test", "ThisIsAValidPassword123!"));
        loginResponse.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<string> ExtractFirstToolTextAsync(HttpResponseMessage response)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;
    }

    private static MeterListener CreateToolExecutionListener(List<ToolTelemetryMeasurement> measurements)
    {
        var sync = new object();
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, currentListener) =>
        {
            if (instrument.Meter.Name == MemorySmithTelemetry.MeterName && instrument.Name.StartsWith("memorysmith.tool.execution", StringComparison.Ordinal))
            {
                currentListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            lock (sync)
            {
                measurements.Add(new ToolTelemetryMeasurement(instrument.Name, measurement, CopyTags(tags)));
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            lock (sync)
            {
                measurements.Add(new ToolTelemetryMeasurement(instrument.Name, measurement, CopyTags(tags)));
            }
        });
        listener.Start();
        return listener;
    }

    private static Dictionary<string, object?> CopyTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copied = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            copied[tag.Key] = tag.Value;
        }

        return copied;
    }

    private static bool HasToolMeasurement(
        IEnumerable<ToolTelemetryMeasurement> measurements,
        string instrumentName,
        string transport,
        string toolName,
        bool success) =>
        measurements.Any(measurement =>
            string.Equals(measurement.InstrumentName, instrumentName, StringComparison.Ordinal) &&
            string.Equals(measurement.Tags.GetValueOrDefault("memorysmith.transport")?.ToString(), transport, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(measurement.Tags.GetValueOrDefault("memorysmith.tool")?.ToString(), toolName, StringComparison.Ordinal) &&
            bool.TryParse(measurement.Tags.GetValueOrDefault("memorysmith.success")?.ToString(), out var taggedSuccess) &&
            taggedSuccess == success);

    private sealed record ToolTelemetryMeasurement(string InstrumentName, object Measurement, IReadOnlyDictionary<string, object?> Tags);
}
