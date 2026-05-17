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

public sealed record ChatProviderResponse(string Content, string ProviderName, string Model, string? Thinking = null);

public sealed record ChatProviderChunk(
    string ContentDelta,
    string? ThinkingDelta,
    string? FinalContent,
    string? FinalThinking,
    bool IsFinal,
    string ProviderName,
    string Model,
    string? Status = null);

public sealed record ChatModelSummary(
    string Name,
    DateTimeOffset? ModifiedAt = null,
    long? Size = null,
    string? Provider = null,
    double? ChatMultiplier = null,
    bool IsPreferred = false,
    string? Description = null);

public sealed record ChatRuntimeConfiguration(
    string Provider,
    string Endpoint,
    string Model,
    IReadOnlyList<ChatModelSummary> Models,
    IReadOnlyList<string> Providers,
    string? ModelsError = null);

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
    IReadOnlyList<string> WrittenPages);

public sealed record MemoryChatStreamUpdate(
    string ContentDelta = "",
    string? ThinkingDelta = null,
    bool IsFinal = false,
    MemoryChatResponse? Response = null,
    IReadOnlyList<ChatContextItem>? Context = null,
    string? Status = null);

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

public sealed class OllamaChatProvider : IChatProvider
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
        return new ChatProviderResponse(content, Name, model, thinking);
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
                emittedFinal = true;
                yield return new ChatProviderChunk(string.Empty, null, visible, thinking, IsFinal: true, Name, model);
            }
        }

        if (!emittedFinal && content.Length > 0)
        {
            var (visible, thinking) = SplitThinking(content.ToString(), finalThinking);
            yield return new ChatProviderChunk(string.Empty, null, visible, thinking, IsFinal: true, Name, model);
        }
    }

    private static IReadOnlyList<OllamaMessage> BuildOllamaMessages(ChatProviderRequest request)
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
        var match = Regex.Match(content, @"<think>(.*?)</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
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
            }
        }

        return new ChatProviderResponse(
            finalContent ?? content.ToString(),
            Name,
            string.IsNullOrWhiteSpace(model) ? ResolveModel(request, _options.CurrentValue.Chat) : model,
            finalThinking ?? (thinking.Length == 0 ? null : thinking.ToString()));
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

        await using var client = CreateClient(chatOptions);
        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = model,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Streaming = true,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false }
        });

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
                    case SessionIdleEvent:
                        channel.Writer.TryWrite(new ChatProviderChunk(
                            string.Empty,
                            null,
                            finalContent ?? content.ToString(),
                            finalThinking ?? (thinking.Length == 0 ? null : thinking.ToString()),
                            IsFinal: true,
                            Name,
                            model));
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
        });

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
            var models = await client.ListModelsAsync();
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
            ?? ReadStringProperty(model, "Name")
            ?? ReadStringProperty(model, "Model")
            ?? string.Empty;
        var multiplier = ReadDoubleProperty(model, "ChatMultiplier")
            ?? ReadDoubleProperty(model, "PremiumMultiplier")
            ?? ReadDoubleProperty(model, "PremiumRequestMultiplier")
            ?? ReadDoubleProperty(model, "Multiplier");
        var description = ReadStringProperty(model, "Description") ?? ReadStringProperty(model, "Family");
        return new ChatModelSummary(name, Provider: "GitHub", ChatMultiplier: multiplier, Description: description);
    }

    private static IReadOnlyList<ChatModelSummary> MergeConfiguredModels(IEnumerable<ChatModelSummary> discovered, IReadOnlyList<ChatModelOption> configured)
    {
        var configuredByName = configured
            .Where(model => !string.IsNullOrWhiteSpace(model.Name))
            .GroupBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var merged = new Dictionary<string, ChatModelSummary>(StringComparer.OrdinalIgnoreCase);

        foreach (var model in discovered)
        {
            configuredByName.TryGetValue(model.Name, out var configuredModel);
            merged[model.Name] = model with
            {
                Provider = "GitHub",
                ChatMultiplier = model.ChatMultiplier ?? configuredModel?.ChatMultiplier,
                IsPreferred = configuredModel?.IsPreferred ?? false,
                Description = model.Description ?? configuredModel?.Description
            };
        }

        foreach (var model in configuredByName.Values)
        {
            if (!merged.ContainsKey(model.Name))
            {
                merged[model.Name] = new ChatModelSummary(
                    model.Name,
                    Provider: "GitHub",
                    ChatMultiplier: model.ChatMultiplier,
                    IsPreferred: model.IsPreferred,
                    Description: model.Description);
            }
        }

        return merged.Values
            .OrderByDescending(model => model.IsPreferred)
            .ThenBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ReadStringProperty(object instance, string propertyName) =>
        instance.GetType().GetProperty(propertyName)?.GetValue(instance)?.ToString();

    private static double? ReadDoubleProperty(object instance, string propertyName)
    {
        var value = instance.GetType().GetProperty(propertyName)?.GetValue(instance);
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
}

public sealed class MemoryChatAgent : IChatAgent
{
    private static readonly Regex SafeIdPattern = new("[^A-Za-z0-9_-]+", RegexOptions.Compiled);

    private readonly IReadOnlyList<IChatProvider> _providers;
    private readonly MemoryApplicationService _memories;
    private readonly IPageService _pages;
    private readonly IOptions<MemorySmithOptions> _options;

    public MemoryChatAgent(
        IEnumerable<IChatProvider> providers,
        MemoryApplicationService memories,
        IPageService pages,
        IOptions<MemorySmithOptions> options)
    {
        _providers = providers.ToList();
        if (_providers.Count == 0)
        {
            throw new InvalidOperationException("At least one chat provider must be registered.");
        }

        _memories = memories;
        _pages = pages;
        _options = options;
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
        var providerResponse = await provider.CompleteAsync(new ChatProviderRequest(messages, request.Mode, request.Model, request.Attachments, provider.Name), cancellationToken);

        return await BuildResponseAsync(request.Mode, providerResponse, context, cancellationToken);
    }

    public async IAsyncEnumerable<MemoryChatStreamUpdate> StreamAsync(MemoryChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            throw new ArgumentException("Message is required.", nameof(request));
        }

        var context = await BuildContextAsync(request.Message, cancellationToken);
        yield return new MemoryChatStreamUpdate(Context: context, Status: $"Loaded {context.Count} local resource(s)");

        var messages = BuildMessages(request, context);
        var provider = ResolveProvider(request.Provider);
        var content = new StringBuilder();
        var thinking = new StringBuilder();
        ChatProviderChunk? finalChunk = null;

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

            if (chunk.IsFinal)
            {
                finalChunk = chunk;
                break;
            }

            yield return new MemoryChatStreamUpdate(chunk.ContentDelta, chunk.ThinkingDelta, Status: chunk.Status);
        }

        var providerResponse = new ChatProviderResponse(
            finalChunk?.FinalContent ?? content.ToString(),
            finalChunk?.ProviderName ?? provider.Name,
            finalChunk?.Model ?? request.Model ?? DefaultModelForProvider(provider.Name),
            finalChunk?.FinalThinking ?? (thinking.Length == 0 ? null : thinking.ToString()));

        var response = await BuildResponseAsync(request.Mode, providerResponse, context, cancellationToken);
        yield return new MemoryChatStreamUpdate(IsFinal: true, Response: response, Context: context);
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
                agentResult.WrittenPages);
        }

        return new MemoryChatResponse(
            providerResponse.Content,
            providerResponse.ProviderName,
            providerResponse.Model,
            providerResponse.Thinking,
            context,
            [],
            []);
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

    private async Task<IReadOnlyList<ChatContextItem>> BuildContextAsync(string query, CancellationToken cancellationToken)
    {
        var options = _options.Value.Chat;
        var context = new List<ChatContextItem>();

        var memories = await _memories.HybridSearchAsync(new HybridMemorySearchQuery(query, Limit: Math.Clamp(options.MaxContextRecords, 0, 20)), cancellationToken);
        context.AddRange(memories.Select(memory => new ChatContextItem("memory", memory.Id, memory.Title, memory.Snippet)));

        var pages = await _pages.SearchAsync(new PageSearchQuery(query, Math.Clamp(options.MaxContextPages, 0, 20)), cancellationToken);
        context.AddRange(pages.Select(page => new ChatContextItem("page", page.Slug, page.Title, page.Snippet)));

        return context;
    }

    private IReadOnlyList<ChatMessage> BuildMessages(MemoryChatRequest request, IReadOnlyList<ChatContextItem> context)
    {
        var options = _options.Value.Chat;
        var messages = new List<ChatMessage>
        {
            new("system", BuildSystemPrompt(request.Mode)),
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

    private string BuildSystemPrompt(MemoryChatMode mode)
    {
        var configuredPrompt = ReadConfiguredSystemPrompt();
        if (!string.IsNullOrWhiteSpace(configuredPrompt))
        {
            return configuredPrompt + $"\n\nCurrent mode: {mode}.";
        }

        return mode == MemoryChatMode.Agent
            ? "You are MemorySmith Agent. Answer the user and, only when useful, propose memoryWrites and pageWrites. Return strict JSON with keys reply, memoryWrites, and pageWrites. memoryWrites items may include id, title, content, tags, status, confidence. pageWrites items may include slug, title, markdown. Do not include markdown fences around the JSON."
            : "You are MemorySmith Chat. Answer the user's question using the supplied memories and pages when useful. Be direct when the local knowledge base does not contain enough evidence.";
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

        return "Local MemorySmith context:\n" + string.Join("\n\n", context.Select(item =>
            $"[{item.Kind}] {item.Id} - {item.Title}\n{item.Snippet}"));
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

    private static string? ReadString(JsonObject item, string name) =>
        item[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double? ReadDouble(JsonObject item, string name) =>
        item[name] is JsonValue value && value.TryGetValue<double>(out var number) ? Math.Clamp(number, 0, 1) : null;

    private static List<string> ReadStringArray(JsonObject item, string name, IReadOnlyList<string> defaults)
    {
        if (item[name] is not JsonArray array)
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
}