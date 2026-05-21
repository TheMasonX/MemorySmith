using System.Text.Json.Nodes;
using MemorySmith.App.Services;
using MemorySmith.Core.Models;
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
            "memorysmith_semantic_search",
            "memorysmith_hybrid_search",
            "memorysmith_context_pack",
            "memorysmith_get",
            "memorysmith_page_search",
            "memorysmith_page_get",
            "memorysmith_unified_search"
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
    public async Task PageSearchAndUnifiedSearchTools_ReturnVisibleMatchesBeyondFirstTwoHundredHiddenResults()
    {
        var pages = new FilePageService(_tempDir);
        const string query = "crowded tool visibility token";
        await PageVisibilitySearchFixture.SeedAsync(pages, query, CancellationToken.None);

        var catalog = new ChatToolCatalog();
        catalog.TryGet("memorysmith_page_search", out var pageSearchTool);
        catalog.TryGet("memorysmith_unified_search", out var unifiedSearchTool);
        var ctx = new ChatToolExecutionContext(null!, pages, "test");

        var pageSearchResult = await pageSearchTool.Execute(new JsonObject
        {
            ["query"] = query,
            ["limit"] = 2
        }, ctx, CancellationToken.None);
        var unifiedSearchResult = await unifiedSearchTool.Execute(new JsonObject
        {
            ["query"] = query,
            ["memoryLimit"] = 0,
            ["pageLimit"] = 2
        }, ctx, CancellationToken.None);

        Assert.Multiple(() =>
        {
            foreach (var slug in PageVisibilitySearchFixture.PublicPageSlugs)
            {
                Assert.That(pageSearchResult.Text, Does.Contain(slug));
                Assert.That(unifiedSearchResult.Text, Does.Contain(slug));
            }

            Assert.That(pageSearchResult.Text, Does.Not.Contain("signed-in-page-001"));
            Assert.That(unifiedSearchResult.Text, Does.Not.Contain("signed-in-page-001"));
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

    [TestCase("search the wiki for durable evidence", "memorysmith_unified_search")]
    [TestCase("find records about caching layer", "memorysmith_unified_search")]
    [TestCase("semantic search for vector embeddings", "memorysmith_semantic_search")]
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
}
