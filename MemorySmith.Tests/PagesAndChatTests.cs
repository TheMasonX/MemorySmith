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
        }));

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
                        Assert.That(response.Reply, Is.EqualTo("Recorded."));
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
            Assert.That(provider.LastRequest.Messages.Any(message => message.Content.Contains("Attached note body", StringComparison.Ordinal)), Is.True);
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

        await agent.SendAsync(new MemoryChatRequest("chat GitHub Haiku Sonnet", MemoryChatMode.Chat), CancellationToken.None);
        var contextMessage = provider.LastRequest!.Messages.Single(message => message.Content.StartsWith("Local MemorySmith context", StringComparison.Ordinal));

        Assert.That(contextMessage.Content, Does.Contain("Claude Haiku before Sonnet"));
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

        Assert.Multiple(() =>
        {
            Assert.That(visibleDeltas, Does.Not.Contain("tools/call"));
            Assert.That(updates.Select(update => update.Status).Where(status => !string.IsNullOrWhiteSpace(status)), Does.Contain("Ran 1 MemorySmith wiki tool call(s): memorysmith_get"));
            Assert.That(final.Reply, Is.EqualTo("stream-tool-target has the evidence."));
            Assert.That(provider.Requests, Has.Count.EqualTo(2));
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