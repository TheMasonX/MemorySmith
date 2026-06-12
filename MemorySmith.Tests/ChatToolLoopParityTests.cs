using System.Runtime.CompilerServices;
using System.Text;
using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using Microsoft.Extensions.Options;

namespace MemorySmith.Tests;

/// <summary>
/// Parity tests for the unified chat tool-call loop (TSK-0042 step 1 / Audit #9 candidate 3).
/// Before the unification the loop existed twice (SendAsync's CompleteWithToolCallsAsync and
/// StreamAsync's inline loop), so the two entry points could drift apart. These tests pin the
/// contract that both paths are the SAME loop: identical final content, identical iteration
/// accounting, and the identical iteration-limit message, for the same scripted provider.
/// </summary>
[TestFixture]
public class ChatToolLoopParityTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MemorySmithToolLoopParity", Guid.NewGuid().ToString("N"));
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
    public async Task SendAndStream_ProduceIdenticalFinalContent_ForToolCallThenAnswerScript()
    {
        var memories = CreateMemories();
        await memories.CreateAsync(new MemoryRecord
        {
            Id = "parity-record",
            Title = "Parity Record",
            Content = "Loop parity fixture content.",
            Tags = ["parity"]
        }, CancellationToken.None);

        const string toolCallEnvelope = """{"toolCalls":[{"name":"memorysmith_get","arguments":{"id":"parity-record"}}]}""";
        const string finalAnswer = "The record says: loop parity fixture.";

        var sendResponse = await CreateAgent(new ScriptedChatProvider(toolCallEnvelope, finalAnswer), memories)
            .SendAsync(new MemoryChatRequest("fetch the parity record"), CancellationToken.None);

        MemoryChatResponse? streamResponse = null;
        var streamToolTraceCount = 0;
        await foreach (var update in CreateAgent(new ScriptedChatProvider(toolCallEnvelope, finalAnswer), memories)
            .StreamAsync(new MemoryChatRequest("fetch the parity record"), CancellationToken.None))
        {
            streamToolTraceCount += update.TraceEvents?.Count(trace => trace.Kind == ChatTraceKinds.ToolResult) ?? 0;
            if (update.IsFinal)
            {
                streamResponse = update.Response;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(streamResponse, Is.Not.Null);
            Assert.That(streamResponse!.Reply, Is.EqualTo(sendResponse.Reply),
                "both entry points drive the same loop, so the same script must produce the same reply");
            Assert.That(sendResponse.Reply, Is.EqualTo(finalAnswer));
            Assert.That(streamResponse.Context.Select(item => item.Id), Does.Contain("parity-record"),
                "tool-accessed context must surface on the streamed final response");
            Assert.That(sendResponse.Context.Select(item => item.Id), Does.Contain("parity-record"));
            Assert.That(streamToolTraceCount, Is.GreaterThanOrEqualTo(1),
                "the streaming surface must still emit tool-result trace events");
        });
    }

    [Test]
    public async Task SendAndStream_HitIterationLimit_WithIdenticalMessage()
    {
        var memories = CreateMemories();
        // The provider ALWAYS requests another tool call, so both paths must stop at the
        // configured iteration cap with the same explanatory message.
        const string relentlessToolCall = """{"toolCalls":[{"name":"memorysmith_search","arguments":{"query":"loop"}}]}""";

        var sendResponse = await CreateAgent(new ScriptedChatProvider(relentlessToolCall), memories)
            .SendAsync(new MemoryChatRequest("never stops"), CancellationToken.None);

        MemoryChatResponse? streamResponse = null;
        await foreach (var update in CreateAgent(new ScriptedChatProvider(relentlessToolCall), memories)
            .StreamAsync(new MemoryChatRequest("never stops"), CancellationToken.None))
        {
            if (update.IsFinal)
            {
                streamResponse = update.Response;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(streamResponse, Is.Not.Null);
            Assert.That(sendResponse.Reply, Does.Contain("tool-iteration limit"));
            Assert.That(streamResponse!.Reply, Is.EqualTo(sendResponse.Reply),
                "the iteration-limit message must be byte-identical on both paths — it lives in ONE place now");
        });
    }

    [Test]
    public async Task SendAsync_IgnoresRunControlStop_ByDesign()
    {
        // Run control has only ever been honored by the streaming entry point; the unified loop
        // gates the stop checks on `streaming`. This pins the documented asymmetry so a future
        // change to either path is a deliberate decision, not drift (council: Skeptical Reviewer).
        var memories = CreateMemories();
        const string toolCallEnvelope = """{"toolCalls":[{"name":"memorysmith_search","arguments":{"query":"loop"}}]}""";
        const string finalAnswer = "Completed despite the stop request.";
        var runControl = new ChatRunControl();
        runControl.RequestStopAfterCurrentStep();

        var response = await CreateAgent(new ScriptedChatProvider(toolCallEnvelope, finalAnswer), memories)
            .SendAsync(new MemoryChatRequest("never stops me", RunControl: runControl), CancellationToken.None);

        Assert.That(response.Reply, Is.EqualTo(finalAnswer),
            "SendAsync must run to completion — stop-after-step is a streaming-surface feature only");
    }

    [Test]
    public async Task SendAndStream_ProduceIdenticalReplies_AcrossTwoToolIterations()
    {
        // NOTE (council, 2026-06-12): usage aggregation is deliberately NOT asserted equal here.
        // Writing that assertion exposed a PRE-EXISTING divergence the unification faithfully
        // preserves: on multi-iteration turns where the provider reports no usage, the streaming
        // path constructs each iteration's provider response with `finalChunk?.Usage ??
        // currentUsage`, feeding the prior aggregate back into CompleteUsage, while SendAsync
        // estimates each iteration from scratch (measured: stream 17,756/100 vs send 13,992/59
        // input/output tokens for an identical two-tool-call script). Fixing the estimator is
        // TSK-0248's scope — a behavior-preserving refactor must not change it silently.
        var memories = CreateMemories();
        const string firstCall = """{"toolCalls":[{"name":"memorysmith_search","arguments":{"query":"alpha"}}]}""";
        const string secondCall = """{"toolCalls":[{"name":"memorysmith_search","arguments":{"query":"beta"}}]}""";
        const string finalAnswer = "Done after two tool rounds.";

        var sendResponse = await CreateAgent(new ScriptedChatProvider(firstCall, secondCall, finalAnswer), memories)
            .SendAsync(new MemoryChatRequest("two rounds"), CancellationToken.None);

        MemoryChatResponse? streamResponse = null;
        await foreach (var update in CreateAgent(new ScriptedChatProvider(firstCall, secondCall, finalAnswer), memories)
            .StreamAsync(new MemoryChatRequest("two rounds"), CancellationToken.None))
        {
            if (update.IsFinal)
            {
                streamResponse = update.Response;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(streamResponse, Is.Not.Null);
            Assert.That(sendResponse.Reply, Is.EqualTo(finalAnswer));
            Assert.That(streamResponse!.Reply, Is.EqualTo(finalAnswer),
                "two tool iterations followed by an answer must complete identically on both paths");
            Assert.That(streamResponse.Usage, Is.Not.Null);
            Assert.That(sendResponse.Usage, Is.Not.Null);
        });
    }

    [Test]
    public async Task Stream_BuffersToolCallEnvelope_AndFlushesPlainAnswerDeltas()
    {
        // Exercises the IsPotentialToolCallPrefix buffering path with REAL multi-chunk streaming:
        // a tool-call envelope streamed in fragments must never leak to the visible content
        // surface, while a plain-text answer must flush fully once the prefix is disambiguated.
        var memories = CreateMemories();
        await memories.CreateAsync(new MemoryRecord
        {
            Id = "parity-record",
            Title = "Parity Record",
            Content = "Loop parity fixture content.",
            Tags = ["parity"]
        }, CancellationToken.None);

        const string finalAnswer = "The record says hi.";
        var provider = new ChunkedScriptProvider(
        [
            ["{\"toolCalls\":[", "{\"name\":\"memorysmith_get\",", "\"arguments\":{\"id\":\"parity-record\"}}]}"],
            ["The record", " says hi."]
        ]);

        var visibleDeltas = new StringBuilder();
        MemoryChatResponse? finalResponse = null;
        await foreach (var update in CreateAgent(provider, memories)
            .StreamAsync(new MemoryChatRequest("fetch then answer"), CancellationToken.None))
        {
            if (!string.IsNullOrEmpty(update.ContentDelta))
            {
                Assert.That(update.ContentDelta, Does.Not.Contain("toolCalls"),
                    "a tool-call envelope fragment must never reach the visible content stream");
                visibleDeltas.Append(update.ContentDelta);
            }

            if (update.IsFinal)
            {
                finalResponse = update.Response;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(finalResponse, Is.Not.Null);
            Assert.That(finalResponse!.Reply, Is.EqualTo(finalAnswer));
            Assert.That(visibleDeltas.ToString(), Is.EqualTo(finalAnswer),
                "buffered envelope contributes nothing visible; the plain answer must flush completely");
            Assert.That(finalResponse.Context.Select(item => item.Id), Does.Contain("parity-record"));
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static MemoryApplicationService CreateMemories() =>
        TestServiceFactory.CreateMemoryApplicationService(
            new InMemoryMemoryStore(),
            new RecordingEventStore(),
            new RecordingMemoryChangePublisher());

    private MemoryChatAgent CreateAgent(IChatProvider provider, MemoryApplicationService memories) =>
        new([provider], memories, new FilePageService(_tempDir), Options.Create(new MemorySmithOptions()));

    /// <summary>
    /// Returns each scripted response once, in order, then repeats the last one — letting a
    /// single script express "tool call, then answer" or "tool call forever".
    /// </summary>
    private sealed class ScriptedChatProvider(params string[] script) : IChatProvider
    {
        private int _index;

        public string Name => "Fake";

        public Task<ChatProviderResponse> CompleteAsync(ChatProviderRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ChatProviderResponse(NextContent(), Name, request.Model ?? "fake-model", null));

        public async IAsyncEnumerable<ChatProviderChunk> StreamAsync(ChatProviderRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            var content = NextContent();
            yield return new ChatProviderChunk(content, null, content, null, IsFinal: true, Name, request.Model ?? "fake-model");
        }

        public Task<IReadOnlyList<ChatModelSummary>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ChatModelSummary>>([new ChatModelSummary("fake-model")]);

        private string NextContent()
        {
            var content = script[Math.Min(_index, script.Length - 1)];
            _index++;
            return content;
        }
    }

    /// <summary>
    /// Streams each turn as MULTIPLE content-delta chunks followed by a final chunk — unlike
    /// ScriptedChatProvider's single-final-chunk shape — so the tool-call prefix buffering path
    /// in the unified loop is actually exercised.
    /// </summary>
    private sealed class ChunkedScriptProvider(string[][] turns) : IChatProvider
    {
        private int _turn;

        public string Name => "Fake";

        public Task<ChatProviderResponse> CompleteAsync(ChatProviderRequest request, CancellationToken cancellationToken)
        {
            var content = string.Concat(NextTurn());
            return Task.FromResult(new ChatProviderResponse(content, Name, request.Model ?? "fake-model", null));
        }

        public async IAsyncEnumerable<ChatProviderChunk> StreamAsync(ChatProviderRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            var chunks = NextTurn();
            foreach (var delta in chunks)
            {
                yield return new ChatProviderChunk(delta, null, null, null, IsFinal: false, Name, request.Model ?? "fake-model");
            }

            yield return new ChatProviderChunk(string.Empty, null, string.Concat(chunks), null, IsFinal: true, Name, request.Model ?? "fake-model");
        }

        public Task<IReadOnlyList<ChatModelSummary>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ChatModelSummary>>([new ChatModelSummary("fake-model")]);

        private string[] NextTurn()
        {
            var turn = turns[Math.Min(_turn, turns.Length - 1)];
            _turn++;
            return turn;
        }
    }
}
