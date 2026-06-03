namespace MemorySmith.App.Services.AgentSessions;

using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MemorySmith.Core.Models;
using MemorySmith.App.Services.Training;

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
        "The sub-agent inference timed out on this turn. Retry the same message to continue; no state was changed.",
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

    // Both "not found" and "wrong principal" return the same message to avoid session enumeration.
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
///
/// Registered as <b>singleton</b>. Uses <see cref="IServiceScopeFactory"/> to resolve
/// scoped services (principally <see cref="IChatProvider"/>) per-invocation, avoiding
/// the captive-dependency antipattern.
/// </summary>
public sealed class AgentSessionService
{
    private static readonly JsonSerializerOptions ResultJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private static readonly HashSet<string> KnownProviders =
        new(StringComparer.OrdinalIgnoreCase) { "Ollama", "GitHubCopilot" };

    private readonly IAgentSessionStore _store;
    private readonly IGpuSlotScheduler _gpuSlots;
    private readonly ChatToolCatalog _toolCatalog;
    private readonly IOptions<MemorySmithOptions> _options;
    private readonly IAuthorizationService _authService;
    private readonly IServiceScopeFactory _scopeFactory;   // resolves IChatProvider per-invocation
    private readonly MemoryApplicationService _memories;
    private readonly IPageService _pages;
    private readonly ChatIntentInterceptor _intentInterceptor;
    private readonly ILogger<AgentSessionService> _logger;
    private readonly Training.IChatTranscriptWriter? _transcriptWriter; // optional, may not be registered

    public AgentSessionService(
        IAgentSessionStore store,
        IGpuSlotScheduler gpuSlots,
        ChatToolCatalog toolCatalog,
        IOptions<MemorySmithOptions> options,
        IAuthorizationService authService,
        IServiceScopeFactory scopeFactory,
        MemoryApplicationService memories,
        IPageService pages,
        ChatIntentInterceptor intentInterceptor,
        ILogger<AgentSessionService> logger,
        Training.IChatTranscriptWriter? transcriptWriter = null)
    {
        _store = store;
        _gpuSlots = gpuSlots;
        _toolCatalog = toolCatalog;
        _options = options;
        _authService = authService;
        _scopeFactory = scopeFactory;
        _memories = memories;
        _pages = pages;
        _intentInterceptor = intentInterceptor;
        _logger = logger;
        _transcriptWriter = transcriptWriter;

        // Startup guard: PersistSessions=true has no store implementation yet (Phase 2).
        if (_options.Value.AgentSession.PersistSessions)
        {
            throw new InvalidOperationException(
                "AgentSession:PersistSessions=true requires a SqliteAgentSessionStore which is not yet " +
                "implemented (Phase 2). Set AgentSession:PersistSessions=false or implement Phase 2 persistence.");
        }
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
        CancellationToken ct,
        string? systemPromptAddendum = null)
    {
        var principalId = RequirePrincipalId(caller);
        if (principalId is null)
            return CreateSessionResult.Fail(
                "Agent sessions require an authenticated caller with a NameIdentifier claim.");

        // Validate provider override against known registered providers.
        if (!string.IsNullOrEmpty(providerOverride) && !KnownProviders.Contains(providerOverride))
            return CreateSessionResult.Fail(
                $"Unknown provider '{providerOverride}'. Valid values: {string.Join(", ", KnownProviders)}.");

        // NOTE (Phase 2 — F13): model/provider strings bypass the ChatModelProfileService
        // AllowedRoles check. The design doc specifies that only model_profile_id (with
        // AllowedRoles enforcement) should be accepted. This is a known spec deviation
        // deferred to Phase 2, when ChatModelProfileService integration is added.
        // When resolved: replace modelOverride/providerOverride with a profile ID lookup
        // via ChatModelProfileService.GetByIdAsync(modelOverride, ct) and enforce AllowedRoles.

        // Validate scope=custom requires a non-empty allowed_tools list.
        if (string.Equals(requestedScope, "custom", StringComparison.OrdinalIgnoreCase) &&
            (customTools is null || customTools.Count == 0))
            return CreateSessionResult.Fail(
                "scope=custom requires a non-empty allowed_tools list. " +
                "Specify at least one tool name, or use a standard scope (read_only, standard, full).");

        var opts = _options.Value;
        var profile = MemorySmithSecurityProfiles.Normalize(opts.SecurityProfile);

        var cap = GetMaxConcurrentSessions(profile, opts);
        var activeCount = await _store.GetActiveCountForPrincipalAsync(principalId, ct);
        if (activeCount >= cap)
            return CreateSessionResult.TooManyRequests(
                $"Concurrent session limit ({cap}) reached for security profile '{profile}'. " +
                "Close existing sessions via memorysmith_agent_session_end to continue.");

        var effectiveToolNames = await ComputeEffectiveScopeAsync(
            requestedScope, customTools, caller, profile, opts, ct);

        // system_prompt_addendum gate:
        // - Requires CanEditMemorySmith
        // - Disabled in RemoteHardened mode (no-op regardless of permission)
        // - Stored on session; actual injection into model prompt deferred to Phase 3
        //   (requires MemoryChatRequest.SystemPromptAddendum addition to ChatServices.cs)
        string? effectiveAddendum = null;
        if (!string.IsNullOrWhiteSpace(systemPromptAddendum))
        {
            var canEdit = (await _authService.AuthorizeAsync(
                caller, null, MemorySmithPolicies.CanEditMemorySmith)).Succeeded;
            var isRemoteHardened = string.Equals(
                profile, MemorySmithSecurityProfiles.RemoteHardened, StringComparison.OrdinalIgnoreCase);

            if (canEdit && !isRemoteHardened)
            {
                effectiveAddendum = systemPromptAddendum.Length > 2000
                    ? systemPromptAddendum[..2000]
                    : systemPromptAddendum;
            }
            // else: silently ignored — no error to avoid leaking permission info
        }

        var session = new AgentSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            PrincipalId = principalId,
            RequestedScope = requestedScope,
            EffectiveToolNames = effectiveToolNames,
            ModelOverride = modelOverride,
            ProviderOverride = providerOverride,
            SystemPromptAddendum = effectiveAddendum,
            CreatedAt = DateTimeOffset.UtcNow,
            MaxTurns = Math.Clamp(maxTurns, 1, 50),
            TimeoutSeconds = Math.Clamp(timeoutSeconds, 10, 600),
            IdleTimeoutMinutes = GetIdleTimeoutMinutes(profile, opts),
            NestingDepth = 0,
            // TODO (Phase 3): enforce MaxNestingDepth ceiling here when internal delegation is enabled.
        };

        await _store.SaveAsync(session, ct);
        _logger.LogDebug(
            "Created agent session {SessionId} for principal {PrincipalId} scope={Scope} tools={ToolCount}",
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

        var principalId = RequirePrincipalId(caller);
        // Same error for wrong principal as not-found to prevent session enumeration.
        if (principalId is null ||
            !string.Equals(session.PrincipalId, principalId, StringComparison.Ordinal))
            return ResumeSessionResult.NotFound();

        if (session.Status is AgentSessionStatus.Expired or AgentSessionStatus.Closed)
            return ResumeSessionResult.NotFound();

        return ResumeSessionResult.Ok(session);
    }

    public async Task<AgentInvokeResult> InvokeAsync(
        AgentSession session,
        string message,
        CancellationToken ct)
    {
        // Acquire the per-session lock embedded in AgentSession.
        // This prevents concurrent invocations on the same session AND prevents
        // AgentSessionCleanupService from racing on status/history writes.
        await session.AcquireAsync(ct);
        try
        {
            // If the session was expired by the cleanup service between the caller's
            // ResumeSessionAsync check and this lock acquisition, return a clean error.
            if (session.Status is AgentSessionStatus.Expired or AgentSessionStatus.Closed)
            {
                return new AgentInvokeResult(
                    session.SessionId, session.TurnCount, string.Empty, [],
                    0, "session_expired", null);
            }

            return await InvokeCoreAsync(session, message, ct);
        }
        finally
        {
            session.Release();
        }
    }

    /// <summary>
    /// Force-closes a session on behalf of an admin caller, bypassing the principal check.
    /// Called from the /admin/sessions Blazor page. Auth is enforced at the page layer via
    /// <c>CanAdminMemorySmith</c> policy.
    /// </summary>
    public async Task AdminCloseSessionAsync(string sessionId, CancellationToken ct)
    {
        var session = await _store.GetAsync(sessionId, ct);
        if (session is null) return;

        await session.AcquireAsync(ct);
        try
        {
            if (session.Status is AgentSessionStatus.Closed or AgentSessionStatus.Expired)
                return;
            session.SetStatus(AgentSessionStatus.Closed);
            await _store.SaveAsync(session, ct);
            await _store.DeleteAsync(sessionId, ct);
        }
        finally
        {
            session.Release();
        }
        _logger.LogInformation("Agent session {SessionId} force-closed by admin.", sessionId[..8]);
    }

    public async Task<bool> EndSessionAsync(string sessionId, ClaimsPrincipal caller, CancellationToken ct)
    {
        var session = await _store.GetAsync(sessionId, ct);
        if (session is null) return false;

        var principalId = RequirePrincipalId(caller);
        if (principalId is null ||
            !string.Equals(session.PrincipalId, principalId, StringComparison.Ordinal))
            return false;

        await session.AcquireAsync(ct);
        try
        {
            // Double-check after acquiring the lock: if cleanup expired the session between
            // the GetAsync check above and lock acquisition, treat it as already gone.
            if (session.Status is AgentSessionStatus.Expired or AgentSessionStatus.Closed)
                return false;

            session.SetStatus(AgentSessionStatus.Closed);
            await _store.SaveAsync(session, ct);
            await _store.DeleteAsync(sessionId, ct);
        }
        finally
        {
            session.Release();
        }

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
        var opts = _options.Value;
        var maxHistoryTurns = opts.AgentSession.MaxHistoryTurns;

        // Build a filtered catalog containing only the session's allowed tools.
        var filteredTools = _toolCatalog.All
            .Where(t => t.AvailableInMcp && session.EffectiveToolNames.Contains(t.Name))
            .ToList();
        var filteredCatalog = new ChatToolCatalog(filteredTools);

        // Create a DI scope so we resolve IChatProvider correctly (scoped lifetime).
        using var scope = _scopeFactory.CreateScope();
        var providers = scope.ServiceProvider.GetServices<IChatProvider>().ToList();

        var agent = new MemoryChatAgent(
            providers,
            _memories,
            _pages,
            _options,
            currentUser: null,
            toolCatalog: filteredCatalog,
            intentInterceptor: _intentInterceptor);

        // Snapshot history under the lock (we already hold it).
        var historyCopy = session.History.Count > 0 ? [.. session.History] : (IReadOnlyList<ChatMessage>?)null;

        var chatRequest = new MemoryChatRequest(
            Message: message,
            Mode: MemoryChatMode.Chat,
            History: historyCopy,
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
            // Per-turn timeout — session stays alive, no state changed, caller may retry.
            await _store.SaveAsync(session, ct);
            return AgentInvokeResult.Timeout(session.SessionId, session.TurnCount);
        }

        // Update history and turn count (still under the session lock).
        session.AppendMessages(message, response.Reply);
        session.TrimHistoryToMaxTurns(maxHistoryTurns);
        session.IncrementTurn();

        var finishReason = "stop";
        if (session.TurnCount >= session.MaxTurns)
        {
            session.SetStatus(AgentSessionStatus.Closed);
            finishReason = "max_turns";
            await _store.DeleteAsync(session.SessionId, ct);
        }
        else
        {
            await _store.SaveAsync(session, ct);
        }

        // Write transcript entry with ModeIntent=sub_agent if transcript writer is registered.
        if (_transcriptWriter is not null)
        {
            _ = WriteTranscriptAsync(session, message, response, finishReason, ct);
        }

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

    // ── Transcript logging ────────────────────────────────────────────────────

    private async Task WriteTranscriptAsync(
        AgentSession session, string userMessage, MemoryChatResponse response,
        string finishReason, CancellationToken ct)
    {
        if (_transcriptWriter is null) return;
        try
        {
            var turnId = Guid.NewGuid().ToString("N");
            var record = new ChatTurnRecord
            {
                Id = turnId,
                Timestamp = DateTimeOffset.UtcNow,
                SessionId = session.SessionId,
                User = new TurnUser(session.PrincipalId, "sub-agent-caller"),
                Model = new TurnModel(response.Model, response.ProviderName),
                TemplateVersion = "sub-agent-v1",
                ModeIntent = "sub_agent",      // ← key: identifies sub-agent turns in transcript
                SystemPromptHash = ChatTranscriptWriter.Sha256Hex(session.SessionId),
                ParentSessionId = session.ParentSessionId,
                Request = new TurnRequest
                {
                    MessageHash = ChatTranscriptWriter.Sha256Hex(userMessage),
                    HistoryTurnCount = session.TurnCount - 1,
                },
                Execution = new TurnExecution
                {
                    IterationsUsed = 1,
                    PromptTokens = response.Usage?.PromptTokens,
                    CompletionTokens = response.Usage?.CompletionTokens,
                    TotalTokens = response.Usage?.TotalTokens,
                },
                Response = new TurnResponse
                {
                    FinishReason = finishReason,
                    ContentSha256 = ChatTranscriptWriter.Sha256Hex(response.Reply),
                    ContentBytes = System.Text.Encoding.UTF8.GetByteCount(response.Reply)
                }
            };
            var content = new ChatTurnContent
            {
                Id = turnId,
                UserMessage = userMessage,
                AssistantMessage = response.Reply
            };
            await _transcriptWriter.WriteAsync(record, content, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write sub-agent transcript entry for session {SessionId}.",
                session.SessionId[..8]);
        }
    }

    // ── Scope computation ─────────────────────────────────────────────────────

    private async Task<IReadOnlyList<string>> ComputeEffectiveScopeAsync(
        string requestedScope,
        IReadOnlyList<string>? customTools,
        ClaimsPrincipal caller,
        string securityProfile,
        MemorySmithOptions opts,
        CancellationToken ct)
    {
        var mcpOptions = opts.Mcp;
        var sessionOptions = opts.AgentSession;

        // Step 1+2: All MCP-available tools filtered by server enable/disable config.
        var enabledSet = _toolCatalog.McpTools
            .Where(t => IsMcpToolEnabled(t, mcpOptions))
            .ToList();

        // Step 3: Determine caller permissions.
        var canWrite = (await _authService.AuthorizeAsync(
            caller, null, MemorySmithPolicies.CanEditMemorySmith)).Succeeded;

        // SensitiveRead tools are currently absent from the master catalog.
        // AllowSensitiveRead=true will enable them for sub-agents if/when they are added.
        // A warning is logged when any SensitiveRead tool is excluded so developers know
        // the behavior without having to read this comment.
        var canReadSensitive = sessionOptions.AllowSensitiveRead &&
            (await _authService.AuthorizeAsync(
                caller, null, MemorySmithPolicies.CanReadSourceBundle)).Succeeded;

        var sensitiveReadExcluded = enabledSet
            .Where(t => t.Risk == ChatToolRisk.SensitiveRead && !canReadSensitive)
            .Select(t => t.Name)
            .ToList();
        if (sensitiveReadExcluded.Count > 0)
        {
            _logger.LogWarning(
                "SensitiveRead tools {ToolNames} excluded from sub-agent scope because " +
                "AgentSession:AllowSensitiveRead is false or caller lacks CanReadSourceBundle.",
                string.Join(", ", sensitiveReadExcluded));
        }

        // Filter by risk tier vs. caller's permissions.
        var callerSet = enabledSet.Where(t => t.Risk switch
        {
            ChatToolRisk.ReadOnly => true,
            ChatToolRisk.SensitiveRead => canReadSensitive,
            ChatToolRisk.Write => canWrite,
            _ => false
        }).ToList();

        // Step 4: Apply requested scope.
        IEnumerable<ChatToolDescriptor> scopedSet = requestedScope switch
        {
            "read_only" or "standard" => callerSet.Where(t => t.Risk == ChatToolRisk.ReadOnly),
            "full" => callerSet,
            "custom" when customTools is { Count: > 0 } =>
                callerSet.Where(t => customTools.Contains(t.Name, StringComparer.OrdinalIgnoreCase)),
            _ => callerSet.Where(t => t.Risk == ChatToolRisk.ReadOnly)
        };

        // Step 5: Apply SecurityProfile ceiling.
        scopedSet = securityProfile switch
        {
            MemorySmithSecurityProfiles.RemoteHardened =>
                scopedSet.Where(t => t.Risk == ChatToolRisk.ReadOnly),
            MemorySmithSecurityProfiles.SecureLocal =>
                scopedSet.Where(t => t.Risk != ChatToolRisk.Write),
            _ => scopedSet // LocalDev: no ceiling
        };

        // Step 6: Self-exclusion — sub-agents can never spawn or manage sessions.
        // TODO (Phase 3): When AvailableInAgent=true is enabled, this unconditional exclusion
        // is the primary anti-recursion guard alongside NestingDepth enforcement.
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

    /// <summary>
    /// Returns the caller's principal identifier, or null if the caller is unauthenticated
    /// or has no NameIdentifier claim. A null return causes session creation to be rejected —
    /// anonymous callers cannot create sessions to prevent namespace collision.
    /// </summary>
    private static string? RequirePrincipalId(ClaimsPrincipal caller) =>
        caller.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)
        ?? caller.Identity?.Name;

    private static int GetMaxConcurrentSessions(string profile, MemorySmithOptions opts)
    {
        // Operator config override takes precedence over profile defaults.
        if (opts.Mcp.MaxConcurrentSessionsPerUser is { } configured)
            return Math.Max(1, configured);

        return profile switch
        {
            MemorySmithSecurityProfiles.RemoteHardened => 1,
            MemorySmithSecurityProfiles.SecureLocal => 3,
            _ => 10 // LocalDev
        };
    }

    private static int GetIdleTimeoutMinutes(string profile, MemorySmithOptions opts)
    {
        // Operator config override takes precedence over profile defaults.
        if (opts.AgentSession.IdleTimeoutMinutes is { } configured)
            return Math.Max(1, configured);

        return profile switch
        {
            MemorySmithSecurityProfiles.RemoteHardened => 5,
            MemorySmithSecurityProfiles.SecureLocal => 10,
            _ => 30 // LocalDev
        };
    }
}
