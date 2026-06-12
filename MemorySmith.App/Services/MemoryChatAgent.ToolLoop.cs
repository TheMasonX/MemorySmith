namespace MemorySmith.App.Services;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using MemorySmith.App.Services.Training;

/// <summary>
/// The single chat tool-call loop (TSK-0042 step 1 / Audit #9 candidate 3).
///
/// Before this file existed the loop was implemented twice — once for SendAsync
/// (CompleteWithToolCallsAsync) and once inline in StreamAsync — so every loop bug had to be
/// found and fixed in two places. <see cref="RunToolLoopAsync"/> is now the only driver. The two
/// entry points differ only in:
///  - acquisition: streaming accumulates provider chunks (emitting deltas and capturing first
///    token per chunk, with tool-call prefix buffering); non-streaming awaits CompleteAsync;
///  - surface events: streaming emits <see cref="MemoryChatStreamUpdate"/> events (status,
///    traces, deltas); the non-streaming drain ignores them;
///  - run control: only the streaming entry point has ever honored
///    ChatRunControl.StopAfterCurrentStepRequested, so stop checks are gated on
///    <c>streaming</c> to preserve behavior exactly.
/// Everything else — iteration cap and its message, tool-call parsing gated on
/// Chat:ToolCallsEnabled, execution, usage aggregation, accessed-context/transcript bookkeeping,
/// and the assistant/untrusted message append pattern — is shared and lives here once.
/// </summary>
public sealed partial class MemoryChatAgent
{
    private enum ToolLoopTermination
    {
        Completed,
        IterationLimit,
        StoppedBeforeTools,
        StoppedAfterTools
    }

    /// <summary>
    /// One event from the unified loop: zero or more stream-surface updates followed by exactly
    /// one terminal event carrying <see cref="ToolLoopResult"/> + termination reason.
    /// </summary>
    private readonly record struct ToolLoopEvent(
        MemoryChatStreamUpdate? Update,
        ToolLoopResult? Result,
        ToolLoopTermination Termination = ToolLoopTermination.Completed);

    /// <summary>Non-streaming drain over <see cref="RunToolLoopAsync"/> (SendAsync entry point).</summary>
    private async Task<ToolLoopResult> CompleteWithToolCallsAsync(
        IChatProvider provider,
        MemoryChatRequest request,
        IReadOnlyList<ChatMessage> initialMessages,
        CancellationToken cancellationToken)
    {
        ToolLoopResult? result = null;
        await foreach (var evt in RunToolLoopAsync(
            provider, request, initialMessages.ToList(), preloadedContext: [], accessedContext: [],
            streaming: false, started: Stopwatch.GetTimestamp(), cancellationToken))
        {
            if (evt.Result is not null)
            {
                result = evt.Result;
            }
        }

        return result ?? throw new InvalidOperationException("The chat tool loop ended without a terminal result.");
    }

    private async IAsyncEnumerable<ToolLoopEvent> RunToolLoopAsync(
        IChatProvider provider,
        MemoryChatRequest request,
        List<ChatMessage> messages,
        IReadOnlyList<ChatContextItem> preloadedContext,
        List<ChatContextItem> accessedContext,
        bool streaming,
        long started,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int? firstTokenMs = null;
        var transcriptToolCalls = new List<TurnToolCall>();
        var iterationsUsed = 0;
        ChatUsageSummary? aggregateUsage = null;
        ChatUsageSummary? currentUsage = null;
        var maxToolIterations = MaxToolIterations();
        var maxToolCallsPerTurn = Math.Clamp(_options.Value.Chat.MaxToolCallsPerTurn, 1, 10);
        if (!streaming)
        {
            _logger?.LogDebug(
                ChatLogEvents.ToolLoopStarted,
                "Chat tool loop started. Provider: {Provider}, Mode: {Mode}, MaxToolIterations: {MaxToolIterations}, MaxToolCallsPerTurn: {MaxToolCallsPerTurn}",
                provider.Name,
                request.Mode,
                maxToolIterations,
                maxToolCallsPerTurn);
        }

        for (var iteration = 0; ; iteration++)
        {
            var approvalRequired = RequiresAgentWriteApproval(request);
            var providerTools = BuildProviderToolDefinitions(request.Mode, approvalRequired);
            var providerRequest = new ChatProviderRequest(messages, request.Mode, request.Model, request.Attachments, provider.Name, providerTools);

            ChatProviderResponse providerResponse;
            if (streaming)
            {
                // ── Streaming acquisition: accumulate chunks, emit deltas, buffer potential
                //    tool-call prefixes so partial envelopes never reach the UI. ─────────────
                var content = new StringBuilder();
                var thinking = new StringBuilder();
                ChatProviderChunk? finalChunk = null;
                var bufferVisibleContent = _options.Value.Chat.ToolCallsEnabled;

                await foreach (var chunk in provider.StreamAsync(providerRequest, cancellationToken))
                {
                    if (!string.IsNullOrEmpty(chunk.ContentDelta))
                    {
                        content.Append(chunk.ContentDelta);
                    }
                    if (!string.IsNullOrEmpty(chunk.ThinkingDelta))
                    {
                        thinking.Append(chunk.ThinkingDelta);
                    }
                    if (!firstTokenMs.HasValue && (!string.IsNullOrEmpty(chunk.ContentDelta) || !string.IsNullOrEmpty(chunk.ThinkingDelta)))
                    {
                        firstTokenMs = ClampMilliseconds(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    }
                    if (chunk.Usage is not null)
                    {
                        var usageForActiveCall = CompleteUsage(chunk.ProviderName, chunk.Model, messages, content.ToString(), chunk.Usage);
                        currentUsage = MergeTurnUsage(aggregateUsage, usageForActiveCall);
                    }

                    if (chunk.IsFinal)
                    {
                        finalChunk = chunk;
                        break;
                    }

                    var contentDelta = chunk.ContentDelta;
                    if (!string.IsNullOrEmpty(contentDelta) && bufferVisibleContent)
                    {
                        if (IsPotentialToolCallPrefix(content.ToString()))
                        {
                            contentDelta = string.Empty;
                        }
                        else
                        {
                            bufferVisibleContent = false;
                            contentDelta = content.ToString();
                        }
                    }

                    yield return new ToolLoopEvent(
                        new MemoryChatStreamUpdate(contentDelta, chunk.ThinkingDelta, Status: chunk.Status, Usage: currentUsage), null);
                }

                providerResponse = new ChatProviderResponse(
                    finalChunk?.FinalContent ?? content.ToString(),
                    finalChunk?.ProviderName ?? provider.Name,
                    finalChunk?.Model ?? request.Model ?? DefaultModelForProvider(provider.Name),
                    finalChunk?.FinalThinking ?? (thinking.Length == 0 ? null : thinking.ToString()),
                    finalChunk?.Usage ?? currentUsage);
            }
            else
            {
                providerResponse = await provider.CompleteAsync(providerRequest, cancellationToken);
            }

            var completedUsage = CompleteUsage(providerResponse.ProviderName, providerResponse.Model, messages, providerResponse.Content, providerResponse.Usage);
            aggregateUsage = MergeTurnUsage(aggregateUsage, completedUsage);
            currentUsage = aggregateUsage;
            providerResponse = providerResponse with { Usage = aggregateUsage };
            if (!streaming)
            {
                firstTokenMs ??= ClampMilliseconds(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            ToolLoopResult Result(ChatProviderResponse response) => new(
                response,
                messages,
                accessedContext,
                firstTokenMs ?? 0,
                ClampMilliseconds(Stopwatch.GetElapsedTime(started).TotalMilliseconds),
                iterationsUsed,
                transcriptToolCalls);

            var toolCalls = _options.Value.Chat.ToolCallsEnabled ? ReadToolCalls(providerResponse.Content) : [];
            if (toolCalls.Count == 0)
            {
                if (!streaming)
                {
                    _logger?.LogInformation(
                        ChatLogEvents.ToolLoopCompleted,
                        "Chat tool loop completed without additional tool calls. Provider: {Provider}, IterationsUsed: {IterationsUsed}, ToolCallCount: {ToolCallCount}",
                        providerResponse.ProviderName,
                        iterationsUsed,
                        transcriptToolCalls.Count);
                }

                yield return new ToolLoopEvent(null, Result(providerResponse), ToolLoopTermination.Completed);
                yield break;
            }

            if (streaming)
            {
                _logger?.LogInformation(
                    ChatLogEvents.StreamToolCallsRequested,
                    "Chat StreamAsync requested tool calls. Iteration: {Iteration}, RequestedTools: {RequestedTools}, ToolCallCount: {ToolCallCount}",
                    iteration,
                    string.Join(",", toolCalls.Select(toolCall => toolCall.Name).Distinct(StringComparer.OrdinalIgnoreCase)),
                    toolCalls.Count);
            }

            if (iteration >= maxToolIterations)
            {
                if (!streaming)
                {
                    _logger?.LogWarning(
                        ChatLogEvents.ToolLoopIterationLimit,
                        "Chat tool loop hit iteration limit. Provider: {Provider}, Iteration: {Iteration}, MaxToolIterations: {MaxToolIterations}, RequestedToolCount: {RequestedToolCount}",
                        providerResponse.ProviderName,
                        iteration,
                        maxToolIterations,
                        toolCalls.Count);
                }

                yield return new ToolLoopEvent(
                    null,
                    Result(providerResponse with
                    {
                        Content = "The model requested another MemorySmith wiki tool call after the configured tool-iteration limit. Try narrowing the request or increasing Chat:MaxToolIterations."
                    }),
                    ToolLoopTermination.IterationLimit);
                yield break;
            }

            if (streaming)
            {
                var requestedToolTrace = toolCalls
                    .Select(toolCall => new ChatTraceEvent(
                        ChatTraceKinds.ToolCall,
                        $"Tool call requested: {toolCall.Name}",
                        toolCall.Arguments.ToJsonString(ToolJsonOptions),
                        TimestampUtc: DateTimeOffset.UtcNow,
                        ToolName: toolCall.Name,
                        ToolArgumentsJson: toolCall.Arguments.ToJsonString(ToolJsonOptions)))
                    .ToList();
                yield return new ToolLoopEvent(
                    new MemoryChatStreamUpdate(
                        Status: $"Model requested {toolCalls.Count} MemorySmith wiki tool call(s)",
                        Context: MergeContext(preloadedContext, accessedContext),
                        Usage: currentUsage,
                        TraceEvents: requestedToolTrace), null);

                // Run control has only ever been honored by the streaming entry point.
                if (request.RunControl?.StopAfterCurrentStepRequested == true)
                {
                    yield return new ToolLoopEvent(
                        null,
                        Result(providerResponse with
                        {
                            Content = "Stopped before running the requested MemorySmith wiki tool call(s)."
                        }),
                        ToolLoopTermination.StoppedBeforeTools);
                    yield break;
                }
            }

            var toolResults = await ExecuteToolCallsAsync(toolCalls, request.Mode, approvalRequired, cancellationToken);
            accessedContext.AddRange(ExtractToolContext(toolResults));
            iterationsUsed++;
            transcriptToolCalls.AddRange(ProjectTranscriptToolCalls(toolCalls, toolResults, maxToolCallsPerTurn));
            if (streaming)
            {
                _logger?.LogInformation(
                    ChatLogEvents.StreamToolCallsExecuted,
                    "Chat StreamAsync executed tool calls. Iteration: {Iteration}, RequestedToolCount: {RequestedToolCount}, ExecutedToolCount: {ExecutedToolCount}, ToolErrors: {ToolErrors}",
                    iteration,
                    toolCalls.Count,
                    toolResults.Count,
                    toolResults.Count(result => result.IsError));
            }
            else
            {
                _logger?.LogInformation(
                    ChatLogEvents.ToolLoopExecuted,
                    "Chat tool loop executed requested tools. Provider: {Provider}, Iteration: {Iteration}, RequestedToolCount: {RequestedToolCount}, ExecutedToolCount: {ExecutedToolCount}, ToolErrors: {ToolErrors}",
                    providerResponse.ProviderName,
                    iteration,
                    toolCalls.Count,
                    toolResults.Count,
                    toolResults.Count(result => result.IsError));
            }

            messages.Add(new ChatMessage("assistant", providerResponse.Content));
            messages.Add(new ChatMessage(UntrustedDataRole, FormatToolResults(toolResults)));

            if (streaming)
            {
                var toolResultTrace = toolResults
                    .Select(result => new ChatTraceEvent(
                        ChatTraceKinds.ToolResult,
                        $"Tool result: {result.Name}",
                        result.Content,
                        result.IsError,
                        DateTimeOffset.UtcNow,
                        ToolName: result.Name,
                        DurationMilliseconds: result.DurationMilliseconds,
                        EstimatedTokens: EstimateTokens(result.Content)))
                    .ToList();
                yield return new ToolLoopEvent(
                    new MemoryChatStreamUpdate(
                        Status: $"Ran {toolResults.Count} MemorySmith wiki tool call(s): {string.Join(", ", toolResults.Select(result => result.Name).Distinct(StringComparer.OrdinalIgnoreCase))}",
                        Context: MergeContext(preloadedContext, accessedContext),
                        Usage: currentUsage,
                        TraceEvents: toolResultTrace), null);

                if (request.RunControl?.StopAfterCurrentStepRequested == true)
                {
                    yield return new ToolLoopEvent(
                        null,
                        Result(providerResponse with
                        {
                            Content = "Stopped after running MemorySmith wiki tool call(s). The tool results are available in the trace; send a follow-up to continue from them."
                        }),
                        ToolLoopTermination.StoppedAfterTools);
                    yield break;
                }
            }
        }
    }
}
