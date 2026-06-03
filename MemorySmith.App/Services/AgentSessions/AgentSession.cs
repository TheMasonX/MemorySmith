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
/// <c>memorysmith_agent_invoke</c>. Session scope (tool list, model) is sealed at creation.
/// History accumulates through successive InvokeAsync calls.
///
/// The embedded <see cref="_lock"/> serializes all mutations and must be held by any caller
/// that reads or writes mutable state (<see cref="TurnCount"/>, <see cref="LastAccessedAt"/>,
/// <see cref="Status"/>, <see cref="_history"/>). Both <see cref="AgentSessionService"/> and
/// <see cref="AgentSessionCleanupService"/> acquire this lock before mutating.
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
    /// Session ID of the Athena session that spawned this sub-agent session (Phase 3 only).
    /// Null for all Phase 1-2 sessions created by external MCP callers.
    /// </summary>
    public string? ParentSessionId { get; init; }

    /// <summary>
    /// Nesting depth in the delegation chain (0 = external MCP caller, 1+ = Athena sub-agent).
    /// Always 0 in Phase 1-2 since AvailableInAgent is false.
    /// TODO (Phase 3): AgentSessionService.CreateSessionAsync must enforce MaxNestingDepth ceiling here.
    /// </summary>
    public int NestingDepth { get; init; }

    // ── Session lock — embedded to prevent lock-identity races ────────────────
    // Both AgentSessionService.InvokeAsync and AgentSessionCleanupService must hold this
    // before reading or writing any mutable field. Embedding it in the session ensures the
    // same lock object is used by all callers regardless of how they obtained the session reference.
    // Note: not serialized — recreated at deserialization for Phase 2 SQLite store.
    private readonly SemaphoreSlim _lock = new(1, 1);

    // ── Mutable state — read and write ONLY while _lock is held ──────────────
    public int TurnCount { get; private set; }
    public DateTimeOffset LastAccessedAt { get; private set; } = DateTimeOffset.UtcNow;
    public AgentSessionStatus Status { get; private set; } = AgentSessionStatus.Active;

    private readonly List<ChatMessage> _history = [];

    /// <summary>
    /// Conversation history for this session. Passed as History in each MemoryChatRequest.
    /// Read only while holding the session lock, or via a snapshot copy ([.. History]).
    /// </summary>
    public IReadOnlyList<ChatMessage> History => _history;

    // ── Lock helpers — called by AgentSessionService and AgentSessionCleanupService ──

    internal Task AcquireAsync(CancellationToken ct) => _lock.WaitAsync(ct);
    internal void Release() => _lock.Release();

    // ── Mutation methods — MUST be called while _lock is held ────────────────

    internal void AppendMessages(string userMessage, string assistantReply)
    {
        _history.Add(new ChatMessage("user", userMessage));
        _history.Add(new ChatMessage("assistant", assistantReply));
    }

    /// <summary>
    /// Trims oldest conversation turns if history exceeds <paramref name="maxTurns"/>.
    /// Called after AppendMessages to enforce AgentSessionOptions.MaxHistoryTurns.
    /// </summary>
    internal void TrimHistoryToMaxTurns(int maxTurns)
    {
        // Clamp to [1, 10000] to guard against misconfigured 0 or extreme values.
        var safeTurns = Math.Max(1, Math.Min(maxTurns, 10_000));
        var maxMessages = safeTurns * 2; // one user + one assistant message per turn
        if (_history.Count > maxMessages)
            _history.RemoveRange(0, _history.Count - maxMessages);
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
