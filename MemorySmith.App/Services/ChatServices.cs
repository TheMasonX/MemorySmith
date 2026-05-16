using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MemorySmith.Core.Models;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Services;

public enum MemoryChatMode
{
    Chat,
    Agent
}

public sealed record ChatMessage(string Role, string Content);

public sealed record ChatAttachment(string Name, string ContentType, string Text, long Size);

public sealed record ChatProviderRequest(IReadOnlyList<ChatMessage> Messages, MemoryChatMode Mode, string? Model = null);

public sealed record ChatProviderResponse(string Content, string ProviderName, string Model);

public sealed record ChatModelSummary(string Name, DateTimeOffset? ModifiedAt = null, long? Size = null);

public sealed record ChatRuntimeConfiguration(
    string Provider,
    string Endpoint,
    string Model,
    IReadOnlyList<ChatModelSummary> Models,
    string? ModelsError = null);

public sealed record MemoryChatRequest(
    string Message,
    MemoryChatMode Mode = MemoryChatMode.Chat,
    IReadOnlyList<ChatMessage>? History = null,
    string? Model = null,
    IReadOnlyList<ChatAttachment>? Attachments = null);

public sealed record ChatContextItem(string Kind, string Id, string Title, string Snippet);

public sealed record MemoryChatResponse(
    string Reply,
    string ProviderName,
    string Model,
    IReadOnlyList<ChatContextItem> Context,
    IReadOnlyList<string> WrittenMemories,
    IReadOnlyList<string> WrittenPages);

public interface IChatProvider
{
    string Name { get; }
    Task<ChatProviderResponse> CompleteAsync(ChatProviderRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChatModelSummary>> ListModelsAsync(CancellationToken cancellationToken);
}

public interface IChatAgent
{
    Task<MemoryChatResponse> SendAsync(MemoryChatRequest request, CancellationToken cancellationToken);
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
        if (!string.Equals(chatOptions.Provider, "Ollama", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Configured chat provider '{chatOptions.Provider}' is not registered.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(chatOptions.RequestTimeoutSeconds, 5, 600)));

        var model = string.IsNullOrWhiteSpace(request.Model) ? chatOptions.OllamaModel : request.Model.Trim();
        var endpoint = new Uri(new Uri(chatOptions.OllamaEndpoint.TrimEnd('/') + "/"), "api/chat");
        var payload = new
        {
            model,
            stream = false,
            messages = request.Messages.Select(message => new { role = message.Role, content = message.Content }).ToArray()
        };

        using var response = await _httpClient.PostAsJsonAsync(endpoint, payload, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Ollama returned {(int)response.StatusCode}: {body}");
        }

        using var document = JsonDocument.Parse(body);
        var content = ReadOllamaContent(document.RootElement);
        return new ChatProviderResponse(content, Name, model);
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

    private static string ReadOllamaContent(JsonElement root)
    {
        if (root.TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.String)
        {
            return response.GetString() ?? string.Empty;
        }

        return root.ToString();
    }
}

public sealed class MemoryChatAgent : IChatAgent
{
    private static readonly Regex SafeIdPattern = new("[^A-Za-z0-9_-]+", RegexOptions.Compiled);

    private readonly IChatProvider _provider;
    private readonly MemoryApplicationService _memories;
    private readonly IPageService _pages;
    private readonly IOptions<MemorySmithOptions> _options;

    public MemoryChatAgent(
        IChatProvider provider,
        MemoryApplicationService memories,
        IPageService pages,
        IOptions<MemorySmithOptions> options)
    {
        _provider = provider;
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
        var providerResponse = await _provider.CompleteAsync(new ChatProviderRequest(messages, request.Mode, request.Model), cancellationToken);

        if (request.Mode == MemoryChatMode.Agent)
        {
            var agentResult = await TryApplyAgentActionsAsync(providerResponse.Content, cancellationToken);
            return new MemoryChatResponse(
                agentResult.Reply,
                providerResponse.ProviderName,
                providerResponse.Model,
                context,
                agentResult.WrittenMemories,
                agentResult.WrittenPages);
        }

        return new MemoryChatResponse(
            providerResponse.Content,
            providerResponse.ProviderName,
            providerResponse.Model,
            context,
            [],
            []);
    }

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

            formatted.Add($"Attachment: {attachment.Name} ({attachment.ContentType}, {attachment.Size} bytes)\n{text}".Trim());
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
        role is "system" or "user" or "assistant";

    private sealed record AgentActionResult(string Reply, IReadOnlyList<string> WrittenMemories, IReadOnlyList<string> WrittenPages);
}