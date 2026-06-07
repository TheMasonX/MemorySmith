namespace MemorySmith.App.Services.Training;

public sealed record ChatTurnRecord
{
    public required string Id { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string SessionId { get; init; }
    public required TurnUser User { get; init; }
    public required TurnModel Model { get; init; }
    public required string TemplateVersion { get; init; }
    public required string ModeIntent { get; init; }
    public required string SystemPromptHash { get; init; }
    public required TurnRequest Request { get; init; }
    public required TurnExecution Execution { get; init; }
    public required TurnResponse Response { get; init; }
    public bool RedactedContent { get; init; }
    public string? RedactionRule { get; init; }

    /// <summary>
    /// Session ID of the parent Athena session that spawned this sub-agent session.
    /// Null for all standard (non-delegation) turns.
    /// Set in Phase 3 when internal delegation via memorysmith_agent_invoke is enabled.
    /// </summary>
    public string? ParentSessionId { get; init; }
}

public sealed record TurnUser(string PrincipalId, string DisplayName);

public sealed record TurnModel(string Tag, string Provider);

public sealed record TurnRequest
{
    public required string MessageHash { get; init; }
    public required int HistoryTurnCount { get; init; }
    public List<string> PreloadedMemoryIds { get; init; } = [];
    public List<string> PreloadedPageSlugs { get; init; } = [];
    public List<string> AttachmentTypes { get; init; } = [];
}

public sealed record TurnExecution
{
    public List<TurnToolCall> ToolCalls { get; init; } = [];
    public int IterationsUsed { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public int? TotalTokens { get; init; }
    public int FirstTokenMs { get; init; }
    public int TotalMs { get; init; }
}

public sealed record TurnToolCall
{
    public required string Name { get; init; }
    public required string ArgumentsJson { get; init; }
    public int LatencyMs { get; init; }
    public bool Succeeded { get; init; } = true;
    public string? ErrorMessage { get; init; }
}

public sealed record TurnResponse
{
    public required string FinishReason { get; init; }
    public required string ContentSha256 { get; init; }
    public required int ContentBytes { get; init; }
}

public sealed record ChatTurnContent
{
    public required string Id { get; init; }
    public required string UserMessage { get; init; }
    public required string AssistantMessage { get; init; }
    public List<string>? SystemMessages { get; init; }
    public List<ChatTurnToolResult>? ToolResults { get; init; }
}

public sealed record ChatTurnToolResult(string ToolName, string ResultJson);
