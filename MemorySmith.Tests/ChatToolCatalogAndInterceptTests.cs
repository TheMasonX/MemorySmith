using System.Text.Json.Nodes;
using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using NUnit.Framework;

namespace MemorySmith.Tests;

[TestFixture]
public class ChatToolCatalogAndInterceptTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MemorySmithChatToolCatalogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best effort */ }
    }

    [Test]
    public void ChatToolCatalog_ExposesAllExpectedChatTools()
    {
        var catalog = new ChatToolCatalog();
        var expectedChatTools = new[]
        {
            "memorysmith_search",
            "memorysmith_hybrid_search",
            "memorysmith_context_pack",
            "memorysmith_get",
            "memorysmith_code_search",
            "memorysmith_code_search_status",
            "memorysmith_page_search",
            "memorysmith_page_get",
            "memorysmith_task_list",
            "memorysmith_task_get"
        };

        var actual = catalog.ChatTools.Select(tool => tool.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Multiple(() =>
        {
            foreach (var name in expectedChatTools)
            {
                Assert.That(actual.Contains(name), Is.True, $"Catalog is missing chat tool '{name}'.");
                Assert.That(catalog.TryGet(name, out var tool), Is.True);
                Assert.That(tool.InputSchema, Is.Not.Null, $"Tool '{name}' must declare an input schema.");
            }
        });
    }

    [Test]
    public void ChatToolCatalog_ExposesWriteToolsOnlyToAgentMode()
    {
        var catalog = new ChatToolCatalog();
        var chatTools = catalog.ChatTools.Select(tool => tool.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var agentTools = catalog.AgentTools.Select(tool => tool.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Multiple(() =>
        {
            Assert.That(chatTools.Contains("memorysmith_task_list"), Is.True);
            Assert.That(chatTools.Contains("memorysmith_task_get"), Is.True);
            Assert.That(chatTools.Contains("memorysmith_task_create"), Is.False);
            Assert.That(chatTools.Contains("memorysmith_task_update"), Is.False);
            Assert.That(chatTools.Contains("memorysmith_memory_create"), Is.False);
            Assert.That(chatTools.Contains("memorysmith_memory_update"), Is.False);
            Assert.That(chatTools.Contains("memorysmith_page_save"), Is.False);
            Assert.That(agentTools.Contains("memorysmith_task_create"), Is.True);
            Assert.That(agentTools.Contains("memorysmith_task_update"), Is.True);
            Assert.That(agentTools.Contains("memorysmith_task_set_status"), Is.True);
            Assert.That(agentTools.Contains("memorysmith_task_add_comment"), Is.True);
            Assert.That(agentTools.Contains("memorysmith_task_add_attachment"), Is.True);
            Assert.That(agentTools.Contains("memorysmith_memory_create"), Is.True);
            Assert.That(agentTools.Contains("memorysmith_memory_update"), Is.True);
            Assert.That(agentTools.Contains("memorysmith_page_save"), Is.False);
            Assert.That(agentTools.Contains("memorysmith_page_delete"), Is.False);
        });
    }

    [Test]
    public void ChatToolCatalog_ExposesExpectedMcpSourceTools()
    {
        var catalog = new ChatToolCatalog();
        var expectedMcpTools = new[]
        {
            "memorysmith_source_bundle",
            "memorysmith_find_by_source"
        };

        var actual = catalog.McpTools.Select(tool => tool.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Multiple(() =>
        {
            foreach (var name in expectedMcpTools)
            {
                Assert.That(actual.Contains(name), Is.True, $"Catalog is missing MCP tool '{name}'.");
                Assert.That(catalog.TryGet(name, out var tool), Is.True);
                Assert.That(tool.Risk, Is.EqualTo(ChatToolRisk.SensitiveRead), $"Tool '{name}' should be marked as a sensitive read.");
            }
        });
    }

    [Test]
    public void ChatToolCatalog_DefaultsSensitiveAndWriteMcpToolsOff()
    {
        var catalog = new ChatToolCatalog();
        var defaultOnMcp = catalog.McpTools.Where(tool => tool.EnabledByDefaultInMcp).Select(tool => tool.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var defaultOffMcp = catalog.McpTools.Where(tool => !tool.EnabledByDefaultInMcp).Select(tool => tool.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Multiple(() =>
        {
            Assert.That(defaultOffMcp, Does.Contain("memorysmith_source_bundle"));
            Assert.That(defaultOffMcp, Does.Contain("memorysmith_find_by_source"));
            Assert.That(defaultOffMcp, Does.Contain("memorysmith_code_search_merge_shard"));
            Assert.That(defaultOffMcp, Does.Contain("memorysmith_task_create"));
            Assert.That(defaultOffMcp, Does.Contain("memorysmith_task_update"));
            Assert.That(defaultOffMcp, Does.Contain("memorysmith_task_set_status"));
            Assert.That(defaultOffMcp, Does.Contain("memorysmith_task_add_comment"));
            Assert.That(defaultOffMcp, Does.Contain("memorysmith_task_add_attachment"));
            Assert.That(defaultOffMcp, Does.Contain("memorysmith_memory_create"));
            Assert.That(defaultOffMcp, Does.Contain("memorysmith_memory_update"));
            Assert.That(defaultOffMcp, Does.Contain("memorysmith_page_save"));
            Assert.That(defaultOffMcp, Does.Contain("memorysmith_page_delete"));
            Assert.That(defaultOnMcp, Does.Contain("memorysmith_search"));
            Assert.That(defaultOnMcp, Does.Contain("memorysmith_context_pack"));
            Assert.That(defaultOnMcp, Does.Contain("memorysmith_task_list"));
            Assert.That(defaultOnMcp, Does.Contain("memorysmith_task_get"));
        });
    }

    [Test]
    public async Task MemoryMutationTools_RequireAgentWritesAndAutoAcceptMode()
    {
        var store = new InMemoryMemoryStore();
        var memories = TestServiceFactory.CreateMemoryApplicationService(store, new RecordingEventStore(), new RecordingMemoryChangePublisher());
        var pages = new FilePageService(_tempDir);

        var catalog = new ChatToolCatalog();
        Assert.That(catalog.TryGet("memorysmith_memory_create", out var createTool), Is.True);

        var disabledResult = await createTool.Execute(new JsonObject
        {
            ["title"] = "Blocked by config",
            ["content"] = "No write should occur."
        }, new ChatToolExecutionContext(memories, pages, "test", AgentWritesEnabled: false, AgentWriteAutoAccept: false), CancellationToken.None);

        var manualResult = await createTool.Execute(new JsonObject
        {
            ["title"] = "Blocked by approval",
            ["content"] = "No write should occur."
        }, new ChatToolExecutionContext(memories, pages, "test", AgentWritesEnabled: true, AgentWriteAutoAccept: false), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(disabledResult.IsError, Is.True);
            Assert.That(disabledResult.Text, Does.Contain("disabled by configuration"));
            Assert.That(manualResult.IsError, Is.True);
            Assert.That(manualResult.Text, Does.Contain("requires Agent auto_accept mode"));
            Assert.That(store.LoadAll(), Is.Empty);
        });
    }

    [Test]
    public async Task MemoryMutationTools_CreateAndUpdateMemoryRecordInAutoAcceptMode()
    {
        var store = new InMemoryMemoryStore();
        var memories = TestServiceFactory.CreateMemoryApplicationService(store, new RecordingEventStore(), new RecordingMemoryChangePublisher());
        var pages = new FilePageService(_tempDir);
        var ctx = new ChatToolExecutionContext(memories, pages, "test", AgentWritesEnabled: true, AgentWriteAutoAccept: true);

        var catalog = new ChatToolCatalog();
        Assert.That(catalog.TryGet("memorysmith_memory_create", out var createTool), Is.True);
        Assert.That(catalog.TryGet("memorysmith_memory_update", out var updateTool), Is.True);

        var createResult = await createTool.Execute(new JsonObject
        {
            ["id"] = "tool-created-memory",
            ["title"] = "Tool Created Memory",
            ["content"] = "Initial content.",
            ["status"] = "Working",
            ["confidence"] = 0.7,
            ["tags"] = new JsonArray("tooling", "chat")
        }, ctx, CancellationToken.None);

        var updateResult = await updateTool.Execute(new JsonObject
        {
            ["id"] = "tool-created-memory",
            ["content"] = "Updated content.",
            ["status"] = "Core",
            ["tags"] = new JsonArray("tooling", "updated")
        }, ctx, CancellationToken.None);

        var saved = store.Load("tool-created-memory");

        Assert.Multiple(() =>
        {
            Assert.That(createResult.IsError, Is.False);
            Assert.That(updateResult.IsError, Is.False);
            Assert.That(saved, Is.Not.Null);
            Assert.That(saved!.Title, Is.EqualTo("Tool Created Memory"));
            Assert.That(saved.Content, Is.EqualTo("Updated content."));
            Assert.That(saved.Status, Is.EqualTo(MemoryStatus.Core));
            Assert.That(saved.Tags, Is.EquivalentTo(new[] { "tooling", "updated" }));
        });
    }

    [Test]
    public async Task PageGetTool_RejectsSlugWithPathTraversal()
    {
        var pages = new FilePageService(_tempDir);
        await pages.SaveAsync(new PageSaveRequest("notes/intro", "Intro", "Hello world"), CancellationToken.None);

        var catalog = new ChatToolCatalog();
        Assert.That(catalog.TryGet("memorysmith_page_get", out var tool), Is.True);

        var ctx = new ChatToolExecutionContext(null!, pages, "test");
        var args = new JsonObject { ["slug"] = "../etc/passwd" };

        var result = await tool.Execute(args, ctx, CancellationToken.None);

        // Path-traversal must not return content from any page on disk.
        Assert.That(result.Text, Does.Not.Contain("Hello world"));
    }

    [Test]
    public async Task PageGetTool_TruncatesAtMaxCharacters()
    {
        var pages = new FilePageService(_tempDir);
        var longMarkdown = new string('z', 5000);
        await pages.SaveAsync(new PageSaveRequest("big", "Big Page", longMarkdown), CancellationToken.None);

        var catalog = new ChatToolCatalog();
        catalog.TryGet("memorysmith_page_get", out var tool);
        var ctx = new ChatToolExecutionContext(null!, pages, "test");
        var args = new JsonObject { ["slug"] = "big", ["maxCharacters"] = 500 };

        var result = await tool.Execute(args, ctx, CancellationToken.None);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.Text, Does.Contain("truncated from"));
        Assert.That(result.Text, Does.Contain("at 500 char limit"));
    }

    [Test]
    public async Task PageSearchTool_ReturnsMatchingPages()
    {
        var pages = new FilePageService(_tempDir);
        await pages.SaveAsync(new PageSaveRequest("alpha", "Alpha", "the durable evidence is here"), CancellationToken.None);
        await pages.SaveAsync(new PageSaveRequest("beta", "Beta", "unrelated content"), CancellationToken.None);

        var catalog = new ChatToolCatalog();
        catalog.TryGet("memorysmith_page_search", out var tool);
        var ctx = new ChatToolExecutionContext(null!, pages, "test");

        var result = await tool.Execute(new JsonObject { ["query"] = "durable evidence" }, ctx, CancellationToken.None);

        Assert.That(result.Text, Does.Contain("alpha"));
    }

    [Test]
    public async Task SearchTool_WithJsonFormat_ReturnsEnvelopeAndContextDiagnostics()
    {
        var store = new InMemoryMemoryStore();
        var options = Options.Create(new MemorySmithOptions());
        var diagnostics = new MemoryDiagnosticsService(
            new TagPolicyService(options),
            new VarResolver(new EmptyVarStore(), options),
            store,
            options);
        var memories = TestServiceFactory.CreateMemoryApplicationService(
            store,
            new RecordingEventStore(),
            new RecordingMemoryChangePublisher(),
            diagnostics: diagnostics);
        store.Save(new MemoryRecord
        {
            Id = "tool-warning-record",
            Title = "Tool Warning Record",
            Content = "tool retrieval warning propagation",
            Status = MemoryStatus.Core,
            Tags = ["project-wiki"],
            Confidence = 1,
            SourceLinks = [new SourceLink { Uri = "%MissingVariable%MemorySmith.App/Program.cs" }]
        });

        var catalog = new ChatToolCatalog();
        Assert.That(catalog.TryGet("memorysmith_search", out var tool), Is.True);
        var args = new JsonObject { ["query"] = "tool retrieval warning", ["format"] = "json" };
        Assert.That(ChatToolCatalog.ReadString(args, "format"), Is.EqualTo("json"));
        var result = await tool.Execute(
            args,
            new ChatToolExecutionContext(memories, new FilePageService(_tempDir), "test"),
            CancellationToken.None);

        var json = JsonNode.Parse(result.Text)!.AsObject();
        Assert.Multiple(() =>
        {
            Assert.That(json["schemaVersion"]!.GetValue<string>(), Is.EqualTo("memorysmith.retrieval-results.v1"));
            Assert.That(json["provider"]!["kind"]!.GetValue<string>(), Is.EqualTo("lexical"));
            Assert.That(json["warnings"]!.AsArray().Select(node => node!.GetValue<string>()), Has.Some.Contains("source.missing_variable"));
            Assert.That(result.Structured, Is.Not.Null);
            Assert.That(result.ContextItems!.Single().Diagnostics!.Select(diagnostic => diagnostic.Code), Does.Contain("source.missing_variable"));
        });
    }

    [Test]
    public void SearchToolSchemas_AdvertiseAllStructuredFormatAliases()
    {
        var catalog = new ChatToolCatalog();
        Assert.That(catalog.TryGet("memorysmith_search", out var searchTool), Is.True);

        static List<string> ReadFormatEnums(ChatToolDescriptor tool) =>
            tool.InputSchema["properties"]?["format"]?["enum"]?.AsArray().Select(node => node?.GetValue<string>() ?? string.Empty).ToList()
            ?? [];

        var searchFormats = ReadFormatEnums(searchTool);

        Assert.Multiple(() =>
        {
            Assert.That(searchFormats, Is.EquivalentTo(new[] { "markdown", "json", "envelope", "json-v2" }));
        });
    }

    [TestCase("json")]
    [TestCase("envelope")]
    [TestCase("json-v2")]
    public async Task SearchTool_AcceptsStructuredFormatAliases(string format)
    {
        var store = new InMemoryMemoryStore();
        var options = Options.Create(new MemorySmithOptions());
        var diagnostics = new MemoryDiagnosticsService(
            new TagPolicyService(options),
            new VarResolver(new EmptyVarStore(), options),
            store,
            options);
        var memories = TestServiceFactory.CreateMemoryApplicationService(
            store,
            new RecordingEventStore(),
            new RecordingMemoryChangePublisher(),
            diagnostics: diagnostics);
        store.Save(new MemoryRecord
        {
            Id = "tool-format-alias-record",
            Title = "Tool Format Alias Record",
            Content = "structured search format alias contract",
            Status = MemoryStatus.Core,
            Tags = ["project-wiki"],
            Confidence = 1
        });

        var catalog = new ChatToolCatalog();
        Assert.That(catalog.TryGet("memorysmith_search", out var tool), Is.True);

        var result = await tool.Execute(
            new JsonObject { ["query"] = "structured search format alias", ["format"] = format },
            new ChatToolExecutionContext(memories, new FilePageService(_tempDir), "test"),
            CancellationToken.None);

        var json = JsonNode.Parse(result.Text)!.AsObject();
        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False);
            Assert.That(result.Structured, Is.Not.Null);
            Assert.That(json["schemaVersion"]!.GetValue<string>(), Is.EqualTo("memorysmith.retrieval-results.v1"));
            Assert.That(json["results"]!.AsArray().Count, Is.GreaterThan(0));
        });
    }

    [Test]
    public async Task PageSearchTool_ReturnsVisibleMatchesBeyondFirstTwoHundredHiddenResults()
    {
        var pages = new FilePageService(_tempDir);
        const string query = "crowded tool visibility token";
        await PageVisibilitySearchFixture.SeedAsync(pages, query, CancellationToken.None);

        var catalog = new ChatToolCatalog();
        catalog.TryGet("memorysmith_page_search", out var pageSearchTool);
        
        var ctx = new ChatToolExecutionContext(null!, pages, "test");

        var pageSearchResult = await pageSearchTool.Execute(new JsonObject
        {
            ["query"] = query,
            ["limit"] = 2
        }, ctx, CancellationToken.None);


        Assert.Multiple(() =>
        {
            foreach (var slug in PageVisibilitySearchFixture.PublicPageSlugs)
            {
                Assert.That(pageSearchResult.Text, Does.Contain(slug));
                
            }

            Assert.That(pageSearchResult.Text, Does.Not.Contain("signed-in-page-001"));
            
        });
    }

    [Test]
    public async Task PageSaveTool_RejectsResolvedAdminDefaultForNonAdminEditor()
    {
        var pages = new FilePageService(_tempDir, new PageOptions { DefaultMinimumRole = PageAccessLevels.Admin });
        var catalog = new ChatToolCatalog();
        Assert.That(catalog.TryGet("memorysmith_page_save", out var tool), Is.True);

        var editor = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, MemorySmithRoles.Editor)], "Test"));
        var ctx = new ChatToolExecutionContext(null!, pages, "test", User: editor, Auth: new AuthOptions(), DefaultPageMinimumRole: PageAccessLevels.Admin);

        var result = await tool.Execute(new JsonObject
        {
            ["slug"] = "editor-page",
            ["title"] = "Editor Page",
            ["markdown"] = "Body"
        }, ctx, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.Text, Does.Contain("not authorized"));
        });
    }

    [Test]
    public async Task PageDeleteTool_RejectsAnonymousCallerEvenForVisiblePage()
    {
        var pages = new FilePageService(_tempDir);
        await pages.SaveAsync(new PageSaveRequest("public-delete-target", "Public Delete Target", "Body", PageAccessLevels.Anonymous), CancellationToken.None);

        var catalog = new ChatToolCatalog();
        Assert.That(catalog.TryGet("memorysmith_page_delete", out var tool), Is.True);

        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var ctx = new ChatToolExecutionContext(null!, pages, "test", User: anonymous, Auth: new AuthOptions());

        var result = await tool.Execute(new JsonObject
        {
            ["slug"] = "public-delete-target"
        }, ctx, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.Text, Does.Contain("not authorized"));
            Assert.That(File.Exists(Path.Combine(_tempDir, "public-delete-target.md")), Is.True);
        });
    }

    [Test]
    public async Task PageDeleteTool_AllowsEditorToDeleteNonAdminPage()
    {
        var pages = new FilePageService(_tempDir);
        await pages.SaveAsync(new PageSaveRequest("editor-delete-target", "Editor Delete Target", "Body", PageAccessLevels.Authenticated), CancellationToken.None);

        var catalog = new ChatToolCatalog();
        Assert.That(catalog.TryGet("memorysmith_page_delete", out var tool), Is.True);

        var editor = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, MemorySmithRoles.Editor)], "Test"));
        var ctx = new ChatToolExecutionContext(null!, pages, "test", User: editor, Auth: new AuthOptions());

        var result = await tool.Execute(new JsonObject
        {
            ["slug"] = "editor-delete-target"
        }, ctx, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False);
            Assert.That(result.Text, Does.Contain("deleted"));
            Assert.That(File.Exists(Path.Combine(_tempDir, "editor-delete-target.md")), Is.False);
        });
    }

    [Test]
    public async Task CodeSearchMergeShardTool_RejectsShardPathOutsideAllowedRoots()
    {
        var repoRoot = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(repoRoot);
        using var codeSearch = CreateCodeSearchService(repoRoot);

        var outsideRoot = Path.Combine(Path.GetTempPath(), "MemorySmithMergeShardOutside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideRoot);
        var shardPath = Path.Combine(outsideRoot, "outside.db");
        CreateEmptyShardDatabase(shardPath);

        var catalog = new ChatToolCatalog();
        Assert.That(catalog.TryGet("memorysmith_code_search_merge_shard", out var tool), Is.True);
        var ctx = new ChatToolExecutionContext(null!, new FilePageService(_tempDir), "test", CodeSearch: codeSearch);

        var result = await tool.Execute(new JsonObject
        {
            ["shardPath"] = shardPath
        }, ctx, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.Text, Does.Contain("configured code-search roots"));
        });
    }

    [Test]
    public async Task CodeSearchMergeShardTool_RejectsNonSqliteExtensionEvenInsideAllowedRoot()
    {
        var repoRoot = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(repoRoot);
        using var codeSearch = CreateCodeSearchService(repoRoot);

        var invalidExtensionPath = Path.Combine(repoRoot, "not-a-shard.txt");
        await File.WriteAllTextAsync(invalidExtensionPath, "not sqlite");

        var catalog = new ChatToolCatalog();
        Assert.That(catalog.TryGet("memorysmith_code_search_merge_shard", out var tool), Is.True);
        var ctx = new ChatToolExecutionContext(null!, new FilePageService(_tempDir), "test", CodeSearch: codeSearch);

        var result = await tool.Execute(new JsonObject
        {
            ["shardPath"] = invalidExtensionPath
        }, ctx, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.Text, Does.Contain("SQLite shard file extension"));
        });
    }

    [Test]
    public async Task CodeSearchMergeShardTool_AllowsSqliteShardInsideAllowedRoot()
    {
        var repoRoot = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(repoRoot);
        using var codeSearch = CreateCodeSearchService(repoRoot);

        var shardPath = Path.Combine(repoRoot, "allowed-shard.db");
        CreateEmptyShardDatabase(shardPath);

        var catalog = new ChatToolCatalog();
        Assert.That(catalog.TryGet("memorysmith_code_search_merge_shard", out var tool), Is.True);
        var ctx = new ChatToolExecutionContext(null!, new FilePageService(_tempDir), "test", CodeSearch: codeSearch);

        var result = await tool.Execute(new JsonObject
        {
            ["shardPath"] = shardPath,
            ["preferNewer"] = true
        }, ctx, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False);
            Assert.That(result.Text, Does.Contain("insertedChunkCount"));
            Assert.That(result.Text, Does.Contain("totalShardChunkCount"));
        });
    }

    [TestCase("search the wiki for durable evidence", "memorysmith_hybrid_search")]
    [TestCase("search the codebase for widget parser", "memorysmith_code_search")]
    [TestCase("find records about caching layer", "memorysmith_hybrid_search")]
    [TestCase("semantic search for vector embeddings", "memorysmith_hybrid_search")]
    [TestCase("hybrid search for chat tools", "memorysmith_hybrid_search")]
    [TestCase("get memory tool-target", "memorysmith_get")]
    [TestCase("open page notes/intro", "memorysmith_page_get")]
    [TestCase("context pack for streaming evidence", "memorysmith_context_pack")]
    public void ChatIntentInterceptor_RecognisesCommonPatterns(string message, string expectedTool)
    {
        var interceptor = new ChatIntentInterceptor();
        var match = interceptor.TryMatch(message);
        Assert.That(match, Is.Not.Null, $"No intercept match for: {message}");
        Assert.That(match!.ToolName, Is.EqualTo(expectedTool));
    }

    [TestCase("Tell me about the weather")]
    [TestCase("how are you")]
    [TestCase("")]
    public void ChatIntentInterceptor_DoesNotMatchAmbiguousMessages(string message)
    {
        var interceptor = new ChatIntentInterceptor();
        Assert.That(interceptor.TryMatch(message), Is.Null);
    }

    private CodeSearchService CreateCodeSearchService(string repositoryRoot)
    {
        var dataPath = Path.Combine(_tempDir, "data", "Memories");
        Directory.CreateDirectory(dataPath);

        var options = new MemorySmithOptions
        {
            DataPath = dataPath,
            CodeSearch = new CodeSearchOptions
            {
                RepositoryRootPath = repositoryRoot,
                TargetDirectories = ["MemorySmith.App"],
                IncludedFileExtensions = [".cs"],
                MaxResults = 10
            }
        };

        return new CodeSearchService(new TestEmbeddingProvider(), null!, Options.Create(options), NullLogger<CodeSearchService>.Instance);
    }

    private static void CreateEmptyShardDatabase(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS CodeSearchChunks (
    TargetKey TEXT NOT NULL,
    DocumentPath TEXT NOT NULL,
    AbsolutePath TEXT NOT NULL,
    ChunkId INTEGER NOT NULL,
    SourceHash TEXT NOT NULL,
    SourceLengthBytes INTEGER NOT NULL,
    SourceLastWriteUtc TEXT NOT NULL,
    ConfigurationHash TEXT NOT NULL,
    StartLine INTEGER NOT NULL,
    EndLine INTEGER NOT NULL,
    Snippet TEXT NOT NULL,
    SearchText TEXT NOT NULL,
    EmbeddingJson TEXT,
    IndexedAtUtc TEXT NOT NULL
);";
        command.ExecuteNonQuery();
    }

    private sealed class TestEmbeddingProvider : ITextEmbeddingProvider
    {
        public EmbeddingProviderStatus GetStatus() => new(true, "ok", null, null, 8, "cpu", "cpu", null, null);

        public bool TryEmbed(string text, EmbeddingInputKind kind, out float[] embedding, out string? reason)
        {
            embedding = new float[8];
            reason = null;
            return true;
        }
    }
}



