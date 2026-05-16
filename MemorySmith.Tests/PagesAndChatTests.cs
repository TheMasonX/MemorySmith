using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using Microsoft.Extensions.Options;

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
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.Html, Does.Contain(">Design Notes</h1>"));
            Assert.That(loaded.Html, Does.Contain("/page-assets/diagram.png"));
            Assert.That(search.Select(page => page.Slug), Does.Contain("design-notes"));
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
        var agent = new MemoryChatAgent(provider, memories, pages, Options.Create(new MemorySmithOptions()));

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

    private sealed class FakeChatProvider : IChatProvider
    {
        private readonly string _content;

        public FakeChatProvider(string content)
        {
            _content = content;
        }

        public string Name => "Fake";
        public ChatProviderRequest? LastRequest { get; private set; }

        public Task<ChatProviderResponse> CompleteAsync(ChatProviderRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new ChatProviderResponse(_content, Name, "fake-model"));
        }
    }
}