using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using Microsoft.Extensions.Options;
using System.Net;

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
        var agent = new MemoryChatAgent([provider], memories, pages, Options.Create(new MemorySmithOptions()));

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
            Assert.That(provider.LastRequest!.Model, Is.EqualTo("custom-model"));
            Assert.That(provider.LastRequest.Messages.Any(message => message.Content.Contains("Use the project wiki prompt.", StringComparison.Ordinal)), Is.True);
            Assert.That(provider.LastRequest.Messages.Any(message => message.Content.Contains("Attached note body", StringComparison.Ordinal)), Is.True);
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
        {"done":true}
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