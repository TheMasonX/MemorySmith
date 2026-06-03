namespace MemorySmith.App.Services.AgentSessions;

using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using MemorySmith.Core.Models;

// ── Result types ─────────────────────────────────────────────────────────────

public sealed record AgentInvokeResult(
    string SessionId,
    int Turn,
    string Message,
    IReadOnlyList<string> ContextIds,
    int ToolCallsMade,
    string FinishReason,
    ChatUsageSummary? Usage)
{
    public static AgentInvokeResult Timeout(string sessionId, int turn) => new(
        sessionId, turn,
        "The sub-agent inference timed out on this turn. Retry the same message to continue.",
        [], 0, "timeout", null);
}

public sealed class CreateSessionResult
{
    public bool Succeeded { get; private init; }
    public string? Error { get; private init; }
    public AgentSession? Session { get; private init; }

    public static CreateSessionResult Ok(AgentSession session) =>
        new() { Succeeded = true, Session = session };
    public static CreateSessionResult TooManyRequests(string error) =>
        new() { Succeeded = false, Error = error };
    public static CreateSessionResult Fail(string error) =>
        new() { Succeeded = false, Error = error };
}

public sealed class ResumeSessionResult
{
    public bool Succeeded { get; private init; }
    public string? Error { get; private init; }
    public AgentSession? Session { get; private init; }

    public static ResumeSessionResult Ok(AgentSession session) =>
        new() { Succeeded = true, Session = session };

    // Both "not found" and "wrong principal" return the same message to avoid enumeration.
    public static ResumeSessionResult NotFound() => new()
    {
        Succeeded = false,
        Error = "{\"finish_reason\":\"session_expired\",\"message\":\"Session not found or has expired. Start a new session by omitting session_id.\"}"
    };
}

// ── Service ───────────────────────────────────────────────────────────────────

/// <summary>
/// Orchestrates multi-turn sub-agent sessions created via <c>memorysmith_agent_invoke</c>.
/// Handles session lifecycle, scope intersection, GPU slot scheduling, and agent invocation.
/// </summary>
public sealed class AgentSessionService
{
    private static readonly JsonSerializerOptions ResultJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly IAgentSessionStore _store;
    private readonly IGpuSlotScheduler _gpuSlots;
    private readonly ChatToolCatalog _toolCatalog;
    private readonly IOptions<MemorySmithOptions> _options;
    private readonly IAuthorizationService _authService;
    private readonly IEnumerable<IChatProvider> _chatProviders;
    private readonly MemoryApplicationService _memories;
    private readonly IPageService _pages;
    private readonly ChatIntentInterceptor _intentInterceptor;
    private readonly ILogger<AgentSessionService> _logger;

    // Per-session semaphores prevent concurrent invocations on the same session.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks =
        new(StringComparer.Ordinal);

    public AgentSessionService(
        IAgentSessionStore store,
        IGpuSlotScheduler gpuSlots,
        ChatToolCatalog toolCatalog,
        IOptions<MemorySmithOptions> options,
        IAuthorizationService authService,
        IEnumerable<IChatProvider> chatProviders,
        MemoryApplicationService memories,
        IPageService pages,
        ChatIntentInterceptor intentInterceptor,
        ILogger<AgentSessionService> logger)
    {
        _store = store;
        _gpuSlots = gpuSlots;
        _toolCatalog = toolCatalog;
        _options = options;
        _authService = authService;
        _chatProviders = chatProviders;
        _memories = memories;
        _pages = pages;
        _intentInterceptor = intentInterceptor;
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<CreateSessionResult> CreateSessionAsync(
        string requestedScope,
        IReadOnlyList<string>? customTools,
        string? modelOverride,
        string? providerOverride,
        int maxTurns,
        int timeoutSeconds,
        ClaimsPrincipal caller,
        CancellationToken ct)
    {
        var profile = MemorySmithSecurityProfiles.Normalize(_options.Value.SecurityProfile);
        var principalId = GetPrincipalId(caller);

        var cap = GetMaxConcurrentSessions(profile);
        var activeCount = await _store.GetActiveCountForPrincipalAsync(principalId, ct);
        if (activeCount >= cap)
            return CreateSessionResult.TooManyRequests(
                $"Concurrent session limit ({cap}) reached for security profile '{profile}'. Close existing sessions to continue.");

        var effectiveToolNames = await ComputeEffectiveScopeAsync(
            requestedScope, customTools, caller, profile, ct);

        var session = new AgentSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            PrincipalId = principalId,
            RequestedScope = requestedScope,
            EffectiveToolNames = effectiveToolNames,
            ModelOverride = modelOverride,
            ProviderOverride = providerOverride,
            CreatedAt = DateTimeOffset.UtcNow,
            MaxTurns = Math.Clamp(maxTurns, 1, 50),
            TimeoutSeconds = Math.Clamp(timeoutSeconds, 10, 600),
            IdleTimeoutMinutes = GetIdleTimeoutMinutes(profile),
            NestingDepth = 0,
        };

        await _store.SaveAsync(session, ct);
        _logger.LogDebug(
            "Created agent session {SessionId} for principal {PrincipalId} with scope {Scope} ({ToolCount} tools)",
            session.SessionId[..8], principalId[..Math.Min(8, principalId.Length)],
            requestedScope, effectiveToolNames.Count);

        return CreateSessionResult.Ok(session);
    }

    public async Task<ResumeSessionResult> ResumeSessionAsync(
        string sessionId,
        ClaimsPrincipal caller,
        CancellationToken ct)
    {
        var session = await _store.GetAsync(sessionId, ct);
        if (session is null) return ResumeSessionResult.NotFound();

        var principalId = GetPrincipalId(caller);
        // Same error for wrong principal as not-found to prevent enumeration.
        if (!string.Equals(session.PrincipalId, principalId, StringComparison.Ordinal))
            return ResumeSessionResult.NotFound();

        if (session.Status is AgentSessionStatus.Expired or AgentSessionStatus.Closed)
            return ResumeSessionResult.NotFound();

        if (session.Status == AgentSessionStatus.Idle)
            session.SetStatus(AgentSessionStatus.Active);

        return ResumeSessionResult.Ok(session);
    }

    public async Task<AgentInvokeResult> InvokeAsync(
        AgentSession session,
        string message,
        CancellationToken ct)
    {
        var sessionLock = _sessionLocks.GetOrAdd(session.SessionId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct);
        try
        {
            return await InvokeCoreAsync(session, message, ct);
        }
        finally
        {
            sessionLock.Release();
            if (session.Status is AgentSessionStatus.Closed or AgentSessionStatus.Expired)
                _sessionLocks.TryRemove(session.SessionId, out _);
        }
    }

    public async Task<bool> EndSessionAsync(string sessionId, ClaimsPrincipal caller, CancellationToken ct)
    {
        var session = await _store.GetAsync(sessionId, ct);
        if (session is null) return false;

        var principalId = GetPrincipalId(caller);
        if (!string.Equals(session.PrincipalId, principalId, StringComparison.Ordinal))
            return false;

        session.SetStatus(AgentSessionStatus.Closed);
        await _store.SaveAsync(session, ct);
        _sessionLocks.TryRemove(sessionId, out _);
        _logger.LogDebug("Agent session {SessionId} closed explicitly.", sessionId[..8]);
        return true;
    }

    /// <summary>
    /// Serializes an <see cref="AgentInvokeResult"/> to the JSON string returned as the MCP tool result text.
    /// </summary>
    public static string SerializeResult(AgentInvokeResult result) =>
        JsonSerializer.Serialize(result, ResultJsonOptions);

    // ── Core invocation ───────────────────────────────────────────────────────

    private async Task<AgentInvokeResult> InvokeCoreAsync(
        AgentSession session, string message, CancellationToken ct)
    {
        // Build a filtered catalog containing only the session's allowed tools.
        var filteredTools = _toolCatalog.All
            .Where(t => t.AvailableInMcp && session.EffectiveToolNames.Contains(t.Name))
            .ToList();
        var filteredCatalog = new ChatToolCatalog(filteredTools);

        var agent = new MemoryChatAgent(
            _chatProviders,
            _memories,
            _pages,
            _options,
            currentUser: null,
            toolCatalog: filteredCatalog,
            intentInterceptor: _intentInterceptor);

        var chatRequest = new MemoryChatRequest(
            Message: message,
            Mode: MemoryChatMode.Chat,  // sub-agents use Chat mode (no write proposals)
            History: session.History.Count > 0 ? [.. session.History] : null,
            Model: session.ModelOverride,
            Provider: session.ProviderOverride,
            SessionId: session.SessionId);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(session.TimeoutSeconds));

        MemoryChatResponse response;
        await using var gpuSlot = await _gpuSlots.AcquireAsync(
            $"sub-agent:{session.SessionId[..8]}", timeoutCts.Token);
        try
        {
            response = await agent.SendAsync(chatRequest, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Per-turn timeout, not caller cancellation — session stays alive.
            await _store.SaveAsync(session, ct);
            return AgentInvokeResult.Timeout(session.SessionId, session.TurnCount);
        }

        // Update history and turn count.
        session.AppendMessages(message, response.Reply);
        session.IncrementTurn();

        var finishReason = "stop";
        if (session.TurnCount >= session.MaxTurns)
        {
            session.SetStatus(AgentSessionStatus.Closed);
            finishReason = "max_turns";
        }

        await _store.SaveAsync(session, ct);

        var contextIds = response.Context
            .Select(c => $"{c.Kind}:{c.Id}")
            .ToList();

        var toolCallsMade = response.Context
            .Count(c => string.Equals(c.Origin, ChatContextOrigins.Tool, StringComparison.Ordinal));

        return new AgentInvokeResult(
            SessionId: session.SessionId,
            Turn: session.TurnCount,
            Message: response.Reply,
            ContextIds: contextIds,
            ToolCallsMade: toolCallsMade,
            FinishReason: finishReason,
            Usage: response.Usage);
    }

    // ── Scope computation ─────────────────────────────────────────────────────

    private async Task<IReadOnlyList<string>> ComputeEffectiveScopeAsync(
        string requestedScope,
        IReadOnlyList<string>? customTools,
        ClaimsPrincipal caller,
        string securityProfile,
        CancellationToken ct)
    {
        var mcpOptions = _options.Value.Mcp;

        // 1. All MCP-available tools filtered by server enable/disable config.
        var enabledSet = _toolCatalog.McpTools
            .Where(t => IsMcpToolEnabled(t, mcpOptions))
            .ToList();

        // 2. Determine caller permissions.
        var canWrite = (await _authService.AuthorizeAsync(
            caller, null, MemorySmithPolicies.CanEditMemorySmith)).Succeeded;

        // 3. Filter by caller's risk-tier permissions.
        var callerSet = enabledSet.Where(t => t.Risk switch
        {
            ChatToolRisk.ReadOnly => true,
            ChatToolRisk.SensitiveRead => false, // not in current master catalog
            ChatToolRisk.Write => canWrite,
            _ => false
        }).ToList();

        // 4. Apply requested scope.
        IEnumerable<ChatToolDescriptor> scopedSet = requestedScope switch
        {
            "read_only" or "standard" => callerSet.Where(t => t.Risk == ChatToolRisk.ReadOnly),
            "full" => callerSet,
            "custom" when customTools is { Count: > 0 } =>
                callerSet.Where(t => customTools.Contains(t.Name, StringComparer.OrdinalIgnoreCase)),
            _ => callerSet.Where(t => t.Risk == ChatToolRisk.ReadOnly)
        };

        // 5. Apply SecurityProfile ceiling.
        scopedSet = securityProfile switch
        {
            MemorySmithSecurityProfiles.RemoteHardened =>
                scopedSet.Where(t => t.Risk == ChatToolRisk.ReadOnly),
            MemorySmithSecurityProfiles.SecureLocal =>
                scopedSet.Where(t => t.Risk != ChatToolRisk.Write),
            _ => scopedSet // LocalDev: no ceiling
        };

        // 6. Self-exclusion — sub-agents can never spawn or manage sessions.
        var selfExcluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "memorysmith_agent_invoke",
            "memorysmith_agent_session_end"
        };

        return scopedSet
            .Where(t => !selfExcluded.Contains(t.Name))
            .Select(t => t.Name)
            .ToList();
    }

    private static bool IsMcpToolEnabled(ChatToolDescriptor tool, McpOptions options)
    {
        if (options.DisabledTools.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
            return false;
        if (options.EnabledTools.Count > 0 &&
            !options.EnabledTools.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
            return false;
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string GetPrincipalId(ClaimsPrincipal caller) =>
        caller.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)
        ?? caller.Identity?.Name
        ?? "anonymous";

    private static int GetMaxConcurrentSessions(string profile) => profile switch
    {
        MemorySmithSecurityProfiles.RemoteHardened => 1,
        MemorySmithSecurityProfiles.SecureLocal => 3,
        _ => 10 // LocalDev
    };

    private static int GetIdleTimeoutMinutes(string profile) => profile switch
    {
        MemorySmithSecurityProfiles.RemoteHardened => 5,
        MemorySmithSecurityProfiles.SecureLocal => 10,
        _ => 30 // LocalDev
    };
}
