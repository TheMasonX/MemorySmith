namespace MemorySmith.App.Services.AgentSessions;

/// <summary>State of an agent session lifecycle.</summary>
public enum AgentSessionStatus
{
    /// <summary>Session is active and accepting new messages.</summary>
    Active,
    /// <summary>Session has not been used recently; will accept messages but cleanup is pending.</summary>
    Idle,
    /// <summary>Session has exceeded idle timeout and can no longer accept messages.</summary>
    Expired,
    /// <summary>Session was explicitly closed or reached MaxTurns.</summary>
    Closed
}

/// <summary>
/// Represents a persistent multi-turn sub-agent conversation session created by
/// <c>memorysmith_agent_invoke</c>. Session scope (tool list, model) is sealed at creation
/// and cannot change mid-session. History accumulates through successive InvokeAsync calls.
/// All mutation goes through the internal methods, which are only called under
/// AgentSessionService's per-session lock.
/// </summary>
public sealed class AgentSession
{
    // ── Identity ──────────────────────────────────────────────────────────────
    public required string SessionId { get; init; }
    public required string PrincipalId { get; init; }

    // ── Scope — sealed at creation, immutable ─────────────────────────────────
    public required string RequestedScope { get; init; }
    public required IReadOnlyList<string> EffectiveToolNames { get; init; }
    public string? ModelOverride { get; init; }
    public string? ProviderOverride { get; init; }

    // ── Timing constraints ────────────────────────────────────────────────────
    public required DateTimeOffset CreatedAt { get; init; }
    public required int MaxTurns { get; init; }
    public required int TimeoutSeconds { get; init; }
    public required int IdleTimeoutMinutes { get; init; }

    // ── Delegation chain — reserved for Phase 3 internal delegation ───────────
    /// <summary>
    /// Session ID of the Athena session that spawned this sub-agent session (Phase 3).
    /// Null for all Phase 1-2 sessions created by external MCP callers.
    /// </summary>
    public string? ParentSessionId { get; init; }

    /// <summary>
    /// Nesting depth in the delegation chain (0 = external MCP caller, 1 = Athena sub-agent).
    /// Always 0 in Phase 1-2 since AvailableInAgent is false.
    /// </summary>
    public int NestingDepth { get; init; }

    // ── Mutable state — mutated only via internal methods under AgentSessionService lock ──
    public int TurnCount { get; private set; }
    public DateTimeOffset LastAccessedAt { get; private set; } = DateTimeOffset.UtcNow;
    public AgentSessionStatus Status { get; private set; } = AgentSessionStatus.Active;

    private readonly List<ChatMessage> _history = [];

    /// <summary>Conversation history for this session. Passed as History in each MemoryChatRequest.</summary>
    public IReadOnlyList<ChatMessage> History => _history;

    // ── Internal mutation methods ─────────────────────────────────────────────

    internal void AppendMessages(string userMessage, string assistantReply)
    {
        _history.Add(new ChatMessage("user", userMessage));
        _history.Add(new ChatMessage("assistant", assistantReply));
    }

    internal void IncrementTurn()
    {
        TurnCount++;
        LastAccessedAt = DateTimeOffset.UtcNow;
    }

    internal void SetStatus(AgentSessionStatus status)
    {
        Status = status;
        LastAccessedAt = DateTimeOffset.UtcNow;
    }
}
