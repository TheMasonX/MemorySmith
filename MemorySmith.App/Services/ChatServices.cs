using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using GitHub.Copilot.SDK;
using MemorySmith.Core.Models;
using Microsoft.Extensions.Options;
using SdkCopilotClient = GitHub.Copilot.SDK.CopilotClient;
using SdkCopilotClientOptions = GitHub.Copilot.SDK.CopilotClientOptions;

namespace MemorySmith.App.Services;

public enum MemoryChatMode
{
    Chat,
    Agent
}

public sealed record ChatMessage(string Role, string Content);

public sealed record ChatAttachment(
    string Name,
    string ContentType,
    string Text,
    long Size,
    string? Base64Data = null,
    bool IsImage = false,
    bool IsTruncated = false,
    string? LocalPath = null);

public sealed record ChatProviderRequest(
    IReadOnlyList<ChatMessage> Messages,
    MemoryChatMode Mode,
    string? Model = null,
    IReadOnlyList<ChatAttachment>? Attachments = null,
    string? Provider = null);

public sealed record ChatUsageSummary(
    int InputTokens,
    int OutputTokens,
    int? ContextTokens = null,
    int? ContextWindowTokens = null,
    string? RateLimit = null,
    bool IsEstimate = true);

public sealed record ChatProviderResponse(
    string Content,
    string ProviderName,
    string Model,
    string? Thinking = null,
    ChatUsageSummary? Usage = null);

public sealed record ChatProviderChunk(
    string ContentDelta,
    string? ThinkingDelta,
    string? FinalContent,
    string? FinalThinking,
    bool IsFinal,
    string ProviderName,
    string Model,
    string? Status = null,
    ChatUsageSummary? Usage = null);

public sealed record ChatModelSummary(
    string Name,
    DateTimeOffset? ModifiedAt = null,
    long? Size = null,
    string? Provider = null,
    double? ChatMultiplier = null,
    bool IsPreferred = false,
    string? Description = null,
    int? ContextWindowTokens = null,
    string? RateLimit = null);

public sealed record ChatRuntimeConfiguration(
    string Provider,
    string Endpoint,
    string Model,
    IReadOnlyList<ChatModelSummary> Models,
    IReadOnlyList<string> Providers,
    string? ModelsError = null);

public static class ChatErrorMessages
{
    public static string Format(Exception ex, string provider, string model)
    {
        if (ProviderMatches(provider, "GitHub"))
        {
            if (ex.Message.Contains("not available", StringComparison.OrdinalIgnoreCase))
            {
                return $"GitHub Copilot model '{model}' is not available for this account/session. Refresh models, then try a discovered GPT option first; configured fallbacks prefer free GPTs, then Claude Haiku, then Sonnet. Original error: {ex.Message}";
            }

            if (ex.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("Session was not created", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("custom provider", StringComparison.OrdinalIgnoreCase))
            {
                return $"GitHub Copilot authentication is not available. Sign in with the GitHub/Copilot CLI path configured for the SDK or set GITHUB_TOKEN, GH_TOKEN, or COPILOT_API_KEY. Original error: {ex.Message}";
            }
        }

        return ex.Message;
    }

    private static bool ProviderMatches(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
        (string.Equals(left, "GitHub", StringComparison.OrdinalIgnoreCase) && string.Equals(right, "Copilot", StringComparison.OrdinalIgnoreCase));
}

public sealed record MemoryChatRequest(
    string Message,
    MemoryChatMode Mode = MemoryChatMode.Chat,
    IReadOnlyList<ChatMessage>? History = null,
    string? Model = null,
    IReadOnlyList<ChatAttachment>? Attachments = null,
    string? Provider = null);

public sealed record ChatContextItem(string Kind, string Id, string Title, string Snippet);

public sealed record MemoryChatResponse(
    string Reply,
    string ProviderName,
    string Model,
    string? Thinking,
    IReadOnlyList<ChatContextItem> Context,
    IReadOnlyList<string> WrittenMemories,
    IReadOnlyList<string> WrittenPages,
    ChatUsageSummary? Usage = null);

public sealed record MemoryChatStreamUpdate(
    string ContentDelta = "",
    string? ThinkingDelta = null,
    bool IsFinal = false,
    MemoryChatResponse? Response = null,
    IReadOnlyList<ChatContextItem>? Context = null,
    string? Status = null,
    ChatUsageSummary? Usage = null);

public interface IChatProvider
{
    string Name { get; }
    Task<ChatProviderResponse> CompleteAsync(ChatProviderRequest request, CancellationToken cancellationToken);
    IAsyncEnumerable<ChatProviderChunk> StreamAsync(ChatProviderRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChatModelSummary>> ListModelsAsync(CancellationToken cancellationToken);
}

public interface IChatAgent
{
    Task<MemoryChatResponse> SendAsync(MemoryChatRequest request, CancellationToken cancellationToken);
    IAsyncEnumerable<MemoryChatStreamUpdate> StreamAsync(MemoryChatRequest request, CancellationToken cancellationToken);
}

public static class ChatAttachmentFiles
{
    private static readonly string TempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MemorySmith", "ChatAttachments"));

    public static async Task<string> SaveTempAsync(string originalName, byte[] content, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(TempRoot);
        var extension = Path.GetExtension(originalName);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 12)
        {
            extension = ".bin";
        }

        var fileName = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        var path = Path.Combine(TempRoot, fileName);
        await File.WriteAllBytesAsync(path, content, cancellationToken);
        return path;
    }

    public static string? ReadTrustedImageBase64(ChatAttachment attachment)
    {
        if (!string.IsNullOrWhiteSpace(attachment.Base64Data))
        {
            return attachment.Base64Data;
        }

        if (string.IsNullOrWhiteSpace(attachment.LocalPath) || !IsTrustedTempPath(attachment.LocalPath) || !File.Exists(attachment.LocalPath))
        {
            return null;
        }

        return Convert.ToBase64String(File.ReadAllBytes(attachment.LocalPath));
    }

    private static bool IsTrustedTempPath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(TempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

public sealed partial class OllamaChatProvider : IChatProvider
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<MemorySmithOptions> _options;

    public OllamaChatProvider(HttpClient httpClient, IOptionsMonitor<MemorySmithOptions> options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public string Name => "Ollama";

    public async Task<ChatProviderResponse> CompleteAsync(ChatProviderRequest request, CancellationToken cancellationToken)
    {
        var chatOptions = _options.CurrentValue.Chat;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(chatOptions.RequestTimeoutSeconds, 5, 600)));

        var model = string.IsNullOrWhiteSpace(request.Model) ? chatOptions.OllamaModel : request.Model.Trim();
        var endpoint = new Uri(new Uri(chatOptions.OllamaEndpoint.TrimEnd('/') + "/"), "api/chat");
        var payload = new
        {
            model,
            stream = false,
            messages = BuildOllamaMessages(request)
        };

        using var response = await _httpClient.PostAsJsonAsync(endpoint, payload, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Ollama returned {(int)response.StatusCode}: {body}");
        }

        using var document = JsonDocument.Parse(body);
        var (content, thinking) = ReadOllamaContent(document.RootElement);
        return new ChatProviderResponse(content, Name, model, thinking, ReadOllamaUsage(document.RootElement));
    }

    public async IAsyncEnumerable<ChatProviderChunk> StreamAsync(ChatProviderRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var chatOptions = _options.CurrentValue.Chat;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(chatOptions.RequestTimeoutSeconds, 5, 600)));

        var model = string.IsNullOrWhiteSpace(request.Model) ? chatOptions.OllamaModel : request.Model.Trim();
        var endpoint = new Uri(new Uri(chatOptions.OllamaEndpoint.TrimEnd('/') + "/"), "api/chat");
        var payload = new
        {
            model,
            stream = true,
            messages = BuildOllamaMessages(request)
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload)
        };
        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var errorBody = response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Ollama returned {(int)response.StatusCode}: {errorBody}");
        }

        var content = new StringBuilder();
        string? finalThinking = null;
        var emittedFinal = false;
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);
        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var delta = ReadOllamaDelta(root, out var thinkingDelta);
            if (!string.IsNullOrEmpty(delta))
            {
                content.Append(delta);
            }
            if (!string.IsNullOrWhiteSpace(thinkingDelta))
            {
                finalThinking = string.IsNullOrWhiteSpace(finalThinking) ? thinkingDelta : finalThinking + thinkingDelta;
            }

            var done = root.TryGetProperty("done", out var doneElement) && doneElement.ValueKind == JsonValueKind.True;
            if (!done && (!string.IsNullOrEmpty(delta) || !string.IsNullOrEmpty(thinkingDelta)))
            {
                yield return new ChatProviderChunk(delta, thinkingDelta, null, null, IsFinal: false, Name, model);
            }
            else if (done)
            {
                var (visible, thinking) = SplitThinking(content.ToString(), finalThinking);
                var usage = ReadOllamaUsage(root);
                emittedFinal = true;
                yield return new ChatProviderChunk(string.Empty, null, visible, thinking, IsFinal: true, Name, model, Usage: usage);
            }
        }

        if (!emittedFinal && content.Length > 0)
        {
            var (visible, thinking) = SplitThinking(content.ToString(), finalThinking);
            yield return new ChatProviderChunk(string.Empty, null, visible, thinking, IsFinal: true, Name, model);
        }
    }

    private static ChatUsageSummary? ReadOllamaUsage(JsonElement root)
    {
        var inputTokens = ReadIntProperty(root, "prompt_eval_count");
        var outputTokens = ReadIntProperty(root, "eval_count");
        if (inputTokens is null && outputTokens is null)
        {
            return null;
        }

        return new ChatUsageSummary(
            inputTokens ?? 0,
            outputTokens ?? 0,
            inputTokens,
            IsEstimate: false);
    }

    private static int? ReadIntProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : null;

    private static List<OllamaMessage> BuildOllamaMessages(ChatProviderRequest request)
    {
        var imagePayloads = request.Attachments?
            .Where(attachment => attachment.IsImage)
            .Select(ChatAttachmentFiles.ReadTrustedImageBase64)
            .Where(payload => !string.IsNullOrWhiteSpace(payload))
            .Select(payload => payload!)
            .ToArray() ?? [];
        var lastUserIndex = request.Messages.ToList().FindLastIndex(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));

        return request.Messages.Select((message, index) => new OllamaMessage
        {
            Role = message.Role,
            Content = message.Content,
            Images = index == lastUserIndex && imagePayloads.Length > 0 ? imagePayloads : null
        }).ToList();
    }

    private static string ReadOllamaDelta(JsonElement root, out string? thinkingDelta)
    {
        thinkingDelta = null;
        if (root.TryGetProperty("message", out var message))
        {
            if (message.TryGetProperty("thinking", out var thinkingElement) && thinkingElement.ValueKind == JsonValueKind.String)
            {
                thinkingDelta = thinkingElement.GetString();
            }

            if (message.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String)
            {
                return contentElement.GetString() ?? string.Empty;
            }
        }

        if (root.TryGetProperty("response", out var responseElement) && responseElement.ValueKind == JsonValueKind.String)
        {
            return responseElement.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    public async Task<IReadOnlyList<ChatModelSummary>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var chatOptions = _options.CurrentValue.Chat;
        var endpoint = new Uri(new Uri(chatOptions.OllamaEndpoint.TrimEnd('/') + "/"), "api/tags");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(chatOptions.RequestTimeoutSeconds, 5, 600)));

        using var response = await _httpClient.GetAsync(endpoint, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Ollama returned {(int)response.StatusCode}: {body}");
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return models.EnumerateArray()
            .Select(ReadOllamaModel)
            .Where(model => !string.IsNullOrWhiteSpace(model.Name))
            .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ChatModelSummary ReadOllamaModel(JsonElement model)
    {
        var name = model.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString() ?? string.Empty
            : string.Empty;
        DateTimeOffset? modifiedAt = null;
        if (model.TryGetProperty("modified_at", out var modifiedElement) && modifiedElement.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(modifiedElement.GetString(), out var parsedModified))
        {
            modifiedAt = parsedModified;
        }

        long? size = null;
        if (model.TryGetProperty("size", out var sizeElement) && sizeElement.ValueKind == JsonValueKind.Number && sizeElement.TryGetInt64(out var parsedSize))
        {
            size = parsedSize;
        }

        return new ChatModelSummary(name, modifiedAt, size);
    }

    private static (string Content, string? Thinking) ReadOllamaContent(JsonElement root)
    {
        string? thinking = null;
        if (root.TryGetProperty("message", out var message) &&
            message.TryGetProperty("thinking", out var thinkingElement) &&
            thinkingElement.ValueKind == JsonValueKind.String)
        {
            thinking = thinkingElement.GetString();
        }

        if (root.TryGetProperty("message", out message) &&
            message.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.String)
        {
            return SplitThinking(content.GetString() ?? string.Empty, thinking);
        }

        if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.String)
        {
            return SplitThinking(response.GetString() ?? string.Empty, thinking);
        }

        return (root.ToString(), thinking);
    }

    private static (string Content, string? Thinking) SplitThinking(string content, string? thinking)
    {
        var match = ThinkingPatternRegex().Match(content);
        if (!match.Success)
        {
            return (content.Trim(), string.IsNullOrWhiteSpace(thinking) ? null : thinking.Trim());
        }

        var visible = (content[..match.Index] + content[(match.Index + match.Length)..]).Trim();
        return (visible, string.IsNullOrWhiteSpace(thinking) ? match.Groups[1].Value.Trim() : thinking.Trim());
    }

    private sealed class OllamaMessage
    {
        public string Role { get; init; } = string.Empty;
        public string Content { get; init; } = string.Empty;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? Images { get; init; }
    }

    [GeneratedRegex("<think>(.*?)</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkingPatternRegex();
}

public sealed class GitHubCopilotChatProvider : IChatProvider
{
    private readonly IOptionsMonitor<MemorySmithOptions> _options;

    public GitHubCopilotChatProvider(IOptionsMonitor<MemorySmithOptions> options)
    {
        _options = options;
    }

    public string Name => "GitHub";

    public async Task<ChatProviderResponse> CompleteAsync(ChatProviderRequest request, CancellationToken cancellationToken)
    {
        var content = new StringBuilder();
        var thinking = new StringBuilder();
        string? finalContent = null;
        string? finalThinking = null;
        ChatUsageSummary? finalUsage = null;
        var model = string.Empty;

        await foreach (var chunk in StreamAsync(request, cancellationToken))
        {
            model = chunk.Model;
            if (!string.IsNullOrEmpty(chunk.ContentDelta))
            {
                content.Append(chunk.ContentDelta);
            }
            if (!string.IsNullOrEmpty(chunk.ThinkingDelta))
            {
                thinking.Append(chunk.ThinkingDelta);
            }
            if (chunk.IsFinal)
            {
                finalContent = chunk.FinalContent;
                finalThinking = chunk.FinalThinking;
                finalUsage = chunk.Usage;
            }
        }

        return new ChatProviderResponse(
            finalContent ?? content.ToString(),
            Name,
            string.IsNullOrWhiteSpace(model) ? ResolveModel(request, _options.CurrentValue.Chat) : model,
            finalThinking ?? (thinking.Length == 0 ? null : thinking.ToString()),
            finalUsage);
    }

    public async IAsyncEnumerable<ChatProviderChunk> StreamAsync(ChatProviderRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var chatOptions = _options.CurrentValue.Chat;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(chatOptions.RequestTimeoutSeconds, 5, 600)));

        var model = ResolveModel(request, chatOptions);
        var channel = Channel.CreateUnbounded<ChatProviderChunk>();
        var content = new StringBuilder();
        var thinking = new StringBuilder();
        string? finalContent = null;
        string? finalThinking = null;
        ChatUsageSummary? usage = null;
        int? tokenLimit = null;
        int? currentTokens = null;

        await using var client = CreateClient(chatOptions);
        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = model,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Streaming = true,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false }
        }, timeout.Token);

        using var registration = timeout.Token.Register(() => channel.Writer.TryComplete(new OperationCanceledException(timeout.Token)));
        using var subscription = session.On(evt =>
        {
            try
            {
                switch (evt)
                {
                    case AssistantMessageDeltaEvent delta:
                        var deltaContent = delta.Data.DeltaContent ?? string.Empty;
                        if (!string.IsNullOrEmpty(deltaContent))
                        {
                            content.Append(deltaContent);
                            channel.Writer.TryWrite(new ChatProviderChunk(deltaContent, null, null, null, IsFinal: false, Name, model));
                        }
                        break;
                    case AssistantReasoningDeltaEvent reasoningDelta:
                        var reasoningContent = reasoningDelta.Data.DeltaContent ?? string.Empty;
                        if (!string.IsNullOrEmpty(reasoningContent))
                        {
                            thinking.Append(reasoningContent);
                            channel.Writer.TryWrite(new ChatProviderChunk(string.Empty, reasoningContent, null, null, IsFinal: false, Name, model));
                        }
                        break;
                    case AssistantMessageEvent message:
                        finalContent = message.Data.Content;
                        break;
                    case AssistantReasoningEvent reasoning:
                        finalThinking = reasoning.Data.Content;
                        break;
                    case SessionUsageInfoEvent usageInfo:
                        tokenLimit = ReadIntProperty(usageInfo.Data, "TokenLimit");
                        currentTokens = ReadIntProperty(usageInfo.Data, "CurrentTokens");
                        usage = MergeUsage(usage, new ChatUsageSummary(
                            usage?.InputTokens ?? currentTokens ?? 0,
                            usage?.OutputTokens ?? 0,
                            currentTokens,
                            tokenLimit,
                            usage?.RateLimit,
                            IsEstimate: false));
                        channel.Writer.TryWrite(new ChatProviderChunk(
                            string.Empty,
                            null,
                            null,
                            null,
                            IsFinal: false,
                            Name,
                            model,
                            Status: "Context usage updated",
                            Usage: usage));
                        break;
                    case AssistantUsageEvent assistantUsage:
                        usage = MergeUsage(usage, ReadGitHubUsage(assistantUsage.Data, tokenLimit, currentTokens));
                        channel.Writer.TryWrite(new ChatProviderChunk(
                            string.Empty,
                            null,
                            null,
                            null,
                            IsFinal: false,
                            Name,
                            model,
                            Status: "Usage updated",
                            Usage: usage));
                        break;
                    case SessionIdleEvent:
                        channel.Writer.TryWrite(new ChatProviderChunk(
                            string.Empty,
                            null,
                            finalContent ?? content.ToString(),
                            finalThinking ?? (thinking.Length == 0 ? null : thinking.ToString()),
                            IsFinal: true,
                            Name,
                            model,
                            Usage: usage));
                        channel.Writer.TryComplete();
                        break;
                    case SessionErrorEvent error:
                        channel.Writer.TryComplete(new InvalidOperationException(error.Data.Message));
                        break;
                }
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        });

        await session.SendAsync(new MessageOptions
        {
            Prompt = FormatGitHubPrompt(request.Messages),
            Attachments = BuildGitHubAttachments(request.Attachments)
        }, timeout.Token);

        await foreach (var chunk in channel.Reader.ReadAllAsync(timeout.Token))
        {
            yield return chunk;
        }
    }

    public async Task<IReadOnlyList<ChatModelSummary>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var chatOptions = _options.CurrentValue.Chat;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(chatOptions.RequestTimeoutSeconds, 5, 600)));

        try
        {
            await using var client = CreateClient(chatOptions);
            var models = await client.ListModelsAsync(timeout.Token);
            var discovered = models
                .Select(ReadSdkModel)
                .Where(model => !string.IsNullOrWhiteSpace(model.Name))
                .ToList();
            return MergeConfiguredModels(discovered, chatOptions.GitHubModels);
        }
        catch when (chatOptions.GitHubModels.Count > 0)
        {
            return MergeConfiguredModels([], chatOptions.GitHubModels);
        }
    }

    private static SdkCopilotClient CreateClient(ChatOptions chatOptions)
    {
        var sdkOptions = new SdkCopilotClientOptions { LogLevel = "warning" };
        var token = ResolveToken(chatOptions);
        if (!string.IsNullOrWhiteSpace(token))
        {
            sdkOptions.GitHubToken = token;
        }

        if (!string.IsNullOrWhiteSpace(chatOptions.GitHubCliPath))
        {
            sdkOptions.CliPath = chatOptions.GitHubCliPath;
        }

        if (!string.IsNullOrWhiteSpace(chatOptions.GitHubCliUrl))
        {
            sdkOptions.CliUrl = chatOptions.GitHubCliUrl;
        }

        return new SdkCopilotClient(sdkOptions);
    }

    private static string ResolveModel(ChatProviderRequest request, ChatOptions chatOptions) =>
        string.IsNullOrWhiteSpace(request.Model) ? chatOptions.GitHubModel : request.Model.Trim();

    private static string? ResolveToken(ChatOptions chatOptions)
    {
        foreach (var name in new[] { chatOptions.GitHubTokenEnvironmentVariable, "GH_TOKEN", "COPILOT_API_KEY" })
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string FormatGitHubPrompt(IReadOnlyList<ChatMessage> messages) =>
        string.Join("\n\n", messages.Select(message => $"{message.Role.ToUpperInvariant()}:\n{message.Content}"));

    private static List<UserMessageDataAttachmentsItem>? BuildGitHubAttachments(IReadOnlyList<ChatAttachment>? attachments)
    {
        if (attachments is null)
        {
            return null;
        }

        var result = new List<UserMessageDataAttachmentsItem>();
        foreach (var attachment in attachments.Where(attachment => attachment.IsImage))
        {
            var payload = ChatAttachmentFiles.ReadTrustedImageBase64(attachment);
            if (string.IsNullOrWhiteSpace(payload))
            {
                continue;
            }

            result.Add(new UserMessageDataAttachmentsItemBlob
            {
                Data = payload,
                MimeType = string.IsNullOrWhiteSpace(attachment.ContentType) ? "image/png" : attachment.ContentType
            });
        }

        return result.Count == 0 ? null : result;
    }

    private static ChatModelSummary ReadSdkModel(object model)
    {
        var name = ReadStringProperty(model, "Id")
            ?? ReadStringProperty(model, "ModelId")
            ?? ReadStringProperty(model, nameof(ChatModelSummary.Name))
            ?? ReadStringProperty(model, "Model")
            ?? string.Empty;
        var multiplier = ReadDoubleProperty(model, "ChatMultiplier")
            ?? ReadDoubleProperty(model, "PremiumMultiplier")
            ?? ReadDoubleProperty(model, "PremiumRequestMultiplier")
            ?? ReadDoubleProperty(model, "Multiplier")
            ?? ReadNestedDoubleProperty(model, "Billing", "Multiplier");
        var description = ReadStringProperty(model, "Description") ?? ReadStringProperty(model, "Family");
        var contextWindowTokens = ReadNestedIntProperty(model, "Capabilities", "Limits", "MaxContextWindowTokens");
        return new ChatModelSummary(name, Provider: "GitHub", ChatMultiplier: multiplier, Description: description, ContextWindowTokens: contextWindowTokens);
    }

    private static List<ChatModelSummary> MergeConfiguredModels(IEnumerable<ChatModelSummary> discovered, IReadOnlyList<ChatModelOption> configured)
    {
        var configuredItems = configured
            .Where(model => !string.IsNullOrWhiteSpace(model.Name))
            .Select((model, index) => new ConfiguredChatModel(model, index))
            .GroupBy(item => item.Option.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var configuredByName = configuredItems.ToDictionary(item => item.Option.Name, StringComparer.OrdinalIgnoreCase);
        var merged = new Dictionary<string, ChatModelSummary>(StringComparer.OrdinalIgnoreCase);

        foreach (var model in discovered)
        {
            configuredByName.TryGetValue(model.Name, out var configuredModel);
            merged[model.Name] = model with
            {
                Provider = "GitHub",
                ChatMultiplier = model.ChatMultiplier ?? configuredModel?.Option.ChatMultiplier,
                IsPreferred = configuredModel?.Option.IsPreferred ?? false,
                Description = model.Description ?? configuredModel?.Option.Description,
                ContextWindowTokens = model.ContextWindowTokens ?? configuredModel?.Option.ContextWindowTokens,
                RateLimit = model.RateLimit ?? configuredModel?.Option.RateLimit
            };
        }

        foreach (var item in configuredItems)
        {
            var model = item.Option;
            if (!merged.ContainsKey(model.Name))
            {
                merged[model.Name] = new ChatModelSummary(
                    model.Name,
                    Provider: "GitHub",
                    ChatMultiplier: model.ChatMultiplier,
                    IsPreferred: model.IsPreferred,
                    Description: model.Description,
                    ContextWindowTokens: model.ContextWindowTokens,
                    RateLimit: model.RateLimit);
            }
        }

        return merged.Values
            .OrderByDescending(model => model.IsPreferred)
            .ThenBy(model => model.ChatMultiplier ?? double.MaxValue)
            .ThenBy(model => configuredByName.TryGetValue(model.Name, out var configuredModel) ? configuredModel.Index : int.MaxValue)
            .ThenBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed record ConfiguredChatModel(ChatModelOption Option, int Index);

    private static string? ReadStringProperty(object instance, string propertyName) =>
        instance.GetType().GetProperty(propertyName)?.GetValue(instance)?.ToString();

    private static object? ReadObjectProperty(object instance, string propertyName) =>
        instance.GetType().GetProperty(propertyName)?.GetValue(instance);

    private static double? ReadDoubleProperty(object instance, string propertyName)
    {
        var value = ReadObjectProperty(instance, propertyName);
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToDouble(value);
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadIntProperty(object instance, string propertyName)
    {
        var value = ReadObjectProperty(instance, propertyName);
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    private static double? ReadNestedDoubleProperty(object instance, params string[] propertyPath)
    {
        var value = ReadNestedProperty(instance, propertyPath);
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToDouble(value);
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadNestedIntProperty(object instance, params string[] propertyPath)
    {
        var value = ReadNestedProperty(instance, propertyPath);
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    private static object? ReadNestedProperty(object instance, params string[] propertyPath)
    {
        object? current = instance;
        foreach (var propertyName in propertyPath)
        {
            if (current is null)
            {
                return null;
            }

            current = ReadObjectProperty(current, propertyName);
        }

        return current;
    }

    private static ChatUsageSummary ReadGitHubUsage(object data, int? tokenLimit, int? currentTokens)
    {
        var inputTokens = ReadIntProperty(data, "InputTokens") ?? currentTokens ?? 0;
        var outputTokens = ReadIntProperty(data, "OutputTokens") ?? 0;
        return new ChatUsageSummary(
            inputTokens,
            outputTokens,
            currentTokens ?? inputTokens,
            tokenLimit,
            FormatQuotaSnapshots(data),
            IsEstimate: false);
    }

    private static ChatUsageSummary MergeUsage(ChatUsageSummary? current, ChatUsageSummary next) =>
        new(
            next.InputTokens > 0 ? next.InputTokens : current?.InputTokens ?? 0,
            next.OutputTokens > 0 ? next.OutputTokens : current?.OutputTokens ?? 0,
            next.ContextTokens ?? current?.ContextTokens,
            next.ContextWindowTokens ?? current?.ContextWindowTokens,
            next.RateLimit ?? current?.RateLimit,
            next.IsEstimate && (current?.IsEstimate ?? true));

    private static string? FormatQuotaSnapshots(object data)
    {
        var snapshots = ReadObjectProperty(data, "QuotaSnapshots");
        if (snapshots is not System.Collections.IEnumerable enumerable)
        {
            return null;
        }

        var entries = new List<string>();
        foreach (var entry in enumerable)
        {
            var key = ReadObjectProperty(entry, "Key")?.ToString();
            var value = ReadObjectProperty(entry, "Value");
            if (string.IsNullOrWhiteSpace(key) || value is null)
            {
                continue;
            }

            var remaining = ReadDoubleProperty(value, "RemainingPercentage");
            var reset = ReadStringProperty(value, "ResetDate");
            if (remaining is null)
            {
                continue;
            }

            var resetText = string.IsNullOrWhiteSpace(reset) ? string.Empty : $", reset {reset}";
            entries.Add($"{key}: {remaining:0.#}% left{resetText}");
            if (entries.Count == 2)
            {
                break;
            }
        }

        return entries.Count == 0 ? null : string.Join("; ", entries);
    }
}

public sealed partial class MemoryChatAgent : IChatAgent
{
    private static readonly Regex SafeIdPattern = SafeMemoryIdRegex();
    private static readonly JsonSerializerOptions ToolJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly HashSet<string> ChatToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "memorysmith_search",
        "memorysmith_semantic_search",
        "memorysmith_hybrid_search",
        "memorysmith_context_pack",
        "memorysmith_get"
    };

    private readonly List<IChatProvider> _providers;
    private readonly MemoryApplicationService _memories;
    private readonly IPageService _pages;
    private readonly IOptions<MemorySmithOptions> _options;
    private readonly ICurrentUserContext? _currentUser;

    public MemoryChatAgent(
        IEnumerable<IChatProvider> providers,
        MemoryApplicationService memories,
        IPageService pages,
        IOptions<MemorySmithOptions> options,
        ICurrentUserContext? currentUser = null)
    {
        _providers = providers.ToList();
        if (_providers.Count == 0)
        {
            throw new InvalidOperationException("At least one chat provider must be registered.");
        }

        _memories = memories;
        _pages = pages;
        _options = options;
        _currentUser = currentUser;
    }

    public async Task<MemoryChatResponse> SendAsync(MemoryChatRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            throw new ArgumentException("Message is required.", nameof(request));
        }

        var context = await BuildContextAsync(request.Message, cancellationToken);
        var messages = BuildMessages(request, context);
        var provider = ResolveProvider(request.Provider);
        var toolLoop = await CompleteWithToolCallsAsync(provider, request, messages, cancellationToken);
        var providerResponse = toolLoop.Response;

        return await BuildResponseAsync(request.Mode, providerResponse, context, cancellationToken);
    }

    public async IAsyncEnumerable<MemoryChatStreamUpdate> StreamAsync(MemoryChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            throw new ArgumentException("Message is required.", nameof(request));
        }

        var context = await BuildContextAsync(request.Message, cancellationToken);
        var messages = BuildMessages(request, context);
        var provider = ResolveProvider(request.Provider);
        var resolvedModel = string.IsNullOrWhiteSpace(request.Model) ? DefaultModelForProvider(provider.Name) : request.Model.Trim();
        var currentUsage = CompleteUsage(provider.Name, resolvedModel, messages, string.Empty, null);
        yield return new MemoryChatStreamUpdate(Context: context, Status: $"Loaded {context.Count} local resource(s)", Usage: currentUsage);

        ChatUsageSummary? aggregateUsage = null;
        var maxToolIterations = MaxToolIterations();
        for (var iteration = 0; ; iteration++)
        {
            var content = new StringBuilder();
            var thinking = new StringBuilder();
            ChatProviderChunk? finalChunk = null;
            var bufferVisibleContent = _options.Value.Chat.ToolCallsEnabled;

            await foreach (var chunk in provider.StreamAsync(new ChatProviderRequest(messages, request.Mode, request.Model, request.Attachments, provider.Name), cancellationToken))
            {
                if (!string.IsNullOrEmpty(chunk.ContentDelta))
                {
                    content.Append(chunk.ContentDelta);
                }
                if (!string.IsNullOrEmpty(chunk.ThinkingDelta))
                {
                    thinking.Append(chunk.ThinkingDelta);
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

                yield return new MemoryChatStreamUpdate(contentDelta, chunk.ThinkingDelta, Status: chunk.Status, Usage: currentUsage);
            }

            var providerResponse = new ChatProviderResponse(
                finalChunk?.FinalContent ?? content.ToString(),
                finalChunk?.ProviderName ?? provider.Name,
                finalChunk?.Model ?? request.Model ?? DefaultModelForProvider(provider.Name),
                finalChunk?.FinalThinking ?? (thinking.Length == 0 ? null : thinking.ToString()),
                finalChunk?.Usage ?? currentUsage);
            var completedUsage = CompleteUsage(providerResponse.ProviderName, providerResponse.Model, messages, providerResponse.Content, providerResponse.Usage);
            aggregateUsage = MergeTurnUsage(aggregateUsage, completedUsage);
            currentUsage = aggregateUsage;
            providerResponse = providerResponse with { Usage = aggregateUsage };

            var toolCalls = _options.Value.Chat.ToolCallsEnabled ? ReadToolCalls(providerResponse.Content) : [];
            if (toolCalls.Count > 0)
            {
                if (iteration >= maxToolIterations)
                {
                    providerResponse = providerResponse with
                    {
                        Content = "The model requested another MemorySmith wiki tool call after the configured tool-iteration limit. Try narrowing the request or increasing Chat:MaxToolIterations."
                    };
                    var limitedResponse = await BuildResponseAsync(request.Mode, providerResponse, context, cancellationToken);
                    yield return new MemoryChatStreamUpdate(IsFinal: true, Response: limitedResponse, Context: context, Usage: limitedResponse.Usage);
                    yield break;
                }

                var toolResults = await ExecuteToolCallsAsync(toolCalls, cancellationToken);
                messages.Add(new ChatMessage("assistant", providerResponse.Content));
                messages.Add(new ChatMessage("system", FormatToolResults(toolResults)));
                yield return new MemoryChatStreamUpdate(
                    Status: $"Ran {toolResults.Count} MemorySmith wiki tool call(s): {string.Join(", ", toolResults.Select(result => result.Name).Distinct(StringComparer.OrdinalIgnoreCase))}",
                    Usage: currentUsage);
                continue;
            }

            var response = await BuildResponseAsync(request.Mode, providerResponse, context, cancellationToken);
            yield return new MemoryChatStreamUpdate(IsFinal: true, Response: response, Context: context, Usage: response.Usage);
            yield break;
        }
    }

    private async Task<ToolLoopResult> CompleteWithToolCallsAsync(
        IChatProvider provider,
        MemoryChatRequest request,
        IReadOnlyList<ChatMessage> initialMessages,
        CancellationToken cancellationToken)
    {
        var messages = initialMessages.ToList();
        ChatUsageSummary? aggregateUsage = null;
        var maxToolIterations = MaxToolIterations();
        for (var iteration = 0; ; iteration++)
        {
            var providerResponse = await provider.CompleteAsync(
                new ChatProviderRequest(messages, request.Mode, request.Model, request.Attachments, provider.Name),
                cancellationToken);
            var completedUsage = CompleteUsage(providerResponse.ProviderName, providerResponse.Model, messages, providerResponse.Content, providerResponse.Usage);
            aggregateUsage = MergeTurnUsage(aggregateUsage, completedUsage);
            providerResponse = providerResponse with { Usage = aggregateUsage };

            var toolCalls = _options.Value.Chat.ToolCallsEnabled ? ReadToolCalls(providerResponse.Content) : [];
            if (toolCalls.Count == 0)
            {
                return new ToolLoopResult(providerResponse, messages);
            }

            if (iteration >= maxToolIterations)
            {
                return new ToolLoopResult(providerResponse with
                {
                    Content = "The model requested another MemorySmith wiki tool call after the configured tool-iteration limit. Try narrowing the request or increasing Chat:MaxToolIterations."
                }, messages);
            }

            var toolResults = await ExecuteToolCallsAsync(toolCalls, cancellationToken);
            messages.Add(new ChatMessage("assistant", providerResponse.Content));
            messages.Add(new ChatMessage("system", FormatToolResults(toolResults)));
        }
    }

    private async Task<MemoryChatResponse> BuildResponseAsync(
        MemoryChatMode mode,
        ChatProviderResponse providerResponse,
        IReadOnlyList<ChatContextItem> context,
        CancellationToken cancellationToken)
    {
        if (mode == MemoryChatMode.Agent)
        {
            var agentResult = await TryApplyAgentActionsAsync(providerResponse.Content, cancellationToken);
            return new MemoryChatResponse(
                agentResult.Reply,
                providerResponse.ProviderName,
                providerResponse.Model,
                providerResponse.Thinking,
                context,
                agentResult.WrittenMemories,
                agentResult.WrittenPages,
                providerResponse.Usage);
        }

        return new MemoryChatResponse(
            providerResponse.Content,
            providerResponse.ProviderName,
            providerResponse.Model,
            providerResponse.Thinking,
            context,
            [],
                [],
                providerResponse.Usage);
    }

    private IChatProvider ResolveProvider(string? providerName)
    {
        var explicitProvider = !string.IsNullOrWhiteSpace(providerName);
        var configuredProvider = explicitProvider ? providerName! : _options.Value.Chat.Provider;
        var provider = _providers.FirstOrDefault(candidate => ProviderMatches(candidate.Name, configuredProvider));
        if (provider is not null)
        {
            return provider;
        }

        if (!explicitProvider && _providers.Count == 1)
        {
            return _providers[0];
        }

        throw new InvalidOperationException($"Chat provider '{configuredProvider}' is not registered.");
    }

    private string DefaultModelForProvider(string providerName) =>
        ProviderMatches(providerName, "GitHub") ? _options.Value.Chat.GitHubModel : _options.Value.Chat.OllamaModel;

    private static bool ProviderMatches(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
        (string.Equals(left, "GitHub", StringComparison.OrdinalIgnoreCase) && string.Equals(right, "Copilot", StringComparison.OrdinalIgnoreCase));

    private ChatUsageSummary CompleteUsage(
        string providerName,
        string model,
        IReadOnlyList<ChatMessage> messages,
        string output,
        ChatUsageSummary? providerUsage)
    {
        var metadata = ResolveUsageMetadata(providerName, model);
        var inputEstimate = EstimateTokens(messages);
        var outputEstimate = EstimateTokens(output);
        if (providerUsage is null)
        {
            return new ChatUsageSummary(
                inputEstimate,
                outputEstimate,
                inputEstimate,
                metadata.ContextWindowTokens,
                metadata.RateLimit,
                IsEstimate: true);
        }

        return providerUsage with
        {
            InputTokens = providerUsage.InputTokens > 0 ? providerUsage.InputTokens : inputEstimate,
            OutputTokens = providerUsage.OutputTokens > 0 ? providerUsage.OutputTokens : outputEstimate,
            ContextTokens = providerUsage.ContextTokens ?? (providerUsage.InputTokens > 0 ? providerUsage.InputTokens : inputEstimate),
            ContextWindowTokens = providerUsage.ContextWindowTokens ?? metadata.ContextWindowTokens,
            RateLimit = providerUsage.RateLimit ?? metadata.RateLimit
        };
    }

    private (int? ContextWindowTokens, string? RateLimit) ResolveUsageMetadata(string providerName, string model)
    {
        var chatOptions = _options.Value.Chat;
        if (ProviderMatches(providerName, "GitHub"))
        {
            var configured = chatOptions.GitHubModels.FirstOrDefault(item => string.Equals(item.Name, model, StringComparison.OrdinalIgnoreCase));
            return (configured?.ContextWindowTokens, configured?.RateLimit);
        }

        return (chatOptions.OllamaContextWindowTokens, null);
    }

    private int MaxToolIterations() =>
        _options.Value.Chat.ToolCallsEnabled ? Math.Clamp(_options.Value.Chat.MaxToolIterations, 0, 5) : 0;

    private static ChatUsageSummary MergeTurnUsage(ChatUsageSummary? current, ChatUsageSummary next) =>
        current is null
            ? next
            : new ChatUsageSummary(
                Math.Max(0, current.InputTokens) + Math.Max(0, next.InputTokens),
                Math.Max(0, current.OutputTokens) + Math.Max(0, next.OutputTokens),
                next.ContextTokens ?? current.ContextTokens,
                next.ContextWindowTokens ?? current.ContextWindowTokens,
                next.RateLimit ?? current.RateLimit,
                current.IsEstimate || next.IsEstimate);

    private static bool IsPotentialToolCallPrefix(string content)
    {
        var trimmed = content.TrimStart();
        return trimmed.Length == 0 || trimmed[0] is '{' or '[' or '`';
    }

    private static IReadOnlyList<ChatToolCall> ReadToolCalls(string content)
    {
        var json = StripJsonFence(content);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var root = JsonNode.Parse(json);
            var calls = new List<ChatToolCall>();
            CollectToolCalls(root, calls);
            return calls;
        }
        catch
        {
            return [];
        }
    }

    private static void CollectToolCalls(JsonNode? node, List<ChatToolCall> calls)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    CollectToolCalls(item, calls);
                }
                break;
            case JsonObject obj:
                if (ReadString(obj, "method") is { } method && string.Equals(method, "tools/call", StringComparison.OrdinalIgnoreCase))
                {
                    AddMcpToolCall(obj, calls);
                    break;
                }

                if (GetProperty(obj, "toolCalls") is JsonArray toolCalls)
                {
                    foreach (var item in toolCalls)
                    {
                        AddToolCall(item as JsonObject, calls);
                    }
                    break;
                }

                if (GetProperty(obj, "toolCall") is JsonObject toolCall)
                {
                    AddToolCall(toolCall, calls);
                    break;
                }

                AddToolCall(obj, calls);
                break;
        }
    }

    private static void AddMcpToolCall(JsonObject obj, List<ChatToolCall> calls)
    {
        if (GetProperty(obj, "params") is not JsonObject parameters)
        {
            return;
        }

        var name = ReadString(parameters, "name");
        var arguments = ReadArguments(parameters);
        AddToolCall(name, arguments, calls);
    }

    private static void AddToolCall(JsonObject? obj, List<ChatToolCall> calls)
    {
        if (obj is null)
        {
            return;
        }

        var name = ReadString(obj, "name") ?? ReadString(obj, "tool");
        if (string.IsNullOrWhiteSpace(name) && GetProperty(obj, "function") is JsonObject function)
        {
            name = ReadString(function, "name");
            obj = function;
        }

        AddToolCall(name, ReadArguments(obj), calls);
    }

    private static void AddToolCall(string? name, JsonObject arguments, List<ChatToolCall> calls)
    {
        if (string.IsNullOrWhiteSpace(name) || !ChatToolNames.Contains(name))
        {
            return;
        }

        calls.Add(new ChatToolCall(name.Trim(), arguments));
    }

    private static JsonObject ReadArguments(JsonObject obj)
    {
        var node = GetProperty(obj, "arguments") ?? GetProperty(obj, "args");
        if (node is JsonObject arguments)
        {
            return CloneJsonObject(arguments);
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
        {
            try
            {
                return JsonNode.Parse(text) as JsonObject ?? new JsonObject { ["query"] = text };
            }
            catch
            {
                return new JsonObject { ["query"] = text };
            }
        }

        return new JsonObject();
    }

    private static JsonObject CloneJsonObject(JsonObject source) =>
        JsonNode.Parse(source.ToJsonString()) as JsonObject ?? new JsonObject();

    private async Task<IReadOnlyList<ChatToolResult>> ExecuteToolCallsAsync(
        IReadOnlyList<ChatToolCall> toolCalls,
        CancellationToken cancellationToken)
    {
        var options = _options.Value.Chat;
        var maxCalls = Math.Clamp(options.MaxToolCallsPerTurn, 1, 10);
        var maxResultCharacters = Math.Clamp(options.MaxToolResultCharacters, 1000, 100000);
        var results = new List<ChatToolResult>();

        foreach (var toolCall in toolCalls.Take(maxCalls))
        {
            try
            {
                var content = await ExecuteToolCallAsync(toolCall, cancellationToken);
                results.Add(new ChatToolResult(toolCall.Name, Truncate(content, maxResultCharacters), IsError: false));
            }
            catch (Exception ex)
            {
                results.Add(new ChatToolResult(toolCall.Name, ex.Message, IsError: true));
            }
        }

        if (toolCalls.Count > maxCalls)
        {
            results.Add(new ChatToolResult("tool-limit", $"Skipped {toolCalls.Count - maxCalls} tool call(s) because Chat:MaxToolCallsPerTurn is {maxCalls}.", IsError: true));
        }

        return results;
    }

    private async Task<string> ExecuteToolCallAsync(ChatToolCall toolCall, CancellationToken cancellationToken) =>
        toolCall.Name.ToLowerInvariant() switch
        {
            "memorysmith_search" => FormatLexicalResults(await _memories.SearchAsync(ReadLexicalQuery(toolCall.Arguments), cancellationToken)),
            "memorysmith_semantic_search" => FormatSemanticResults(await _memories.SemanticSearchAsync(ReadSemanticQuery(toolCall.Arguments), cancellationToken)),
            "memorysmith_hybrid_search" => FormatHybridResults(await _memories.HybridSearchAsync(ReadHybridQuery(toolCall.Arguments), cancellationToken)),
            "memorysmith_context_pack" => FormatContextPack(
                await _memories.BuildContextPackAsync(ReadContextPackQuery(toolCall.Arguments), cancellationToken),
                ReadString(toolCall.Arguments, "format")),
            "memorysmith_get" => await FormatRecordAsync(toolCall.Arguments, cancellationToken),
            _ => $"Unknown MemorySmith tool '{toolCall.Name}'."
        };

    private static string FormatToolResults(IReadOnlyList<ChatToolResult> results)
    {
        if (results.Count == 0)
        {
            return "Local MemorySmith tool results: no calls were executed.";
        }

        return "Local MemorySmith tool results (application-executed MCP-compatible calls):\n" +
            string.Join("\n\n", results.Select(result =>
                $"Tool: {result.Name}\nStatus: {(result.IsError ? "error" : "ok")}\nResult:\n{result.Content}"));
    }

    private async Task<string> FormatRecordAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var id = ReadString(arguments, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return "The memorysmith_get tool requires an id argument.";
        }

        var record = await _memories.GetAsync(id, cancellationToken);
        return record is null
            ? $"No memory record found for id '{id}'."
            : JsonSerializer.Serialize(record, ToolJsonOptions);
    }

    private static MemorySearchQuery ReadLexicalQuery(JsonObject arguments) => new(
        Query: ReadString(arguments, "query"),
        Status: ReadStatus(arguments),
        Tags: ReadString(arguments, "tags"),
        Limit: ReadInt(arguments, "limit", 20));

    private static SemanticMemorySearchQuery ReadSemanticQuery(JsonObject arguments) => new(
        Query: ReadString(arguments, "query"),
        Status: ReadStatus(arguments),
        Tags: ReadString(arguments, "tags"),
        Limit: ReadInt(arguments, "limit", 20));

    private static HybridMemorySearchQuery ReadHybridQuery(JsonObject arguments) => new(
        Query: ReadString(arguments, "query"),
        Status: ReadStatus(arguments),
        Tags: ReadString(arguments, "tags"),
        Limit: ReadInt(arguments, "limit", 20));

    private static MemoryContextPackQuery ReadContextPackQuery(JsonObject arguments) => new(
        Query: ReadString(arguments, "query"),
        Status: ReadStatus(arguments),
        Tags: ReadString(arguments, "tags"),
        Limit: ReadInt(arguments, "limit", 5),
        ReferenceDepth: ReadInt(arguments, "referenceDepth", 1),
        MaxContentChars: ReadInt(arguments, "maxContentChars", 1200),
        MaxRecords: ReadInt(arguments, "maxRecords", 20),
        Ids: ReadString(arguments, "ids"),
        IncludeBacklinks: ReadBool(arguments, "includeBacklinks", false));

    private static string FormatLexicalResults(IReadOnlyList<MemoryRecord> records)
    {
        if (records.Count == 0)
        {
            return "No lexical search results.";
        }

        return string.Join(Environment.NewLine + Environment.NewLine, records.Select(record =>
            $"- {record.Id}: {record.Title}{Environment.NewLine}  Tags: {string.Join(", ", record.Tags)}{Environment.NewLine}  {Truncate(record.Content, 320)}"));
    }

    private static string FormatSemanticResults(IReadOnlyList<MemorySearchResult> results)
    {
        if (results.Count == 0)
        {
            return "No semantic search results.";
        }

        return string.Join(Environment.NewLine + Environment.NewLine, results.Select(result =>
            $"- {result.Id}: {result.Title}{Environment.NewLine}  Score: {result.Score:0.###}{Environment.NewLine}  Match: {result.MatchReason}{Environment.NewLine}  Tags: {string.Join(", ", result.Tags)}{Environment.NewLine}  {result.Snippet}"));
    }

    private static string FormatHybridResults(IReadOnlyList<MemorySearchResult> results)
    {
        if (results.Count == 0)
        {
            return "No hybrid search results.";
        }

        return string.Join(Environment.NewLine + Environment.NewLine, results.Select(result =>
            $"- {result.Id}: {result.Title}{Environment.NewLine}  RRF Score: {result.Score:0.######}{Environment.NewLine}  Match: {result.MatchReason}{Environment.NewLine}  Tags: {string.Join(", ", result.Tags)}{Environment.NewLine}  {result.Snippet}"));
    }

    private static string FormatContextPack(MemoryContextPack pack, string? format)
    {
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return JsonSerializer.Serialize(pack, ToolJsonOptions);
        }

        var warnings = pack.Warnings.Count == 0
            ? string.Empty
            : $"{Environment.NewLine}Warnings:{Environment.NewLine}" + string.Join(Environment.NewLine, pack.Warnings.Select(warning => $"- {warning}")) + Environment.NewLine;
        if (pack.Records.Count == 0)
        {
            return $"# Context Pack{Environment.NewLine}Query: {pack.Query ?? string.Empty}{Environment.NewLine}Generated: {pack.GeneratedAt:O}{warnings}{Environment.NewLine}No context pack records.";
        }

        var sections = pack.Records.Select(record =>
        {
            var scoreLine = record.Score.HasValue ? $"Score: {record.Score:0.######}" : "Score: linked context";
            var matchLine = string.IsNullOrWhiteSpace(record.MatchReason) ? string.Empty : $"Match: {record.MatchReason}{Environment.NewLine}";
            return $"## {record.Id}: {record.Title}{Environment.NewLine}" +
                   $"Relationship: {record.Relationship}{Environment.NewLine}" +
                   $"Status: {record.Status}; Confidence: {record.Confidence:P0}; Uses: {record.UsageCount}{Environment.NewLine}" +
                   $"Tags: {string.Join(", ", record.Tags)}{Environment.NewLine}" +
                   $"References: {FormatLinks(record.References)}{Environment.NewLine}" +
                   $"Conflicts: {FormatLinks(record.Conflicts)}{Environment.NewLine}" +
                   $"{scoreLine}{Environment.NewLine}" +
                   matchLine +
                   record.Content;
        });

        return $"# Context Pack{Environment.NewLine}Query: {pack.Query ?? string.Empty}{Environment.NewLine}Generated: {pack.GeneratedAt:O}{warnings}{Environment.NewLine}" +
            string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    private static string FormatLinks(IReadOnlyList<string> values) => values.Count == 0 ? "none" : string.Join(", ", values);

    private static string Truncate(string? value, int maxCharacters)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Trim();
        return text.Length <= maxCharacters ? text : text[..maxCharacters].TrimEnd() + "...";
    }

    private static int ReadInt(JsonObject item, string name, int fallback)
    {
        if (GetProperty(item, name) is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text) && int.TryParse(text, out number) ? number : fallback;
    }

    private static bool ReadBool(JsonObject item, string name, bool fallback)
    {
        if (GetProperty(item, name) is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue<bool>(out var boolean))
        {
            return boolean;
        }

        return value.TryGetValue<string>(out var text) && bool.TryParse(text, out boolean) ? boolean : fallback;
    }

    private static MemoryStatus? ReadStatus(JsonObject item) =>
        Enum.TryParse<MemoryStatus>(ReadString(item, "status"), ignoreCase: true, out var status) ? status : null;

    private static int EstimateTokens(IReadOnlyList<ChatMessage> messages) =>
        messages.Sum(message => EstimateTokens(message.Role) + EstimateTokens(message.Content) + 4);

    private static int EstimateTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
    }

    private async Task<List<ChatContextItem>> BuildContextAsync(string query, CancellationToken cancellationToken)
    {
        var options = _options.Value.Chat;
        var context = new List<ChatContextItem>();

        var memories = await _memories.HybridSearchAsync(new HybridMemorySearchQuery(query, Limit: Math.Clamp(options.MaxContextRecords, 0, 20)), cancellationToken);
        foreach (var memory in memories)
        {
            var record = await _memories.GetAsync(memory.Id, cancellationToken);
            var content = string.IsNullOrWhiteSpace(record?.Content) ? memory.Snippet : record.Content;
            context.Add(new ChatContextItem(
                "memory",
                memory.Id,
                memory.Title,
                TrimContextText(content, options.MaxContextItemCharacters)));
        }

        var pages = await _pages.SearchAsync(new PageSearchQuery(query, Math.Clamp(options.MaxContextPages, 0, 20)), cancellationToken);
        context.AddRange(pages.Select(page => new ChatContextItem(
            "page",
            page.Slug,
            page.Title,
            TrimContextText(page.Snippet, options.MaxContextItemCharacters))));

        return context;
    }

    private List<ChatMessage> BuildMessages(MemoryChatRequest request, IReadOnlyList<ChatContextItem> context)
    {
        var options = _options.Value.Chat;
        var messages = new List<ChatMessage>
        {
            new("system", BuildSystemPrompt(request.Mode)),
            new("system", FormatCurrentUser()),
            new("system", FormatContext(context))
        };

        var attachments = FormatAttachments(request.Attachments, options.MaxAttachmentCharacters);
        if (!string.IsNullOrWhiteSpace(attachments))
        {
            messages.Add(new ChatMessage("system", attachments));
        }

        if (request.History is not null)
        {
            messages.AddRange(request.History
                .Where(message => IsSupportedRole(message.Role) && !string.IsNullOrWhiteSpace(message.Content))
                .TakeLast(Math.Clamp(options.MaxHistoryMessages, 0, 64)));
        }

        messages.Add(new ChatMessage("user", request.Message));
        return messages;
    }

    private string FormatCurrentUser()
    {
        if (_currentUser?.IsAuthenticated != true)
        {
            return "Current MemorySmith user: Anonymous.";
        }

        var roles = _currentUser.Roles.Count == 0 ? "none" : string.Join(", ", _currentUser.Roles);
        return $"Current MemorySmith user: {_currentUser.DisplayName} (roles: {roles}).";
    }

    private string BuildSystemPrompt(MemoryChatMode mode)
    {
        var configuredPrompt = ReadConfiguredSystemPrompt();
        if (!string.IsNullOrWhiteSpace(configuredPrompt))
        {
            var toolPrompt = configuredPrompt.Contains("toolCalls", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : BuildToolProtocolPrompt(mode);
            return configuredPrompt + $"\n\nCurrent mode: {mode}." + toolPrompt;
        }

        var prompt = mode == MemoryChatMode.Agent
            ? "You are MemorySmith Agent. Answer the user and, only when useful, propose memoryWrites and pageWrites. Return strict JSON with keys reply, memoryWrites, and pageWrites. memoryWrites items may include id, title, content, tags, status, confidence. pageWrites items may include slug, title, markdown. Do not include markdown fences around the JSON."
            : "You are MemorySmith Chat. Answer the user's question using the supplied memories and pages when useful. Be direct when the local knowledge base does not contain enough evidence.";
        return prompt + BuildToolProtocolPrompt(mode);
    }

    private string BuildToolProtocolPrompt(MemoryChatMode mode)
    {
        if (!_options.Value.Chat.ToolCallsEnabled)
        {
            return string.Empty;
        }

        var finalInstruction = mode == MemoryChatMode.Agent
            ? "After tool results are supplied, return the normal strict Agent JSON with reply, memoryWrites, and pageWrites."
            : "After tool results are supplied, answer the user normally and do not expose the tool-call JSON.";
        return "\n\nLocal wiki tools are available through an application-intercepted MCP-compatible protocol. " +
            "When you need more MemorySmith wiki evidence than the preloaded context provides, respond with only one JSON object and no prose: " +
            "{\"toolCalls\":[{\"name\":\"memorysmith_hybrid_search\",\"arguments\":{\"query\":\"search text\",\"limit\":5}}]}. " +
            "Available read-only tools are memorysmith_search, memorysmith_semantic_search, memorysmith_hybrid_search, memorysmith_context_pack, and memorysmith_get. " +
            "The application will run the call locally and send results back in the same conversation turn. " +
            finalInstruction;
    }

    private string? ReadConfiguredSystemPrompt()
    {
        var path = _options.Value.Chat.SystemPromptPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var candidate in ResolvePromptPathCandidates(path))
        {
            if (File.Exists(candidate))
            {
                var prompt = File.ReadAllText(candidate).Trim();
                return string.IsNullOrWhiteSpace(prompt) ? null : prompt;
            }
        }

        return null;
    }

    private static IEnumerable<string> ResolvePromptPathCandidates(string path)
    {
        if (Path.IsPathRooted(path))
        {
            yield return path;
            yield break;
        }

        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        yield return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
    }

    private static string FormatContext(IReadOnlyList<ChatContextItem> context)
    {
        if (context.Count == 0)
        {
            return "Local MemorySmith context: no matching memories or pages were found.";
        }

        return "Local MemorySmith context (preloaded search/context results):\n" + string.Join("\n\n", context.Select(item =>
            $"[{item.Kind}] {item.Id} - {item.Title}\n{item.Snippet}"));
    }

    private static string TrimContextText(string value, int maxCharacters)
    {
        var text = value.Trim();
        var limit = Math.Clamp(maxCharacters, 0, 100_000);
        return text.Length <= limit ? text : text[..limit].TrimEnd() + "...";
    }

    private static string FormatAttachments(IReadOnlyList<ChatAttachment>? attachments, int maxCharacters)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return string.Empty;
        }

        var remaining = Math.Clamp(maxCharacters, 0, 2_000_000);
        var formatted = new List<string>();
        foreach (var attachment in attachments.Take(12))
        {
            var text = attachment.Text ?? string.Empty;
            if (remaining == 0)
            {
                text = string.Empty;
            }
            else if (text.Length > remaining)
            {
                text = text[..remaining] + "...";
                remaining = 0;
            }
            else
            {
                remaining -= text.Length;
            }

            var truncated = attachment.IsTruncated ? " truncated" : string.Empty;
            var payloadNote = attachment.IsImage
                ? "Image attachment is also supplied to the provider as a native image payload when supported."
                : text;
            formatted.Add($"Attachment: {attachment.Name} ({attachment.ContentType}, {attachment.Size} bytes{truncated})\n{payloadNote}".Trim());
        }

        return "User-provided attachments:\n\n" + string.Join("\n\n", formatted);
    }

    private async Task<AgentActionResult> TryApplyAgentActionsAsync(string providerContent, CancellationToken cancellationToken)
    {
        var writtenMemories = new List<string>();
        var writtenPages = new List<string>();
        var content = StripJsonFence(providerContent);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(content);
        }
        catch
        {
            return new AgentActionResult(providerContent, writtenMemories, writtenPages);
        }

        if (root is not JsonObject json)
        {
            return new AgentActionResult(providerContent, writtenMemories, writtenPages);
        }

        var reply = json["reply"]?.GetValue<string>() ?? providerContent;
        if (!_options.Value.Chat.AgentWritesEnabled)
        {
            return new AgentActionResult(reply, writtenMemories, writtenPages);
        }

        if (json["memoryWrites"] is JsonArray memoryWrites)
        {
            foreach (var item in memoryWrites.OfType<JsonObject>())
            {
                var written = await SaveMemoryActionAsync(item, cancellationToken);
                if (!string.IsNullOrWhiteSpace(written))
                {
                    writtenMemories.Add(written);
                }
            }
        }

        if (json["pageWrites"] is JsonArray pageWrites)
        {
            foreach (var item in pageWrites.OfType<JsonObject>())
            {
                var written = await SavePageActionAsync(item, cancellationToken);
                if (!string.IsNullOrWhiteSpace(written))
                {
                    writtenPages.Add(written);
                }
            }
        }

        return new AgentActionResult(reply, writtenMemories, writtenPages);
    }

    private async Task<string?> SaveMemoryActionAsync(JsonObject item, CancellationToken cancellationToken)
    {
        var title = ReadString(item, "title");
        var content = ReadString(item, "content");
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var id = NormalizeMemoryId(ReadString(item, "id") ?? title ?? "agent-memory");
        var existing = await _memories.GetAsync(id, cancellationToken);
        var record = new MemoryRecord
        {
            Id = id,
            Title = title ?? existing?.Title ?? id,
            Content = content,
            Status = ReadStatus(item, existing?.Status ?? MemoryStatus.Working),
            Confidence = ReadDouble(item, "confidence") ?? existing?.Confidence ?? 0.7,
            Tags = ReadStringArray(item, "tags", ["agent", "chat"]),
            References = existing?.References.ToList() ?? [],
            Conflicts = existing?.Conflicts.ToList() ?? [],
            SourceLinks = existing?.SourceLinks.ToList() ?? [],
            UsageCount = existing?.UsageCount ?? 0
        };

        if (existing is null)
        {
            await _memories.CreateAsync(record, cancellationToken);
        }
        else
        {
            await _memories.UpdateAsync(id, record, cancellationToken);
        }

        return id;
    }

    private async Task<string?> SavePageActionAsync(JsonObject item, CancellationToken cancellationToken)
    {
        var markdown = ReadString(item, "markdown") ?? ReadString(item, "content");
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var page = await _pages.SaveAsync(new PageSaveRequest(ReadString(item, "slug"), ReadString(item, "title"), markdown), cancellationToken);
        return page.Slug;
    }

    private static string StripJsonFence(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstLineEnd = trimmed.IndexOf('\n');
        if (firstLineEnd < 0)
        {
            return trimmed;
        }

        var withoutOpening = trimmed[(firstLineEnd + 1)..];
        var closing = withoutOpening.LastIndexOf("```", StringComparison.Ordinal);
        return closing >= 0 ? withoutOpening[..closing].Trim() : withoutOpening.Trim();
    }

    private static string NormalizeMemoryId(string value)
    {
        var id = SafeIdPattern.Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
    }

    [GeneratedRegex("[^A-Za-z0-9_-]+")]
    private static partial Regex SafeMemoryIdRegex();

    private static JsonNode? GetProperty(JsonObject item, string name)
    {
        foreach (var property in item)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string? ReadString(JsonObject item, string name) =>
        GetProperty(item, name) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double? ReadDouble(JsonObject item, string name) =>
        GetProperty(item, name) is JsonValue value && value.TryGetValue<double>(out var number) ? Math.Clamp(number, 0, 1) : null;

    private static List<string> ReadStringArray(JsonObject item, string name, IReadOnlyList<string> defaults)
    {
        if (GetProperty(item, name) is not JsonArray array)
        {
            return defaults.ToList();
        }

        return array.OfType<JsonValue>()
            .Select(value => value.TryGetValue<string>(out var text) ? text.Trim() : string.Empty)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static MemoryStatus ReadStatus(JsonObject item, MemoryStatus fallback) =>
        Enum.TryParse<MemoryStatus>(ReadString(item, "status"), ignoreCase: true, out var status) ? status : fallback;

    private static bool IsSupportedRole(string role) =>
        string.Equals(role, "system", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase);

    private sealed record AgentActionResult(string Reply, IReadOnlyList<string> WrittenMemories, IReadOnlyList<string> WrittenPages);
    private sealed record ToolLoopResult(ChatProviderResponse Response, IReadOnlyList<ChatMessage> Messages);
    private sealed record ChatToolCall(string Name, JsonObject Arguments);
    private sealed record ChatToolResult(string Name, string Content, bool IsError);
}