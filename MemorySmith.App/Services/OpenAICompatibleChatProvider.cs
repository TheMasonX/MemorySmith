using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Services;

/// <summary>
/// <see cref="IChatProvider"/> for OpenAI-compatible chat completion APIs.
///
/// A single implementation covering OpenAI, DeepSeek, OpenRouter, and any API
/// that speaks the same protocol:
///   POST /v1/chat/completions   — Bearer token auth, JSON request/response
///   GET  /v1/models             — model discovery
///
/// Configuration via <see cref="ChatOptions"/>:
///   MemorySmith:Chat:OpenAIEndpoint     — Base URL (e.g. https://api.deepseek.com)
///   MemorySmith:Chat:OpenAIApiKeyEnvVar — Env var name for API key (default: MSA_LLM_API_KEY)
///   MemorySmith:Chat:OpenAIModel        — Default model name (e.g. deepseek-chat)
///
/// Env var pattern follows MemorySmith.Agent (MSA_LLM_API_KEY), so the same
/// credential works across both repos.
/// </summary>
public sealed partial class OpenAICompatibleChatProvider : IChatProvider
{
    private const string DefaultApiKeyEnvVar = "MSA_LLM_API_KEY";
    private const string DefaultModel = "deepseek-chat";
    private const string ChatCompletionsPath = "/v1/chat/completions";
    private const string ModelsPath = "/v1/models";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<MemorySmithOptions> _options;
    private readonly ILogger<OpenAICompatibleChatProvider>? _logger;

    public OpenAICompatibleChatProvider(
        HttpClient httpClient,
        IOptionsMonitor<MemorySmithOptions> options,
        ILogger<OpenAICompatibleChatProvider>? logger = null)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public string Name => "OpenAI";

    public ChatProviderCapabilities Capabilities => new(
        SupportsStreaming: true,
        SupportsImageInput: false,
        SupportsStructuredResponses: false,
        ReportsContextWindowUsage: false,
        SupportsNativeToolCalls: true,
        NativeToolCallStatus: "OpenAI-compatible native tool calling is enabled via the standard tools parameter.");

    // ── Non-streaming completion ──────────────────────────────────────────────

    public async Task<ChatProviderResponse> CompleteAsync(
        ChatProviderRequest request,
        CancellationToken cancellationToken)
    {
        var chatOptions = _options.CurrentValue.Chat;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(chatOptions.RequestTimeoutSeconds, 5, 600)));

        var model = ResolveModel(request.Model, chatOptions);
        var baseUrl = ResolveBaseUrl(chatOptions);
        var apiKey = ResolveApiKey(chatOptions);

        var payload = BuildChatRequestPayload(model, request, stream: false);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), ChatCompletionsPath.TrimStart('/')))
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        ApplyAuth(httpRequest, apiKey);

        using var response = await _httpClient.SendAsync(httpRequest, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI-compatible API returned {(int)response.StatusCode}: {body}");
        }

        using var document = JsonDocument.Parse(body);
        var (content, thinking) = ReadOpenAIResponseContent(document.RootElement);

        // Check for native tool calls in non-streaming response
        if (ReadOpenAINativeToolCalls(document.RootElement) is { Count: > 0 } toolCalls)
        {
            var toolEnvelope = new JsonObject { ["toolCalls"] = new JsonArray(toolCalls.Select(tc => JsonNode.Parse(tc)!).ToArray()) }.ToJsonString();
            content = string.IsNullOrWhiteSpace(content)
                ? toolEnvelope
                : content + Environment.NewLine + toolEnvelope;
        }

        var usage = ReadOpenAIUsage(document.RootElement);
        _logger?.LogDebug("OpenAI-compatible complete response received for model {Model}. Reply chars: {ReplyLength}.", model, content.Length);
        return new ChatProviderResponse(content, Name, model, thinking, usage);
    }

    // ── Streaming completion ─────────────────────────────────────────────────

    public async IAsyncEnumerable<ChatProviderChunk> StreamAsync(
        ChatProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var chatOptions = _options.CurrentValue.Chat;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(chatOptions.RequestTimeoutSeconds, 5, 600)));
        var chunkIdleTimeout = ResolveStreamIdleTimeout(chatOptions);

        var model = ResolveModel(request.Model, chatOptions);
        var baseUrl = ResolveBaseUrl(chatOptions);
        var apiKey = ResolveApiKey(chatOptions);

        var payload = BuildChatRequestPayload(model, request, stream: true);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), ChatCompletionsPath.TrimStart('/')))
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        ApplyAuth(httpRequest, apiKey);

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var errorBody = response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI-compatible API returned {(int)response.StatusCode}: {errorBody}");
        }

        var content = new StringBuilder();
        string? finalThinking = null;
        ChatUsageSummary? usage = null;
        var emittedFinal = false;
        var malformedLines = 0;

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);

        while (true)
        {
            string? line;
            using (var chunkIdle = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            {
                chunkIdle.CancelAfter(chunkIdleTimeout);
                try
                {
                    line = await reader.ReadLineAsync(chunkIdle.Token);
                }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested && chunkIdle.IsCancellationRequested)
                {
                    throw new TimeoutException($"OpenAI-compatible stream was idle for {chunkIdleTimeout.TotalSeconds:0} second(s) while waiting for the next chunk.");
                }
            }

            if (line is null)
            {
                break;
            }

            // SSE: skip empty lines and lines that are not data:
            if (!line.StartsWith("data: ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line.AsSpan(6); // strip "data: "
            var dataStr = data.ToString();
            if (dataStr.Trim().Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                // End of stream signal — yield final chunk with accumulated content
                break;
            }

            if (string.IsNullOrWhiteSpace(dataStr))
            {
                continue;
            }

            JsonDocument chunkDocument;
            try
            {
                chunkDocument = JsonDocument.Parse(dataStr);
            }
            catch (JsonException)
            {
                malformedLines++;
                continue;
            }

            using (chunkDocument)
            {
                var root = chunkDocument.RootElement;
                var delta = ReadOpenAIStreamDelta(root, out var thinkingDelta);

                if (!string.IsNullOrEmpty(delta))
                {
                    content.Append(delta);
                }
                if (!string.IsNullOrWhiteSpace(thinkingDelta))
                {
                    finalThinking = string.IsNullOrWhiteSpace(finalThinking) ? thinkingDelta : finalThinking + thinkingDelta;
                }

                // Accumulate usage from the final chunk
                if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
                {
                    usage = ReadOpenAIUsage(root);
                }

                // Check for tool calls in this delta
                var nativeToolEnvelope = ReadOpenAIStreamToolCalls(root);
                if (!string.IsNullOrWhiteSpace(nativeToolEnvelope))
                {
                    content.Append(nativeToolEnvelope);
                }

                // Check finish reason
                var finishReason = ReadFinishReason(root);
                if (finishReason is not null)
                {
                    // Stream is done — yield final chunk
                    var finalContent = content.ToString();
                    var (visible, thinking) = SplitThinking(finalContent, finalThinking);
                    emittedFinal = true;
                    yield return new ChatProviderChunk(
                        string.Empty, null, visible, thinking,
                        IsFinal: true, Name, model, Usage: usage);
                }
                else if (!string.IsNullOrEmpty(delta) || !string.IsNullOrEmpty(thinkingDelta))
                {
                    yield return new ChatProviderChunk(
                        delta, thinkingDelta, null, null,
                        IsFinal: false, Name, model);
                }
            }
        }

        // If stream ended without a finish_reason, emit a final chunk with what we have
        if (!emittedFinal && content.Length > 0)
        {
            var (visible, thinking) = SplitThinking(content.ToString(), finalThinking);
            yield return new ChatProviderChunk(
                string.Empty, null, visible, thinking,
                IsFinal: true, Name, model, Usage: usage);
        }

        if (malformedLines > 0)
        {
            _logger?.LogWarning(
                "OpenAI-compatible stream for model {Model} skipped {MalformedLines} malformed SSE line(s).",
                model, malformedLines);
        }
    }

    // ── Model listing ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ChatModelSummary>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var chatOptions = _options.CurrentValue.Chat;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(chatOptions.RequestTimeoutSeconds, 5, 600)));

        var baseUrl = ResolveBaseUrl(chatOptions);
        var apiKey = ResolveApiKey(chatOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), ModelsPath.TrimStart('/')));
        ApplyAuth(httpRequest, apiKey);

        using var response = await _httpClient.SendAsync(httpRequest, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            // Fall back to configured model if listing fails
            return [new ChatModelSummary(chatOptions.OpenAIModel, Provider: Name)];
        }

        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var models = new List<ChatModelSummary>();
        if (root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dataElement.EnumerateArray())
            {
                var modelName = item.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(modelName))
                {
                    continue;
                }

                DateTimeOffset? createdAt = null;
                if (item.TryGetProperty("created", out var createdElement) && createdElement.ValueKind == JsonValueKind.Number)
                {
                    if (createdElement.TryGetInt64(out var unixSeconds))
                    {
                        createdAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
                    }
                }

                models.Add(new ChatModelSummary(modelName, ModifiedAt: createdAt, Provider: Name));
            }
        }

        // Merge with configured model preferences
        if (models.Count == 0)
        {
            return [new ChatModelSummary(chatOptions.OpenAIModel, Provider: Name)];
        }

        var modelOptions = chatOptions.OpenAIModels;
        if (modelOptions is { Count: > 0 })
        {
            var configuredByName = modelOptions
                .Where(m => !string.IsNullOrWhiteSpace(m.Name))
                .ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < models.Count; i++)
            {
                if (configuredByName.TryGetValue(models[i].Name, out var option))
                {
                    models[i] = models[i] with
                    {
                        IsPreferred = option.IsPreferred,
                        ChatMultiplier = option.ChatMultiplier,
                        Description = option.Description,
                        ContextWindowTokens = option.ContextWindowTokens,
                        RateLimit = option.RateLimit
                    };
                }
            }
        }

        return models
            .OrderByDescending(m => m.IsPreferred)
            .ThenBy(m => m.ChatMultiplier ?? double.MaxValue)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Configuration helpers ─────────────────────────────────────────────────

    private static string ResolveBaseUrl(ChatOptions chatOptions)
    {
        if (!string.IsNullOrWhiteSpace(chatOptions.OpenAIEndpoint))
        {
            return chatOptions.OpenAIEndpoint;
        }

        // Default to DeepSeek when no explicit endpoint is configured
        return "https://api.deepseek.com";
    }

    private static string ResolveApiKey(ChatOptions chatOptions)
    {
        // 1. Try the configured env var name
        var envVarName = !string.IsNullOrWhiteSpace(chatOptions.OpenAIApiKeyEnvironmentVariable)
            ? chatOptions.OpenAIApiKeyEnvironmentVariable
            : DefaultApiKeyEnvVar;
        var fromEnv = Environment.GetEnvironmentVariable(envVarName);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        // 2. Try MSA_LLM_API_KEY as fallback (MemorySmith.Agent compatibility)
        if (!string.Equals(envVarName, DefaultApiKeyEnvVar, StringComparison.OrdinalIgnoreCase))
        {
            fromEnv = Environment.GetEnvironmentVariable(DefaultApiKeyEnvVar);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return fromEnv;
            }
        }

        return string.Empty;
    }

    private static void ApplyAuth(HttpRequestMessage request, string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new("Bearer", apiKey);
        }
    }

    private static string ResolveModel(string? requestModel, ChatOptions chatOptions) =>
        !string.IsNullOrWhiteSpace(requestModel)
            ? requestModel
            : (!string.IsNullOrWhiteSpace(chatOptions.OpenAIModel)
                ? chatOptions.OpenAIModel
                : DefaultModel);

    // ── Request building ──────────────────────────────────────────────────────

    private static Dictionary<string, object?> BuildChatRequestPayload(
        string model, ChatProviderRequest request, bool stream)
    {
        var messages = new List<object>();
        foreach (var msg in request.Messages)
        {
            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = msg.Role,
                ["content"] = msg.Content
            });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = stream,
        };

        // Add tools if present
        if (request.Tools is { Count: > 0 })
        {
            var tools = new List<object>();
            foreach (var tool in request.Tools)
            {
                if (!string.IsNullOrWhiteSpace(tool.Name))
                {
                    tools.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = tool.Name,
                            ["description"] = tool.Description,
                            ["parameters"] = tool.InputSchema
                        }
                    });
                }
            }
            if (tools.Count > 0)
            {
                payload["tools"] = tools;
            }
        }

        return payload;
    }

    // ── Response parsing ──────────────────────────────────────────────────────

    private static (string Content, string? Thinking) ReadOpenAIResponseContent(JsonElement root)
    {
        var choice = root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            ? choices[0]
            : default;

        if (choice.ValueKind == JsonValueKind.Undefined)
        {
            return (string.Empty, null);
        }

        var message = choice.TryGetProperty("message", out var msg) ? msg : default;
        if (message.ValueKind == JsonValueKind.Undefined)
        {
            return (string.Empty, null);
        }

        var content = message.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String
            ? contentElement.GetString() ?? string.Empty
            : string.Empty;

        // Some providers send reasoning/thinking in a non-standard field
        var thinking = message.TryGetProperty("reasoning_content", out var reasoningElement) && reasoningElement.ValueKind == JsonValueKind.String
            ? reasoningElement.GetString()
            : null;

        // Also check for "reasoning" field (used by some providers)
        thinking ??= message.TryGetProperty("reasoning", out var reasoning2) && reasoning2.ValueKind == JsonValueKind.String
            ? reasoning2.GetString()
            : null;

        return (content, thinking);
    }

    private static List<string>? ReadOpenAINativeToolCalls(JsonElement root)
    {
        var choice = root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            ? choices[0]
            : default;

        if (choice.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        var message = choice.TryGetProperty("message", out var msg) ? msg : default;
        if (message.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (!message.TryGetProperty("tool_calls", out var toolCallsElement) || toolCallsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var calls = new List<string>();
        foreach (var tc in toolCallsElement.EnumerateArray())
        {
            var name = tc.TryGetProperty("function", out var func)
                       && func.TryGetProperty("name", out var nameElement)
                       && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var args = func.TryGetProperty("arguments", out var argsElement) && argsElement.ValueKind == JsonValueKind.String
                ? argsElement.GetString()
                : "{}";

            calls.Add(new JsonObject
            {
                ["name"] = name,
                ["arguments"] = JsonNode.Parse(args ?? "{}")?.AsObject() ?? new JsonObject()
            }.ToJsonString());
        }

        return calls.Count > 0 ? calls : null;
    }

    private static string? ReadOpenAIStreamDelta(JsonElement root, out string? thinkingDelta)
    {
        thinkingDelta = null;

        var choice = root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            ? choices[0]
            : default;

        if (choice.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var content = delta.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String
            ? contentElement.GetString()
            : null;

        // Check for reasoning/thinking in delta
        thinkingDelta = delta.TryGetProperty("reasoning_content", out var reasoningElement) && reasoningElement.ValueKind == JsonValueKind.String
            ? reasoningElement.GetString()
            : null;

        thinkingDelta ??= delta.TryGetProperty("reasoning", out var reasoning2) && reasoning2.ValueKind == JsonValueKind.String
            ? reasoning2.GetString()
            : null;

        return content;
    }

    private static string? ReadOpenAIStreamToolCalls(JsonElement root)
    {
        var choice = root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            ? choices[0]
            : default;

        if (choice.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!delta.TryGetProperty("tool_calls", out var toolCallsElement) || toolCallsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // Accumulate partial tool calls across stream chunks
        var calls = new JsonArray();
        foreach (var tc in toolCallsElement.EnumerateArray())
        {
            var name = tc.TryGetProperty("function", out var func)
                       && func.TryGetProperty("name", out var nameElement)
                       && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var args = func.TryGetProperty("arguments", out var argsElement) && argsElement.ValueKind == JsonValueKind.String
                ? argsElement.GetString()
                : "{}";

            calls.Add(new JsonObject
            {
                ["name"] = name,
                ["arguments"] = JsonNode.Parse(args ?? "{}")?.AsObject() ?? new JsonObject()
            });
        }

        if (calls.Count == 0)
        {
            return null;
        }

        return new JsonObject { ["toolCalls"] = calls }.ToJsonString();
    }

    private static string? ReadFinishReason(JsonElement root)
    {
        var choice = root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            ? choices[0]
            : default;

        if (choice.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (choice.TryGetProperty("finish_reason", out var finishElement) && finishElement.ValueKind == JsonValueKind.String)
        {
            var reason = finishElement.GetString();
            return !string.IsNullOrWhiteSpace(reason) && !string.Equals(reason, "null", StringComparison.OrdinalIgnoreCase)
                ? reason
                : null;
        }

        return null;
    }

    private static ChatUsageSummary? ReadOpenAIUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usageElement) || usageElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var inputTokens = ReadIntProperty(usageElement, "prompt_tokens");
        var outputTokens = ReadIntProperty(usageElement, "completion_tokens");
        var totalTokens = ReadIntProperty(usageElement, "total_tokens");

        if (inputTokens is null && outputTokens is null)
        {
            return null;
        }

        return new ChatUsageSummary(
            inputTokens ?? 0,
            outputTokens ?? 0,
            totalTokens,
            IsEstimate: false);
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static int? ReadIntProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var value)
            ? value
            : null;

    private static (string Visible, string? Thinking) SplitThinking(string content, string? accumulatedThinking)
    {
        // Some providers put thinking in the reasoning fields (already extracted above).
        // For providers that embed <think> tags in content, extract them.
        if (string.IsNullOrWhiteSpace(content))
        {
            return (string.Empty, accumulatedThinking);
        }

        var matches = ThinkingPatternRegex().Matches(content);
        if (matches.Count == 0)
        {
            return (content.Trim(), string.IsNullOrWhiteSpace(accumulatedThinking) ? null : accumulatedThinking.Trim());
        }

        var extractedThinking = string.Join(Environment.NewLine,
            matches.Select(match => match.Groups[1].Value.Trim()).Where(value => value.Length > 0));
        var thinking = string.Join(Environment.NewLine,
            new[] { accumulatedThinking, extractedThinking }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var visible = ThinkingPatternRegex().Replace(content, string.Empty).Trim();
        return (visible, string.IsNullOrWhiteSpace(thinking) ? null : thinking.Trim());
    }

    private static TimeSpan ResolveStreamIdleTimeout(ChatOptions chatOptions)
    {
        var requestTimeoutSeconds = Math.Clamp(chatOptions.RequestTimeoutSeconds, 5, 600);
        var idleTimeoutSeconds = Math.Clamp(requestTimeoutSeconds / 4, 5, 60);
        return TimeSpan.FromSeconds(idleTimeoutSeconds);
    }

    [GeneratedRegex("<think>(.*?)</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkingPatternRegex();
}
