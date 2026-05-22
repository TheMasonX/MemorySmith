using MemorySmith.App.Services;
using MemorySmith.App.Controllers;
using MemorySmith.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Net;
using System.Security.Claims;

namespace MemorySmith.Tests;

[TestFixture]
public class PagesAndChatTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"memorysmith-pages-chat-{Guid.NewGuid():N}");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public async Task FilePageService_SavesSearchesAndRendersMarkdown()
    {
        var pages = new FilePageService(_tempDir);

        var saved = await pages.SaveAsync(new PageSaveRequest("Design Notes", "Design Notes", "Markdown body with ![alt](assets/diagram.png)"), CancellationToken.None);
        var loaded = await pages.GetAsync(saved.Slug, CancellationToken.None);
        var search = await pages.SearchAsync(new PageSearchQuery("diagram"), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(saved.Slug, Is.EqualTo("design-notes"));
            Assert.That(saved.MinimumRole, Is.EqualTo(PageAccessLevels.Anonymous));
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.Html, Does.Contain(">Design Notes</h1>"));
            Assert.That(loaded.Html, Does.Contain("/page-assets/diagram.png"));
            Assert.That(search.Select(page => page.Slug), Does.Contain("design-notes"));
        });
    }

    [Test]
    public async Task FilePageService_PersistsPageMinimumRoleMetadata()
    {
        var pages = new FilePageService(_tempDir, new PageOptions { DefaultMinimumRole = PageAccessLevels.Authenticated });

        var saved = await pages.SaveAsync(new PageSaveRequest("secure-page", "Secure Page", "Private by default"), CancellationToken.None);
        var updated = await pages.SaveAsync(new PageSaveRequest("secure-page", "Secure Page", "Still private"), CancellationToken.None);
        var publicPage = await pages.SaveAsync(new PageSaveRequest("secure-page", "Secure Page", "Now public", PageAccessLevels.Anonymous), CancellationToken.None);
        var listed = await pages.ListAsync(CancellationToken.None);
        var metadataPath = Path.Combine(_tempDir, "secure-page.page.json");
        var metadata = await File.ReadAllTextAsync(metadataPath);
        var deleted = await pages.DeleteAsync("secure-page", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(saved.MinimumRole, Is.EqualTo(PageAccessLevels.Authenticated));
            Assert.That(updated.MinimumRole, Is.EqualTo(PageAccessLevels.Authenticated));
            Assert.That(publicPage.MinimumRole, Is.EqualTo(PageAccessLevels.Anonymous));
            Assert.That(listed.Single().MinimumRole, Is.EqualTo(PageAccessLevels.Anonymous));
            Assert.That(metadata, Does.Contain("Anonymous"));
            Assert.That(deleted, Is.True);
            Assert.That(File.Exists(metadataPath), Is.False);
        });
    }

    [Test]
    public async Task PageSearchVisibleAsync_FindsVisibleMatchesBeyondFirstTwoHundredHiddenResults()
    {
        var pages = new FilePageService(_tempDir);
        const string query = "crowded visibility search token";
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        await PageVisibilitySearchFixture.SeedAsync(pages, query, CancellationToken.None);

        var visiblePages = await pages.SearchVisibleAsync(
            query,
            page => PageAccessLevels.CanView(page.MinimumRole, anonymous, new AuthOptions()),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(visiblePages, Has.Count.EqualTo(PageVisibilitySearchFixture.PublicPageSlugs.Length));
            Assert.That(visiblePages.Select(page => page.Slug), Is.EquivalentTo(PageVisibilitySearchFixture.PublicPageSlugs));
        });
    }

    [Test]
    public void PageAccessLevels_EditorsCannotSetAdminMinimumRole()
    {
        var editor = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, MemorySmithRoles.Editor)], "Test"));
        var admin = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, MemorySmithRoles.Admin)], "Test"));
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.Multiple(() =>
        {
            Assert.That(PageAccessLevels.CanSetMinimumRole(PageAccessLevels.Anonymous, editor, new AuthOptions()), Is.True);
            Assert.That(PageAccessLevels.CanSetMinimumRole(PageAccessLevels.Authenticated, editor, new AuthOptions()), Is.True);
            Assert.That(PageAccessLevels.CanSetMinimumRole(PageAccessLevels.Admin, editor, new AuthOptions()), Is.False);
            Assert.That(PageAccessLevels.CanSetMinimumRole(PageAccessLevels.Admin, admin, new AuthOptions()), Is.True);
            Assert.That(PageAccessLevels.CanSetMinimumRole(PageAccessLevels.Anonymous, anonymous, new AuthOptions()), Is.False);
        });
    }

    [Test]
    public void PageAccessLevels_AuthDisabledAllowsViewingAndEditingAllMinimumRoles()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var authDisabled = new AuthOptions { Enabled = false };

        Assert.Multiple(() =>
        {
            Assert.That(PageAccessLevels.CanView(PageAccessLevels.Admin, anonymous, authDisabled), Is.True);
            Assert.That(PageAccessLevels.CanSetMinimumRole(PageAccessLevels.Admin, anonymous, authDisabled), Is.True);
        });
    }

    [Test]
    public void PageAccessLevels_AutoEditorTreatsAuthenticatedUsersAsEditorsEvenWithViewerRole()
    {
        var viewer = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, MemorySmithRoles.Viewer)], "Test"));
        var auth = new AuthOptions { AutoEditorForAuthenticatedUsers = true };

        Assert.Multiple(() =>
        {
            Assert.That(PageAccessLevels.CanSetMinimumRole(PageAccessLevels.Anonymous, viewer, auth), Is.True);
            Assert.That(PageAccessLevels.CanSetMinimumRole(PageAccessLevels.Authenticated, viewer, auth), Is.True);
            Assert.That(PageAccessLevels.CanSetMinimumRole(PageAccessLevels.Admin, viewer, auth), Is.False);
        });
    }

    [Test]
    public async Task PagesController_Save_RejectsResolvedAdminDefaultForNonAdminEditor()
    {
        var pages = new FilePageService(_tempDir, new PageOptions { DefaultMinimumRole = PageAccessLevels.Admin });
        var options = new StaticOptionsMonitor<MemorySmithOptions>(new MemorySmithOptions
        {
            Pages = new PageOptions { DefaultMinimumRole = PageAccessLevels.Admin },
            Auth = new AuthOptions()
        });
        var controller = new PagesController(pages, options)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, MemorySmithRoles.Editor)], "Test"))
                }
            }
        };

        var response = await controller.Save(new PageSaveRequest("editor-page", "Editor Page", "Body"), CancellationToken.None);

        Assert.That(response.Result, Is.InstanceOf<ForbidResult>());
    }

    [Test]
    public async Task PagesController_Save_PersistsResolvedConfiguredDefaultMinimumRole()
    {
        var pages = new FilePageService(_tempDir, new PageOptions { DefaultMinimumRole = PageAccessLevels.Anonymous });
        var options = new StaticOptionsMonitor<MemorySmithOptions>(new MemorySmithOptions
        {
            Pages = new PageOptions { DefaultMinimumRole = PageAccessLevels.Authenticated },
            Auth = new AuthOptions()
        });
        var controller = new PagesController(pages, options)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, MemorySmithRoles.Editor)], "Test"))
                }
            }
        };

        var response = await controller.Save(new PageSaveRequest("editor-page", "Editor Page", "Body"), CancellationToken.None);
        var created = response.Result as CreatedAtActionResult;
        var saved = created?.Value as PageDocument;
        var loaded = await pages.GetAsync("editor-page", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(created, Is.Not.Null);
            Assert.That(saved, Is.Not.Null);
            Assert.That(saved!.MinimumRole, Is.EqualTo(PageAccessLevels.Authenticated));
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.MinimumRole, Is.EqualTo(PageAccessLevels.Authenticated));
        });
    }

    [Test]
    public async Task PagesController_Update_CreatesMissingPageWithResolvedConfiguredDefaultMinimumRole()
    {
        var pages = new FilePageService(_tempDir, new PageOptions { DefaultMinimumRole = PageAccessLevels.Anonymous });
        var options = new StaticOptionsMonitor<MemorySmithOptions>(new MemorySmithOptions
        {
            Pages = new PageOptions { DefaultMinimumRole = PageAccessLevels.Authenticated },
            Auth = new AuthOptions()
        });
        var controller = new PagesController(pages, options)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, MemorySmithRoles.Editor)], "Test"))
                }
            }
        };

        var response = await controller.Update("editor-page", new PageSaveRequest(null, "Editor Page", "Body"), CancellationToken.None);
        var updated = response.Result as OkObjectResult;
        var saved = updated?.Value as PageDocument;
        var loaded = await pages.GetAsync("editor-page", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(updated, Is.Not.Null);
            Assert.That(saved, Is.Not.Null);
            Assert.That(saved!.MinimumRole, Is.EqualTo(PageAccessLevels.Authenticated));
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.MinimumRole, Is.EqualTo(PageAccessLevels.Authenticated));
        });
    }

    [Test]
    public async Task FilePageService_UsesMostRestrictiveReferencedPageRoleForAssetAccess()
    {
        var pages = new FilePageService(_tempDir);

        await pages.SaveAsync(new PageSaveRequest("admin-page", "Admin Page", "![asset](assets/shared.png)", PageAccessLevels.Admin), CancellationToken.None);
        await pages.SaveAsync(new PageSaveRequest("public-page", "Public Page", "![asset](/page-assets/shared.png)", PageAccessLevels.Anonymous), CancellationToken.None);

        var accessInfo = await pages.GetAssetAccessInfoAsync("shared.png", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(accessInfo.IsReferenced, Is.True);
            Assert.That(accessInfo.MinimumRole, Is.EqualTo(PageAccessLevels.Admin));
        });
    }

    [Test]
    public async Task FilePageService_IgnoresPlainTextAndCodeBlockAssetMentionsWhenBuildingAssetAccessIndex()
    {
        var pages = new FilePageService(_tempDir);

        await pages.SaveAsync(new PageSaveRequest("notes", "Notes", """
        Plain text mention: assets/ghost.png

        `assets/ghost.png`

        ```md
        ![ghost](assets/ghost.png)
        <img src="assets/ghost.png" />
        ```
        """), CancellationToken.None);

        var accessInfo = await pages.GetAssetAccessInfoAsync("ghost.png", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(accessInfo.IsReferenced, Is.False);
            Assert.That(accessInfo.MinimumRole, Is.EqualTo(PageAccessLevels.Anonymous));
        });
    }

    [Test]
    public void FilePageService_DisablesRawHtmlByDefault()
    {
        var pages = new FilePageService(_tempDir);

        var html = pages.RenderHtml("# Trusted markdown\n\n<script>alert(1)</script>\n\n<div onclick=\"alert(1)\">Unsafe</div>\n\n![diagram](assets/diagram.png)");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain(">Trusted markdown</h1>"));
            Assert.That(html, Does.Contain("/page-assets/diagram.png"));
            Assert.That(html, Does.Not.Contain("<script"));
            Assert.That(html, Does.Not.Contain("<div"));
        });
    }

    [Test]
    public void FilePageService_AllowsRawHtmlWhenExplicitlyTrusted()
    {
        var pages = new FilePageService(_tempDir, new PageOptions { AllowRawHtml = true });

        var html = pages.RenderHtml("<video src=\"assets/demo.mp4\" controls></video>");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("<video"));
            Assert.That(html, Does.Contain("/page-assets/demo.mp4"));
        });
    }

    [Test]
    public void ChatMarkdownRenderer_RendersMarkdownAndBlocksUnsafeHtml()
    {
        var html = ChatMarkdownRenderer.RenderHtml("""
        # Answer

        **Bold** text with `code`.

        | Item | Value |
        | --- | --- |
        | One | Two |

        ```csharp
        Console.WriteLine("hello");
        ```

        <script>alert(1)</script>

        [bad](javascript:alert(1))

        ![bad image](javascript:alert(1))
        """);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain(">Answer</h1>"));
            Assert.That(html, Does.Contain("<strong>Bold</strong>"));
            Assert.That(html, Does.Contain("<table>"));
            Assert.That(html, Does.Contain("language-csharp"));
            Assert.That(html, Does.Not.Contain("<script"));
            Assert.That(html, Does.Not.Contain("javascript:alert"));
            Assert.That(html, Does.Contain("href=\"#\""));
            Assert.That(html, Does.Contain("src=\"\""));
            Assert.That(html, Does.Not.Contain("src=\"#\""));
        });
    }

    [Test]
    public void ChatMarkdownRenderer_RendersMermaidAndPrismCodeBlocks()
    {
        var html = ChatMarkdownRenderer.RenderHtml("""
        ```mermaid
        graph TD
            A[Start] --> B[Done]
        ```

        ```json
        { "answer": true }
        ```
        """);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("<pre class=\"mermaid\">"));
            Assert.That(html, Does.Contain("graph TD"));
            Assert.That(html, Does.Contain("<pre><code class=\"language-json\">"));
            Assert.That(html, Does.Not.Contain("language-mermaid"));
        });
    }

    [Test]
    public void ChatMarkdownRenderer_KeepsUnclosedMermaidFenceAsCode()
    {
        var html = ChatMarkdownRenderer.RenderHtml("""
        ```mermaid
        graph TD
        """);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Not.Contain("<pre class=\"mermaid\">"));
            Assert.That(html, Does.Contain("<pre><code class=\"language-mermaid\">"));
            Assert.That(html, Does.Contain("graph TD"));
        });
    }

    [Test]
    public void FilePageService_RendersMermaidAndPrismCodeBlocks()
    {
        var pages = new FilePageService(_tempDir);

        var html = pages.RenderHtml("""
        # Diagram

        ```mermaid
        sequenceDiagram
            participant A
            participant B
            A->>B: Hello
        ```

        ```csharp
        Console.WriteLine("highlighted");
        ```
        """);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain(">Diagram</h1>"));
            Assert.That(html, Does.Contain("<pre class=\"mermaid\">"));
            Assert.That(html, Does.Contain("sequenceDiagram"));
            Assert.That(html, Does.Contain("<pre><code class=\"language-csharp\">"));
        });
    }

    [Test]
    public async Task FilePageService_SavesImageAssetsForMarkdownEmbeds()
    {
        var pages = new FilePageService(_tempDir);
        await using var content = new MemoryStream([1, 2, 3, 4]);

        var asset = await pages.SaveAssetAsync("My Diagram.PNG", content, CancellationToken.None);
        var saved = await pages.SaveAsync(new PageSaveRequest("asset-page", "Asset Page", $"![diagram]({asset.MarkdownPath})"), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(asset.FileName, Is.EqualTo("my-diagram.png"));
            Assert.That(asset.MarkdownPath, Is.EqualTo("assets/my-diagram.png"));
            Assert.That(asset.RequestPath, Is.EqualTo("/page-assets/my-diagram.png"));
            Assert.That(File.Exists(Path.Combine(_tempDir, "assets", asset.FileName)), Is.True);
            Assert.That(saved.Html, Does.Contain("/page-assets/my-diagram.png"));
        });
    }

    [Test]
    public async Task MemoryChatAgent_AgentModeWritesMemoriesAndPagesFromProviderJson()
    {
        var memoryStore = new InMemoryMemoryStore();
        var eventStore = new RecordingEventStore();
        var publisher = new RecordingMemoryChangePublisher();
        var memories = TestServiceFactory.CreateMemoryApplicationService(memoryStore, eventStore, publisher);
        var pages = new FilePageService(_tempDir);
        var provider = new FakeChatProvider("""
        {
          "reply": "Recorded.",
          "memoryWrites": [
            { "id": "agent-note", "title": "Agent Note", "content": "Remember this from chat.", "tags": ["agent", "chat"], "status": "Core", "confidence": 0.8 }
          ],
          "pageWrites": [
            { "slug": "agent-page", "title": "Agent Page", "markdown": "Agent page body." }
          ]
        }
        """);
        var agent = new MemoryChatAgent([provider], memories, pages, Options.Create(new MemorySmithOptions
        {
            Chat = new ChatOptions { AgentWritesEnabled = true }
        }), new FakeCurrentUserContext("editor-1", "Editor User", [MemorySmithRoles.Editor]));

        var response = await agent.SendAsync(new MemoryChatRequest("Capture this", MemoryChatMode.Agent), CancellationToken.None);
        var writtenMemory = memoryStore.Load("agent-note");
        var writtenPage = await pages.GetAsync("agent-page", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(response.Reply, Is.EqualTo("Recorded."));
            Assert.That(response.WrittenMemories, Is.EqualTo(new[] { "agent-note" }));
            Assert.That(response.WrittenPages, Is.EqualTo(new[] { "agent-page" }));
            Assert.That(writtenMemory, Is.Not.Null);
            Assert.That(writtenMemory!.Status, Is.EqualTo(MemoryStatus.Core));
            Assert.That(writtenPage, Is.Not.Null);
            Assert.That(writtenPage!.Html, Does.Contain(">Agent Page</h1>"));
            Assert.That(provider.LastRequest!.Messages.Any(message => message.Content.Contains("Local MemorySmith context", StringComparison.Ordinal)), Is.True);
        });
    }

    [Test]
    public async Task MemoryChatAgent_AgentModeSynthesizesReplyWhenProviderReplyIsEmpty()
    {
        var memoryStore = new InMemoryMemoryStore();
        var eventStore = new RecordingEventStore();
        var publisher = new RecordingMemoryChangePublisher();
        var memories = TestServiceFactory.CreateMemoryApplicationService(memoryStore, eventStore, publisher);
        var pages = new FilePageService(_tempDir);
        var provider = new FakeChatProvider("""
        {
          "reply": null,
          "memoryWrites": [],
          "pageWrites": [
            { "slug": "agent-page", "title": "Agent Page", "markdown": "Agent page body." }
          ]
        }
        """);
        var agent = new MemoryChatAgent([provider], memories, pages, Options.Create(new MemorySmithOptions
        {
            Chat = new ChatOptions { AgentWritesEnabled = true }
        }), new FakeCurrentUserContext("editor-1", "Editor User", [MemorySmithRoles.Editor]));

        var response = await agent.SendAsync(new MemoryChatRequest("Create a page", MemoryChatMode.Agent), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(response.Reply, Is.EqualTo("Created or updated page agent-page."));
            Assert.That(response.WrittenPages, Is.EqualTo(new[] { "agent-page" }));
        });
    }

    [Test]
    public async Task MemoryChatAgent_ReturnsProposedWritesWhenApprovalRequired()
    {
        var memoryStore = new InMemoryMemoryStore();
        var eventStore = new RecordingEventStore();
        var publisher = new RecordingMemoryChangePublisher();
        var memories = TestServiceFactory.CreateMemoryApplicationService(memoryStore, eventStore, publisher);
        var pages = new FilePageService(_tempDir);
        var provider = new FakeChatProvider("""
        {
            "reply": "Ready to record.",
            "memoryWrites": [
                { "id": "agent-approval-note", "title": "Agent Approval Note", "content": "Approve this from chat.", "tags": ["agent"], "status": "Core", "confidence": 0.9 }
            ],
            "pageWrites": [
                { "slug": "agent-approval-page", "title": "Agent Approval Page", "markdown": "Approval page body." }
            ]
        }
        """);
        var agent = new MemoryChatAgent([provider], memories, pages, Options.Create(new MemorySmithOptions
        {
            Chat = new ChatOptions { AgentWritesEnabled = true }
        }), new FakeCurrentUserContext("editor-1", "Editor User", [MemorySmithRoles.Editor]));

        var response = await agent.SendAsync(new MemoryChatRequest("Capture this", MemoryChatMode.Agent, RequireAgentWriteApproval: true), CancellationToken.None);
        var missingBeforeApproval = await pages.GetAsync("agent-approval-page", CancellationToken.None);
        var applied = await agent.ApplyAgentWritesAsync(response.ProposedMemoryWrites!, response.ProposedPageWrites!, CancellationToken.None);
        var writtenPage = await pages.GetAsync("agent-approval-page", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(response.Reply, Is.EqualTo("2 write proposals are ready for review. No memories or pages have been changed yet; approve the proposed write(s) in MemorySmith to apply them."));
            Assert.That(response.WrittenMemories, Is.Empty);
            Assert.That(response.WrittenPages, Is.Empty);
            Assert.That(response.ProposedMemoryWrites, Has.Count.EqualTo(1));
            Assert.That(response.ProposedPageWrites, Has.Count.EqualTo(1));
            Assert.That(memoryStore.Load("agent-approval-note"), Is.Not.Null);
            Assert.That(missingBeforeApproval, Is.Null);
            Assert.That(applied.WrittenMemories, Is.EqualTo(new[] { "agent-approval-note" }));
            Assert.That(applied.WrittenPages, Is.EqualTo(new[] { "agent-approval-page" }));
            Assert.That(writtenPage, Is.Not.Null);
        });
    }

    [Test]
    public async Task MemoryChatAgent_AgentModeApprovalReplyDoesNotClaimWritesWereApplied()
    {
        var memoryStore = new InMemoryMemoryStore();
        var eventStore = new RecordingEventStore();
        var publisher = new RecordingMemoryChangePublisher();
        var memories = TestServiceFactory.CreateMemoryApplicationService(memoryStore, eventStore, publisher);
        var pages = new FilePageService(_tempDir);
        var provider = new FakeChatProvider("""
        {
            "reply": "Created the page.",
            "memoryWrites": [],
            "pageWrites": [
                { "slug": "pending-page", "title": "Pending Page", "markdown": "Pending page body." }
            ]
        }
        """);
        var agent = new MemoryChatAgent([provider], memories, pages, Options.Create(new MemorySmithOptions
        {
            Chat = new ChatOptions { AgentWritesEnabled = true }
        }), new FakeCurrentUserContext("editor-1", "Editor User", [MemorySmithRoles.Editor]));

        var response = await agent.SendAsync(new MemoryChatRequest("Create a page", MemoryChatMode.Agent, RequireAgentWriteApproval: true), CancellationToken.None);
        var writtenPage = await pages.GetAsync("pending-page", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(response.Reply, Does.Contain("ready for review"));
            Assert.That(response.Reply, Does.Contain("No memories or pages have been changed yet"));
            Assert.That(response.Reply, Does.Not.Contain("Created the page"));
            Assert.That(response.WrittenPages, Is.Empty);
            Assert.That(response.ProposedPageWrites, Has.Count.EqualTo(1));
            Assert.That(writtenPage, Is.Null);
        });
    }

    [Test]
    public async Task MemoryChatAgent_AgentWritesAreDisabledByDefault()
    {
        var memoryStore = new InMemoryMemoryStore();
        var eventStore = new RecordingEventStore();
        var publisher = new RecordingMemoryChangePublisher();
        var memories = TestServiceFactory.CreateMemoryApplicationService(memoryStore, eventStore, publisher);
        var pages = new FilePageService(_tempDir);
        var provider = new FakeChatProvider("""
        {
            "reply": "Recorded.",
            "memoryWrites": [
                { "id": "agent-note", "title": "Agent Note", "content": "Remember this from chat." }
            ],
            "pageWrites": [
                { "slug": "agent-page", "title": "Agent Page", "markdown": "Agent page body." }
            ]
        }
        """);
        var agent = new MemoryChatAgent([provider], memories, pages, Options.Create(new MemorySmithOptions()));

        var response = await agent.SendAsync(new MemoryChatRequest("Capture this", MemoryChatMode.Agent), CancellationToken.None);
        var writtenPage = await pages.GetAsync("agent-page", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(response.Reply, Is.EqualTo("Recorded.\n\nAgent write actions are disabled; no memories or pages were changed."));
            Assert.That(response.WrittenMemories, Is.Empty);
            Assert.That(response.WrittenPages, Is.Empty);
            Assert.That(memoryStore.Load("agent-note"), Is.Null);
            Assert.That(writtenPage, Is.Null);
        });
    }

    [Test]
    public async Task MemoryChatAgent_UsesModelOverridePromptFileAndAttachments()
    {
        var memoryStore = new InMemoryMemoryStore();
        var eventStore = new RecordingEventStore();
        var publisher = new RecordingMemoryChangePublisher();
        var memories = TestServiceFactory.CreateMemoryApplicationService(memoryStore, eventStore, publisher);
        var pages = new FilePageService(_tempDir);
        var promptPath = Path.Combine(_tempDir, "prompt.md");
        await File.WriteAllTextAsync(promptPath, "Use the project wiki prompt.");
        var provider = new FakeChatProvider("Done.", thinking: "private reasoning");
        var options = Options.Create(new MemorySmithOptions
        {
            Chat = new ChatOptions
            {
                SystemPromptPath = promptPath,
                MaxAttachmentCharacters = 1000
            }
        });
        var agent = new MemoryChatAgent([provider], memories, pages, options);

        var response = await agent.SendAsync(new MemoryChatRequest(
            "Summarize this",
            MemoryChatMode.Chat,
            Model: "custom-model",
            Attachments: [new ChatAttachment("notes.md", "text/markdown", "Attached note body", 18)]), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(response.Model, Is.EqualTo("custom-model"));
            Assert.That(response.Thinking, Is.EqualTo("private reasoning"));
            Assert.That(response.Usage, Is.Not.Null);
            Assert.That(response.Usage!.IsEstimate, Is.True);
            Assert.That(response.Usage.InputTokens, Is.GreaterThan(0));
            Assert.That(provider.LastRequest!.Model, Is.EqualTo("custom-model"));
            Assert.That(provider.LastRequest.Messages.Any(message => message.Content.Contains("Use the project wiki prompt.", StringComparison.Ordinal)), Is.True);
            Assert.That(provider.LastRequest.Messages.Any(message => message.Content.Contains("memorysmith_unified_search", StringComparison.Ordinal)), Is.True);
            Assert.That(provider.LastRequest.Messages.Any(message => message.Content.Contains("Mermaid diagrams", StringComparison.Ordinal)), Is.True);
            Assert.That(provider.LastRequest.Messages.Any(message => message.Content.Contains("Attached note body", StringComparison.Ordinal)), Is.True);
        });
    }

    [Test]
    public async Task MemoryChatAgent_IncludesCurrentUserInProviderMessages()
    {
        var memoryStore = new InMemoryMemoryStore();
        var eventStore = new RecordingEventStore();
        var publisher = new RecordingMemoryChangePublisher();
        var memories = TestServiceFactory.CreateMemoryApplicationService(memoryStore, eventStore, publisher);
        var pages = new FilePageService(_tempDir);
        var provider = new FakeChatProvider("Done.");
        var currentUser = new FakeCurrentUserContext("user-1", "Signed In User", [MemorySmithRoles.Admin]);
        var agent = new MemoryChatAgent([provider], memories, pages, Options.Create(new MemorySmithOptions()), currentUser);

        await agent.SendAsync(new MemoryChatRequest("Who am I?", MemoryChatMode.Chat), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(provider.LastRequest!.Messages.Any(message => message.Content.Contains("Current MemorySmith user: Signed In User", StringComparison.Ordinal)), Is.True);
            Assert.That(provider.LastRequest.Messages.Any(message => message.Content.Contains("Current MemorySmith capabilities and limits", StringComparison.Ordinal)), Is.True);
            Assert.That(provider.LastRequest.Messages.Any(message => message.Content.Contains("Chat mode cannot create, update, or delete MemorySmith memories or pages", StringComparison.Ordinal)), Is.True);
        });
    }

    [Test]
    public async Task MemoryChatAgent_HydratesMemoryContextBeyondSearchSnippet()
    {
        var memoryStore = new InMemoryMemoryStore();
        var eventStore = new RecordingEventStore();
        var publisher = new RecordingMemoryChangePublisher();
        var memories = TestServiceFactory.CreateMemoryApplicationService(memoryStore, eventStore, publisher);
        memoryStore.Save(new MemoryRecord
        {
            Id = "chat-provider-context",
            Title = "Chat Provider Context",
            Status = MemoryStatus.Core,
            Content = "MemorySmith chat context starts with a broad architecture summary. "
                + new string('x', 300)
                + " GitHub model ordering prefers free GPTs first, then Claude Haiku before Sonnet, and compact chat history titles persist locally."
        });
        var pages = new FilePageService(_tempDir);
        var provider = new FakeChatProvider("Done.");
        var options = Options.Create(new MemorySmithOptions
        {
            Chat = new ChatOptions
            {
                MaxContextItemCharacters = 1000
            }
        });
        var agent = new MemoryChatAgent([provider], memories, pages, options);

        await agent.SendAsync(new MemoryChatRequest("What does the MemorySmith wiki say about chat GitHub Haiku Sonnet?", MemoryChatMode.Chat), CancellationToken.None);
        var contextMessage = provider.LastRequest!.Messages.Single(message => message.Content.StartsWith("Local MemorySmith context", StringComparison.Ordinal));

        Assert.That(contextMessage.Content, Does.Contain("Claude Haiku before Sonnet"));
    }

    [Test]
    public async Task MemoryChatAgent_SkipsPreloadedContextForSimpleDirectPrompt()
    {
        var memoryStore = new InMemoryMemoryStore();
        var eventStore = new RecordingEventStore();
        var publisher = new RecordingMemoryChangePublisher();
        var memories = TestServiceFactory.CreateMemoryApplicationService(memoryStore, eventStore, publisher);
        memoryStore.Save(new MemoryRecord
        {
            Id = "direct-prompt-noise",
            Title = "Direct Prompt Noise",
            Status = MemoryStatus.Core,
            Content = "This local wiki record should not be sent for an exact-reply smoke prompt."
        });
        var pages = new FilePageService(_tempDir);
        var provider = new FakeChatProvider("DIRECT_OK");
        var agent = new MemoryChatAgent([provider], memories, pages, Options.Create(new MemorySmithOptions()));

        var response = await agent.SendAsync(new MemoryChatRequest("Reply exactly: DIRECT_OK", MemoryChatMode.Chat), CancellationToken.None);
        var contextMessage = provider.LastRequest!.Messages.Single(message => message.Content.StartsWith("Local MemorySmith context", StringComparison.Ordinal));

        Assert.Multiple(() =>
        {
            Assert.That(response.Context, Is.Empty);
            Assert.That(contextMessage.Content, Does.Contain("no memories or pages were preloaded"));
            Assert.That(contextMessage.Content, Does.Not.Contain("exact-reply smoke prompt"));
        });
    }

    [Test]
    public async Task MemoryChatAgent_BoundsPreloadedContextForLocalKnowledgePrompt()
    {
        var memoryStore = new InMemoryMemoryStore();
        var eventStore = new RecordingEventStore();
        var publisher = new RecordingMemoryChangePublisher();
        var memories = TestServiceFactory.CreateMemoryApplicationService(memoryStore, eventStore, publisher);
        for (var index = 1; index <= 3; index++)
        {
            memoryStore.Save(new MemoryRecord
            {
                Id = $"bounded-context-{index}",
                Title = $"Bounded Context {index}",
                Status = MemoryStatus.Core,
                Content = "MemorySmith wiki context should be bounded before the model call."
            });
        }
        var pages = new FilePageService(_tempDir);
        var provider = new FakeChatProvider("Done.");
        var agent = new MemoryChatAgent([provider], memories, pages, Options.Create(new MemorySmithOptions
        {
            Chat = new ChatOptions
            {
                MaxContextRecords = 5,
                MaxPreloadedContextRecords = 1,
                MaxContextPages = 0,
                MaxPreloadedContextPages = 0
            }
        }));

        var response = await agent.SendAsync(new MemoryChatRequest("What does the MemorySmith wiki say about bounded context?", MemoryChatMode.Chat), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(response.Context, Has.Count.EqualTo(1));
            Assert.That(response.Context.Single().Origin, Is.EqualTo(ChatContextOrigins.Preloaded));
        });
    }

    [Test]
    public void ChatContextPlanner_PageIntent_UsesOnlyPageBudgetAndPageSearchFallback()
    {
        var plan = ChatContextPlanner.Plan(
            new MemoryChatRequest("What does the wiki page say about deployment docs?", MemoryChatMode.Chat),
            new ChatOptions
            {
                MaxContextRecords = 5,
                MaxPreloadedContextRecords = 2,
                MaxContextPages = 5,
                MaxPreloadedContextPages = 2
            },
            new ChatIntentInterceptor());

        Assert.Multiple(() =>
        {
            Assert.That(plan.ShouldPreload, Is.True);
            Assert.That(plan.MemoryLimit, Is.EqualTo(0));
            Assert.That(plan.PageLimit, Is.EqualTo(2));
            Assert.That(plan.RecommendedToolName, Is.EqualTo("memorysmith_page_search"));
        });
    }

    [Test]
    public async Task MemoryChatAgent_ContextPlannerSkipsMemoryPreloadForPageOnlyPrompt()
    {
        var memoryStore = new InMemoryMemoryStore();
        var eventStore = new RecordingEventStore();
        var publisher = new RecordingMemoryChangePublisher();
        var memories = TestServiceFactory.CreateMemoryApplicationService(memoryStore, eventStore, publisher);
        memoryStore.Save(new MemoryRecord
        {
            Id = "page-only-noise",
            Title = "Deployment Docs Noise",
            Status = MemoryStatus.Core,
            Content = "This memory record should not be preloaded for a page-only planner prompt."
        });
        var pages = new FilePageService(_tempDir);
        await pages.SaveAsync(new PageSaveRequest("deployment-docs", "Deployment Docs", "Page-only planner evidence."), CancellationToken.None);

        var provider = new FakeChatProvider("Done.");
        var agent = new MemoryChatAgent([provider], memories, pages, Options.Create(new MemorySmithOptions
        {
            Chat = new ChatOptions
            {
                MaxContextRecords = 5,
                MaxPreloadedContextRecords = 2,
                MaxContextPages = 5,
                MaxPreloadedContextPages = 1
            }
        }));

        var response = await agent.SendAsync(new MemoryChatRequest("What does the wiki page say about deployment docs?", MemoryChatMode.Chat), CancellationToken.None);
        var capabilityMessage = provider.LastRequest!.Messages.Single(message => message.Content.StartsWith("Current MemorySmith capabilities", StringComparison.Ordinal));

        Assert.Multiple(() =>
        {
            Assert.That(response.Context.Select(item => item.Kind), Is.EquivalentTo(new[] { "page" }));
            Assert.That(response.Context.Single().Id, Is.EqualTo("deployment-docs"));
            Assert.That(capabilityMessage.Content, Does.Contain("Context planner"));
            Assert.That(capabilityMessage.Content, Does.Contain("memorysmith_page_search"));
        });
    }

    [Test]
    public async Task MemoryChatAgent_StreamTraceShowsContextPlannerSkipReason()
    {
        var memoryStore = new InMemoryMemoryStore();
        var eventStore = new RecordingEventStore();
        var publisher = new RecordingMemoryChangePublisher();
        var memories = TestServiceFactory.CreateMemoryApplicationService(memoryStore, eventStore, publisher);
        var pages = new FilePageService(_tempDir);
        var provider = new FakeChatProvider("DIRECT_OK");
        var agent = new MemoryChatAgent([provider], memories, pages, Options.Create(new MemorySmithOptions()));

        var traceEvents = new List<ChatTraceEvent>();
        await foreach (var update in agent.StreamAsync(new MemoryChatRequest("Reply exactly: DIRECT_OK", MemoryChatMode.Chat), CancellationToken.None))
        {
            if (update.TraceEvents is not null)
            {
                traceEvents.AddRange(update.TraceEvents);
            }
        }

        var plannerTrace = traceEvents.Single(trace => trace.Title == "Context planner");
        Assert.Multiple(() =>
        {
            Assert.That(plannerTrace.Content, Does.Contain("direct/simple reply"));
            Assert.That(plannerTrace.Content, Does.Contain("Recommended tool: memorysmith_unified_search"));
        });
    }

    [Test]
    public void ChatProviders_ReportCapabilityMetadata()
    {
        var options = new StaticOptionsMonitor<MemorySmithOptions>(new MemorySmithOptions());
        var ollama = new OllamaChatProvider(new HttpClient(new CapturingHandler()), options);
        var github = new GitHubCopilotChatProvider(options);

        Assert.Multiple(() =>
        {
            Assert.That(ollama.Capabilities.SupportsStreaming, Is.True);
            Assert.That(ollama.Capabilities.SupportsImageInput, Is.True);
            Assert.That(ollama.Capabilities.SupportsNativeToolCalls, Is.False);
            Assert.That(github.Capabilities.ReportsContextWindowUsage, Is.True);
            Assert.That(github.Capabilities.NativeToolCallStatus, Does.Contain("SDK"));
        });
    }

    [Test]
    public async Task MemoryChatAgent_PreloadsVisiblePagesBeyondFirstTwoHundredHiddenResults()
    {
        var memoryStore = new InMemoryMemoryStore();
        var eventStore = new RecordingEventStore();
        var publisher = new RecordingMemoryChangePublisher();
        var memories = TestServiceFactory.CreateMemoryApplicationService(memoryStore, eventStore, publisher);
        var pages = new FilePageService(_tempDir);
        const string query = "crowded preload visibility token";
        await PageVisibilitySearchFixture.SeedAsync(pages, query, CancellationToken.None);

        var provider = new FakeChatProvider("Done.");
        var agent = new MemoryChatAgent([provider], memories, pages, Options.Create(new MemorySmithOptions
        {
            Chat = new ChatOptions
            {
                MaxContextRecords = 0,
                MaxPreloadedContextRecords = 0,
                MaxContextPages = 2,
                MaxPreloadedContextPages = 2
            }
        }));

        var response = await agent.SendAsync(new MemoryChatRequest($"What does the MemorySmith wiki say about {query}?", MemoryChatMode.Chat), CancellationToken.None);
        var pageContextIds = response.Context.Where(item => item.Kind == "page").Select(item => item.Id).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(pageContextIds, Has.Length.EqualTo(PageVisibilitySearchFixture.PublicPageSlugs.Length));
            Assert.That(pageContextIds, Is.EquivalentTo(PageVisibilitySearchFixture.PublicPageSlugs));
        });
    }

    [Test]
    public async Task MemoryChatAgent_ExecutesInterceptedToolCallsBeforeReturningReply()
    {
        var memoryStore = new InMemoryMemoryStore();
        var eventStore = new RecordingEventStore();
        var publisher = new RecordingMemoryChangePublisher();
        var memories = TestServiceFactory.CreateMemoryApplicationService(memoryStore, eventStore, publisher);
        memoryStore.Save(new MemoryRecord
        {
            Id = "tool-target",
            Title = "Tool Target",
            Status = MemoryStatus.Core,
            Content = "Durable tool-call evidence lives in this wiki record.",
            Tags = ["project-wiki", "tooling"]
        });
        var pages = new FilePageService(_tempDir);
        var provider = new SequencedChatProvider(
            """
            {"toolCalls":[{"name":"memorysmith_hybrid_search","arguments":{"query":"durable tool-call evidence","limit":5}}]}
            """,
            "Found the durable tool-call evidence in tool-target.");
        var agent = new MemoryChatAgent([provider], memories, pages, Options.Create(new MemorySmithOptions()));

        var response = await agent.SendAsync(new MemoryChatRequest("Search for durable tool-call evidence", MemoryChatMode.Chat), CancellationToken.None);
        var toolResultMessage = provider.Requests[1].Messages.Single(message => message.Content.StartsWith("Local MemorySmith tool results", StringComparison.Ordinal));

        Assert.Multiple(() =>
        {
            Assert.That(response.Reply, Is.EqualTo("Found the durable tool-call evidence in tool-target."));
            Assert.That(response.Reply, Does.Not.Contain("toolCalls"));
            Assert.That(provider.Requests, Has.Count.EqualTo(2));
            Assert.That(toolResultMessage.Content, Does.Contain("memorysmith_hybrid_search"));
            Assert.That(toolResultMessage.Content, Does.Contain("tool-target"));
            Assert.That(response.Context.Select(item => item.Id), Does.Contain("tool-target"));
            Assert.That(response.Context.Single(item => item.Id == "tool-target").Origin, Is.EqualTo(ChatContextOrigins.Tool));
        });
    }

    [Test]
    public async Task MemoryChatAgent_ExecutesInlineFencedToolCallJson()
    {
        var memoryStore = new InMemoryMemoryStore();
        var eventStore = new RecordingEventStore();
        var publisher = new RecordingMemoryChangePublisher();
        var memories = TestServiceFactory.CreateMemoryApplicationService(memoryStore, eventStore, publisher);
        memoryStore.Save(new MemoryRecord
        {
            Id = "fenced-tool-target",
            Title = "Fenced Tool Target",
            Status = MemoryStatus.Core,
            Content = "Inline fenced JSON tool calls should still execute.",
            Tags = ["project-wiki", "tooling"]
        });
        var pages = new FilePageService(_tempDir);
        var provider = new SequencedChatProvider(
            """```json {"toolCalls":[{"name":"memorysmith_get","arguments":{"id":"fenced-tool-target"}}]} ```""",
            "fenced-tool-target was fetched.");
        var agent = new MemoryChatAgent([provider], memories, pages, Options.Create(new MemorySmithOptions()));

        var response = await agent.SendAsync(new MemoryChatRequest("Use a fenced tool call", MemoryChatMode.Chat), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(response.Reply, Is.EqualTo("fenced-tool-target was fetched."));
            Assert.That(provider.Requests, Has.Count.EqualTo(2));
            Assert.That(response.Context.Select(item => item.Id), Does.Contain("fenced-tool-target"));
        });
    }

    [Test]
    public async Task MemoryChatAgent_StreamsToolCallStatusWithoutLeakingToolJson()
    {
        var memoryStore = new InMemoryMemoryStore();
        var eventStore = new RecordingEventStore();
        var publisher = new RecordingMemoryChangePublisher();
        var memories = TestServiceFactory.CreateMemoryApplicationService(memoryStore, eventStore, publisher);
        memoryStore.Save(new MemoryRecord
        {
            Id = "stream-tool-target",
            Title = "Stream Tool Target",
            Status = MemoryStatus.Core,
            Content = "Streaming tool-call evidence is searchable inside the same user turn.",
            Tags = ["project-wiki", "tooling"]
        });
        var pages = new FilePageService(_tempDir);
        var provider = new SequencedChatProvider(
            """
            {"jsonrpc":"2.0","id":"search","method":"tools/call","params":{"name":"memorysmith_get","arguments":{"id":"stream-tool-target"}}}
            """,
            "stream-tool-target has the evidence.");
        var agent = new MemoryChatAgent([provider], memories, pages, Options.Create(new MemorySmithOptions()));

        var updates = new List<MemoryChatStreamUpdate>();
        await foreach (var update in agent.StreamAsync(new MemoryChatRequest("Look up the streaming tool target", MemoryChatMode.Chat), CancellationToken.None))
        {
            updates.Add(update);
        }

        var visibleDeltas = string.Concat(updates.Select(update => update.ContentDelta));
        var final = updates.Single(update => update.IsFinal).Response!;
        var traceEvents = updates
            .SelectMany(update => update.TraceEvents ?? [])
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(visibleDeltas, Does.Not.Contain("tools/call"));
            Assert.That(updates.Select(update => update.Status).Where(status => !string.IsNullOrWhiteSpace(status)), Does.Contain("Ran 1 MemorySmith wiki tool call(s): memorysmith_get"));
            Assert.That(traceEvents.Any(trace => trace.Kind == ChatTraceKinds.ToolCall && trace.Title.Contains("memorysmith_get", StringComparison.Ordinal)), Is.True);
            Assert.That(traceEvents.Any(trace => trace.Kind == ChatTraceKinds.ToolResult && trace.Title.Contains("memorysmith_get", StringComparison.Ordinal)), Is.True);
            Assert.That(final.Reply, Is.EqualTo("stream-tool-target has the evidence."));
            Assert.That(provider.Requests, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public async Task MemoryChatAgent_StopAfterCurrentStepSkipsRequestedToolCalls()
    {
        var memoryStore = new InMemoryMemoryStore();
        var eventStore = new RecordingEventStore();
        var publisher = new RecordingMemoryChangePublisher();
        var memories = TestServiceFactory.CreateMemoryApplicationService(memoryStore, eventStore, publisher);
        memoryStore.Save(new MemoryRecord
        {
            Id = "stop-step-target",
            Title = "Stop Step Target",
            Status = MemoryStatus.Core,
            Content = "This should not be fetched when stop-after-step is already requested."
        });
        var pages = new FilePageService(_tempDir);
        var provider = new SequencedChatProvider(
            """
            {"toolCalls":[{"name":"memorysmith_get","arguments":{"id":"stop-step-target"}}]}
            """,
            "This should not be reached.");
        var runControl = new ChatRunControl();
        runControl.RequestStopAfterCurrentStep();
        var agent = new MemoryChatAgent([provider], memories, pages, Options.Create(new MemorySmithOptions()));

        var updates = new List<MemoryChatStreamUpdate>();
        await foreach (var update in agent.StreamAsync(new MemoryChatRequest("Continue this turn", MemoryChatMode.Chat, RunControl: runControl), CancellationToken.None))
        {
            updates.Add(update);
        }

        var final = updates.Single(update => update.IsFinal).Response!;
        var traceEvents = updates.SelectMany(update => update.TraceEvents ?? []).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(provider.Requests, Has.Count.EqualTo(1));
            Assert.That(final.Reply, Is.EqualTo("Stopped before running the requested MemorySmith wiki tool call(s)."));
            Assert.That(final.Context.Select(item => item.Id), Does.Not.Contain("stop-step-target"));
            Assert.That(traceEvents.Any(trace => trace.Title == "Stop after step"), Is.True);
        });
    }

    [Test]
    public async Task MemoryChatAgent_PassesImageAttachmentsToProvider()
    {
        var memoryStore = new InMemoryMemoryStore();
        var eventStore = new RecordingEventStore();
        var publisher = new RecordingMemoryChangePublisher();
        var memories = TestServiceFactory.CreateMemoryApplicationService(memoryStore, eventStore, publisher);
        var pages = new FilePageService(_tempDir);
        var provider = new FakeChatProvider("Seen.");
        var agent = new MemoryChatAgent([provider], memories, pages, Options.Create(new MemorySmithOptions()));

        await agent.SendAsync(new MemoryChatRequest(
            "What is in this image?",
            Attachments: [new ChatAttachment("sample.png", "image/png", "Image payload", 4, "aW1hZ2U=", IsImage: true)]), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(provider.LastRequest!.Attachments, Is.Not.Null);
            Assert.That(provider.LastRequest.Attachments!.Single().IsImage, Is.True);
            Assert.That(provider.LastRequest.Messages.Any(message => message.Content.Contains("native image payload", StringComparison.Ordinal)), Is.True);
        });
    }

    [Test]
    public void ChatErrorMessages_ExplainGitHubModelAndAuthenticationFailures()
    {
        var unavailable = ChatErrorMessages.Format(
            new InvalidOperationException("Request session.create failed with message: Model \"gpt-4.1\" is not available."),
            "GitHub",
            "gpt-4.1");
        var auth = ChatErrorMessages.Format(
            new InvalidOperationException("Session was not created with authentication info or custom provider"),
            "GitHub",
            "gpt-4.1");

        Assert.Multiple(() =>
        {
            Assert.That(unavailable, Does.Contain("free GPTs"));
            Assert.That(unavailable, Does.Contain("Claude Haiku"));
            Assert.That(auth, Does.Contain("GITHUB_TOKEN"));
            Assert.That(auth, Does.Contain("COPILOT_API_KEY"));
        });
    }

    [Test]
    public async Task OllamaChatProvider_SendsImagePayloadsToLastUserMessage()
    {
        var handler = new CapturingHandler();
        var provider = new OllamaChatProvider(new HttpClient(handler), new StaticOptionsMonitor<MemorySmithOptions>(new MemorySmithOptions
        {
            Chat = new ChatOptions
            {
                OllamaEndpoint = "http://localhost:11434",
                OllamaModel = "vision-model"
            }
        }));

        await provider.CompleteAsync(new ChatProviderRequest(
            [new ChatMessage("system", "prompt"), new ChatMessage("user", "describe")],
            MemoryChatMode.Chat,
            Attachments: [new ChatAttachment("image.png", "image/png", "image", 5, "abc123", IsImage: true)]), CancellationToken.None);

        Assert.That(handler.Body, Does.Contain("\"images\":[\"abc123\"]"));
    }

    [Test]
    public async Task OllamaChatProvider_StreamsLiveChunks()
    {
        var handler = new CapturingHandler("""
        {"message":{"content":"hel"},"done":false}
        {"message":{"content":"lo"},"done":false}
        {"done":true,"prompt_eval_count":23,"eval_count":5}
        """);
        var provider = new OllamaChatProvider(new HttpClient(handler), new StaticOptionsMonitor<MemorySmithOptions>(new MemorySmithOptions
        {
            Chat = new ChatOptions
            {
                OllamaEndpoint = "http://localhost:11434",
                OllamaModel = "stream-model"
            }
        }));

        var chunks = new List<ChatProviderChunk>();
        await foreach (var chunk in provider.StreamAsync(new ChatProviderRequest([new ChatMessage("user", "hello")], MemoryChatMode.Chat), CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Multiple(() =>
        {
            Assert.That(chunks.Where(chunk => !chunk.IsFinal).Select(chunk => chunk.ContentDelta), Is.EqualTo(new[] { "hel", "lo" }));
            Assert.That(chunks.Single(chunk => chunk.IsFinal).FinalContent, Is.EqualTo("hello"));
            Assert.That(chunks.Single(chunk => chunk.IsFinal).Usage, Is.EqualTo(new ChatUsageSummary(23, 5, 23, IsEstimate: false)));
            Assert.That(handler.Body, Does.Contain("\"stream\":true"));
        });
    }

    [Test]
    public async Task OllamaChatProvider_LoadsImagePayloadsFromTrustedTempFiles()
    {
        var handler = new CapturingHandler();
        var provider = new OllamaChatProvider(new HttpClient(handler), new StaticOptionsMonitor<MemorySmithOptions>(new MemorySmithOptions
        {
            Chat = new ChatOptions
            {
                OllamaEndpoint = "http://localhost:11434",
                OllamaModel = "vision-model"
            }
        }));
        var tempPath = await ChatAttachmentFiles.SaveTempAsync("image.png", [1, 2, 3], CancellationToken.None);

        await provider.CompleteAsync(new ChatProviderRequest(
            [new ChatMessage("user", "describe")],
            MemoryChatMode.Chat,
            Attachments: [new ChatAttachment("image.png", "image/png", "image", 3, IsImage: true, LocalPath: tempPath)]), CancellationToken.None);

        Assert.That(handler.Body, Does.Contain("\"images\":[\"AQID\"]"));
    }

    [Test]
    public async Task MemoryChatAgent_ApplyAgentWritesThrowsForViewerRole()
    {
        var memoryStore = new InMemoryMemoryStore();
        var eventStore = new RecordingEventStore();
        var publisher = new RecordingMemoryChangePublisher();
        var memories = TestServiceFactory.CreateMemoryApplicationService(memoryStore, eventStore, publisher);
        var pages = new FilePageService(_tempDir);
        var provider = new FakeChatProvider("""
        {
            "reply": "Ready.",
            "memoryWrites": [
                { "id": "viewer-note", "title": "Viewer Note", "content": "Should not be written." }
            ],
            "pageWrites": []
        }
        """);
        var viewerUser = new FakeCurrentUserContext("viewer-1", "Viewer User", [MemorySmithRoles.Viewer]);
        var agent = new MemoryChatAgent([provider], memories, pages, Options.Create(new MemorySmithOptions
        {
            Chat = new ChatOptions { AgentWritesEnabled = true }
        }), viewerUser);

        var response = await agent.SendAsync(new MemoryChatRequest("Capture this", MemoryChatMode.Agent, RequireAgentWriteApproval: true), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(response.ProposedMemoryWrites, Has.Count.EqualTo(0), "Viewer should not receive proposals");
            Assert.That(response.Reply, Does.Contain("cannot approve"), "Reply should explain role restriction");
            Assert.ThrowsAsync<InvalidOperationException>(() =>
                agent.ApplyAgentWritesAsync([], [], CancellationToken.None),
                "Viewer calling ApplyAgentWritesAsync should throw");
            Assert.That(memoryStore.Load("viewer-note"), Is.Null, "No memory should be written");
        });
    }

    private sealed class FakeChatProvider : IChatProvider
    {
        private readonly string _content;
        private readonly string? _thinking;

        public FakeChatProvider(string content, string? thinking = null)
        {
            _content = content;
            _thinking = thinking;
        }

        public string Name => "Fake";
        public ChatProviderRequest? LastRequest { get; private set; }

        public Task<ChatProviderResponse> CompleteAsync(ChatProviderRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new ChatProviderResponse(_content, Name, request.Model ?? "fake-model", _thinking));
        }

        public Task<IReadOnlyList<ChatModelSummary>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ChatModelSummary>>([new ChatModelSummary("fake-model")]);

        public async IAsyncEnumerable<ChatProviderChunk> StreamAsync(ChatProviderRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastRequest = request;
            await Task.Yield();
            yield return new ChatProviderChunk(_content, _thinking, _content, _thinking, IsFinal: true, Name, request.Model ?? "fake-model");
        }
    }

    private sealed class SequencedChatProvider : IChatProvider
    {
        private readonly Queue<string> _responses;

        public SequencedChatProvider(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        public string Name => "Fake";
        public List<ChatProviderRequest> Requests { get; } = [];

        public Task<ChatProviderResponse> CompleteAsync(ChatProviderRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var content = NextResponse();
            return Task.FromResult(new ChatProviderResponse(content, Name, request.Model ?? "fake-model"));
        }

        public Task<IReadOnlyList<ChatModelSummary>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ChatModelSummary>>([new ChatModelSummary("fake-model")]);

        public async IAsyncEnumerable<ChatProviderChunk> StreamAsync(ChatProviderRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var content = NextResponse();
            await Task.Yield();
            yield return new ChatProviderChunk(content, null, null, null, IsFinal: false, Name, request.Model ?? "fake-model");
            yield return new ChatProviderChunk(string.Empty, null, content, null, IsFinal: true, Name, request.Model ?? "fake-model");
        }

        private string NextResponse()
        {
            if (_responses.Count == 0)
            {
                return string.Empty;
            }

            return _responses.Count == 1 ? _responses.Peek() : _responses.Dequeue();
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public CapturingHandler(string responseBody = "{\"message\":{\"content\":\"ok\"}}")
        {
            _responseBody = responseBody;
        }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody)
            };
        }
    }

    private sealed class FakeCurrentUserContext : ICurrentUserContext
    {
        public FakeCurrentUserContext(string userId, string displayName, IReadOnlyList<string> roles)
        {
            UserId = userId;
            DisplayName = displayName;
            Roles = roles;
        }

        public string? UserId { get; }
        public string DisplayName { get; }
        public string AuthScheme => "Test";
        public string? Provider => MemorySmithProviders.LocalPassword;
        public bool IsAuthenticated => true;
        public IReadOnlyList<string> Roles { get; }
        public string ActorKind => MemorySmithActorKinds.User;
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value)
        {
            CurrentValue = value;
        }

        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
