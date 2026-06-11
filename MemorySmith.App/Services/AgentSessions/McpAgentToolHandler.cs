namespace MemorySmith.App.Services.AgentSessions;

using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

/// <summary>
/// Plain (text, isError) outcome of an MCP agent tool call. The MCP content envelope
/// (content array / isError / meta) is owned by McpController.ToolText — this type keeps the
/// handler transport-agnostic.
/// </summary>
public readonly record struct McpAgentToolOutcome(string Text, bool IsError = false);

/// <summary>
/// MCP-side handling for the two agent session tools (<c>memorysmith_agent_invoke</c> and
/// <c>memorysmith_agent_session_end</c>): governance checks, JSON schema listing, argument
/// parsing, permission checks, and dispatch into <see cref="AgentSessionService"/>.
///
/// Extracted from McpController so the controller stays a thin JSON-RPC router. The agent tools
/// are not ChatToolCatalog descriptors (they are MCP-only, Write-tier, and default-off), which
/// is why they need this bespoke path instead of DelegateToCatalogAsync.
/// </summary>
public sealed class McpAgentToolHandler
{
    public const string AgentInvokeToolName = "memorysmith_agent_invoke";
    public const string AgentSessionEndToolName = "memorysmith_agent_session_end";

    private readonly AgentSessionService _agentSessionService;
    private readonly IAuthorizationService _authorization;
    private readonly IOptionsMonitor<MemorySmithOptions> _options;

    public McpAgentToolHandler(
        AgentSessionService agentSessionService,
        IAuthorizationService authorization,
        IOptionsMonitor<MemorySmithOptions> options)
    {
        _agentSessionService = agentSessionService;
        _authorization = authorization;
        _options = options;
    }

    /// <summary>True when <paramref name="toolName"/> is one of the agent session tools.</summary>
    public static bool IsAgentTool(string? toolName) =>
        toolName is AgentInvokeToolName or AgentSessionEndToolName;

    /// <summary>
    /// Checks whether an agent session tool is enabled by the server's MCP governance
    /// configuration. These tools have Risk=Write and are disabled by default; they must be
    /// explicitly opted in via <c>MemorySmith:Mcp:EnabledTools</c>.
    ///
    /// Semantics (identical to IsMcpToolEnabled in AgentSessionService):
    /// - DisabledTools always wins.
    /// - EnabledTools is additive opt-in.
    /// - Default: disabled (because agent tools are Write-tier).
    /// </summary>
    public bool IsToolEnabled(string toolName)
    {
        var mcp = _options.CurrentValue.Mcp;
        if (mcp.DisabledTools.Contains(toolName, StringComparer.OrdinalIgnoreCase))
            return false;
        if (mcp.EnabledTools.Contains(toolName, StringComparer.OrdinalIgnoreCase))
            return true;
        // Agent tools are Write-tier and default-off (EnabledByDefaultInMcp=false)
        return false;
    }

    /// <summary>
    /// The tools/list entries for whichever agent session tools are currently enabled.
    /// Returns zero, one, or two entries depending on MemorySmith:Mcp:EnabledTools.
    /// </summary>
    public IEnumerable<(string Name, string Description, JsonObject InputSchema)> GetEnabledToolListEntries()
    {
        if (IsToolEnabled(AgentInvokeToolName))
        {
            yield return (
                AgentInvokeToolName,
                "Invoke the MemorySmith chat agent as a scoped sub-agent with its own managed context window. On the first call (no session_id), a new multi-turn session is created and the session_id is returned. Include that session_id in subsequent calls to continue the conversation.",
                BuildAgentInvokeSchema());
        }

        if (IsToolEnabled(AgentSessionEndToolName))
        {
            yield return (
                AgentSessionEndToolName,
                "Explicitly close an agent session created by memorysmith_agent_invoke. Frees resources immediately rather than waiting for idle timeout.",
                BuildAgentSessionEndSchema());
        }
    }

    public async Task<McpAgentToolOutcome> HandleAgentInvokeAsync(
        JsonElement argumentsElement, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        // Require CanEditMemorySmith — agent sessions have Write-tier side effects.
        if (!await CanEditMemorySmithAsync(user))
            return new("The caller is not authorized to invoke the agent (requires edit permission).", IsError: true);

        var message = GetString(argumentsElement, "message");
        if (string.IsNullOrWhiteSpace(message))
            return new("The memorysmith_agent_invoke tool requires a 'message' argument.", IsError: true);

        var sessionId = GetString(argumentsElement, "session_id");
        var requestedScope = GetString(argumentsElement, "scope") ?? "standard";
        var maxTurns = GetInt(argumentsElement, "max_turns", 10);
        var timeoutSeconds = GetInt(argumentsElement, "timeout_seconds", 120);
        var modelOverride = GetString(argumentsElement, "model");
        var providerOverride = GetString(argumentsElement, "provider");
        var systemPromptAddendum = GetString(argumentsElement, "system_prompt_addendum");

        List<string>? customTools = null;
        if (TryGetProperty(argumentsElement, "allowed_tools", out var toolsElement) &&
            toolsElement.ValueKind == JsonValueKind.Array)
        {
            customTools = toolsElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToList();
        }

        AgentSession session;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            var createResult = await _agentSessionService.CreateSessionAsync(
                requestedScope, customTools, modelOverride, providerOverride,
                maxTurns, timeoutSeconds, user, cancellationToken,
                systemPromptAddendum: systemPromptAddendum);
            if (!createResult.Succeeded)
                return new(createResult.Error!, IsError: true);
            session = createResult.Session!;
        }
        else
        {
            var resumeResult = await _agentSessionService.ResumeSessionAsync(
                sessionId, user, cancellationToken);
            if (!resumeResult.Succeeded)
                return new(resumeResult.Error!, IsError: true);
            session = resumeResult.Session!;
        }

        var invokeResult = await _agentSessionService.InvokeAsync(session, message, cancellationToken);
        return new(AgentSessionService.SerializeResult(invokeResult));
    }

    public async Task<McpAgentToolOutcome> HandleAgentSessionEndAsync(
        JsonElement argumentsElement, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (!await CanEditMemorySmithAsync(user))
            return new("The caller is not authorized to end agent sessions.", IsError: true);

        var sessionId = GetString(argumentsElement, "session_id");
        if (string.IsNullOrWhiteSpace(sessionId))
            return new("The memorysmith_agent_session_end tool requires a 'session_id' argument.", IsError: true);

        var ended = await _agentSessionService.EndSessionAsync(sessionId, user, cancellationToken);
        // Return IsError: true on not-found/already-closed so MCP callers can distinguish
        // success from failure programmatically (same error-signal convention as HandleAgentInvokeAsync).
        return new(
            ended
                ? $"{{\"closed\":true,\"session_id\":\"{sessionId}\"}}"
                : "{\"finish_reason\":\"session_expired\",\"message\":\"Session not found or already closed.\"}",
            IsError: !ended);
    }

    // ── Schemas ───────────────────────────────────────────────────────────────

    public static JsonObject BuildAgentInvokeSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray { "message" },
        ["properties"] = new JsonObject
        {
            ["message"] = new JsonObject { ["type"] = "string", ["description"] = "The task or question for the sub-agent. Be specific — the sub-agent will run tool calls to answer it." },
            ["session_id"] = new JsonObject { ["type"] = "string", ["description"] = "Session ID returned from a prior call. Omit to start a new session." },
            ["scope"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray { "read_only", "standard", "full", "custom" },
                ["default"] = "standard",
                ["description"] = "Tool access scope. read_only/standard: search and fetch tools only (read-only). full: all agent-chat-mode tools the caller has permission to use (note: MCP-only write tools such as page_save are not included, as the sub-agent runs in chat mode). custom: specify exact tool names via allowed_tools."
            },
            ["allowed_tools"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Required when scope=custom. List of memorysmith_* tool names to enable."
            },
            ["max_turns"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 50, ["default"] = 10 },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["minimum"] = 10, ["maximum"] = 600, ["default"] = 120 },
            ["model"] = new JsonObject { ["type"] = "string", ["description"] = "Optional Ollama model tag override (e.g. 'qwen3.5:4b')." },
            ["provider"] = new JsonObject { ["type"] = "string", ["description"] = "Optional provider override. Currently only 'Ollama' is supported. Other values are rejected at session creation time — enabling additional providers requires both registering them as IChatProvider in DI and adding them to the server-side provider allowlist (AgentSessionService.KnownProviders)." },
            ["system_prompt_addendum"] = new JsonObject
            {
                ["type"] = "string",
                ["maxLength"] = 2000,
                ["description"] = "Optional extra instructions appended to the sub-agent's system context. Requires CanEditMemorySmith role. No-op in remote-hardened mode. Note: stored on session; injection into model prompt is a Phase 3 feature."
            }
        }
    };

    public static JsonObject BuildAgentSessionEndSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray { "session_id" },
        ["properties"] = new JsonObject
        {
            ["session_id"] = new JsonObject { ["type"] = "string", ["description"] = "Session ID to close." }
        }
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<bool> CanEditMemorySmithAsync(ClaimsPrincipal user) =>
        (await _authorization.AuthorizeAsync(user, null, MemorySmithPolicies.CanEditMemorySmith)).Succeeded;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static int GetInt(JsonElement element, string name, int defaultValue)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return defaultValue;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed)
            ? parsed
            : defaultValue;
    }
}
