# Agent-as-MCP-Tool: `memorysmith_agent_invoke`
## Design Document — MemorySmith

**Date:** 2026-06-01
**Branch reference:** master @ `b69542fc` (feature/code-search-high-roi-batch8 not found on remote; master used throughout — see Assumptions §15)
**Status:** Proposed — awaiting implementation approval
**Relation to prior work:** Complements Sub-Agent Architecture design doc (2026-05-31, `[[FILE_4fnzo8t5]]`); implements the GPU scheduling and sub-agent invocation primitives that Sprint G/H proposed but never shipped.

---

## Table of Contents

1. Overview and Motivation
2. Architecture Overview
3. Multi-Turn Session Model
4. MCP Tool Schema
5. Scope and Auth Intersection System
6. Model Selection Wiring
7. GPU Slot Scheduling
8. Anti-Recursion Design
9. `MemoryChatRequest` Extension
10. Security Analysis
11. Logging and Transcript Integration
12. Implementation Roadmap
13. Open Questions
14. Cross-References
15. Assumptions

---

## 1. Overview and Motivation

### 1.1 The Gap

MemorySmith's MCP server currently exposes 24 individual tools to external callers. Each tool does exactly one thing: search memories, fetch a page, list tasks. When a calling agent — Claude, Cursor, or an internal Athena session — needs to **research** something (search → read → cross-reference → synthesize), it must drive that multi-hop loop itself, managing context accumulation, intermediate results, and loop termination entirely on its own side of the wire.

This works, but has real costs:

1. **Context pollution**: every intermediate `memorysmith_search` result inflates the calling agent's context window, often permanently, competing with the caller's own reasoning.
2. **Reasoning asymmetry**: the calling agent is optimized for its own domain. It uses MemorySmith tools as raw instruments but lacks the domain preloads (wiki context, memory tiering, Athena's system prompt) that make MemorySmith's agent genuinely good at knowledge-base work.
3. **No internal delegation path**: if Athena wants to hand off a sub-task to a scoped, purpose-built agent, there is no mechanism. The Sub-Agent Architecture design doc (2026-05-31) proposed `VramScheduler`, `SubAgentRunner`, and `IAgentOrchestrator`, but **none of these classes exist in the codebase** (confirmed: GitHub search across all branches, zero results).

### 1.2 The Solution

Expose a single new MCP tool — `memorysmith_agent_invoke` — that wraps the full `MemoryChatAgent` loop behind a clean task-in/answer-out interface. The caller provides a message (and optionally an existing session ID for multi-turn continuation). MemorySmith's internal agent runs the complete search-read-synthesize loop in **its own context window** and returns a synthesized answer.

This serves two callers simultaneously:

| Caller | Transport path | Primary use case |
|--------|---------------|-----------------|
| External MCP clients (Claude, Cursor, etc.) | HTTP POST `/mcp` → `McpController` | "Search the wiki for everything about project-x and give me a structured summary" — without coupling to 24 individual tools |
| Internal Athena sessions (agent-mode) | Direct `AgentSessionService` injection | "Delegate this sub-task to a scoped read-only agent, then continue with its synthesis" |

The tool is dual-use by design: both callers route through the same `AgentSessionService`. The distinction is only in how `ChatToolExecutionContext` is constructed (HTTP auth claims vs. the calling Athena's `ICurrentUserContext`) and whether GPU yielding is needed (§7.4).

### 1.3 Design Constraints (Ordered by Precedence)

1. **Single-GPU serial execution**: RTX 5060 8 GB. Two simultaneous Ollama inference sessions can OOM or cause severe KV-cache eviction. Default must be serial. A new `IGpuSlotScheduler` service enforces this.
2. **Auth passthrough without escalation**: the sub-agent inherits the caller's permissions. Scope intersection (§5) is a downgrade-only operation — it can never produce a tool set broader than what the caller could exercise directly.
3. **SecurityProfile-driven defaults**: `LocalDev` / `SecureLocal` / `RemoteHardened` profiles cap the maximum scope available to any session.
4. **Existing tool governance respected exactly**: `IsMcpToolEnabled()`, risk-tier checks, `DisabledTools`/`EnabledTools` configuration — all apply to the sub-agent's catalog the same way they apply to direct MCP calls.
5. **No new JS interop**: this is a pure backend feature. Any admin UI surfaces use Blazor only, consistent with Audit #5's clipboard-paste finding.

---

## 2. Architecture Overview

```
┌──────────────────────────────────────────────────────────────────────────────────┐
│  EXTERNAL PATH                                                                   │
│                                                                                  │
│  Claude / Cursor / any MCP client                                                │
│       │ HTTP POST /mcp                                                           │
│       ▼                                                                          │
│  McpController.Post()                                                            │
│       │ tools/call → memorysmith_agent_invoke                                   │
│       │ Risk = Write → requires CanEditMemorySmith                              │
│       │ Build ChatToolExecutionContext(NestingDepth=0)                          │
│       ▼                                                                          │
│  AgentInvokeTool.Execute(args, ctx, ct)                                         │
│       │                                                                          │
│       ▼                                                                          │
│  AgentSessionService                          ┌─────────────────┐               │
│  ┌──────────────────────────────────────┐     │ IAgentSession   │               │
│  │ CreateOrResumeSession()              │◄────│ Store           │               │
│  │   compute effective scope            │     │ (InMemory / SQL)│               │
│  │   enforce SecurityProfile cap        │     └─────────────────┘               │
│  │   seal EffectiveToolNames            │                                        │
│  │                                      │     ┌─────────────────┐               │
│  │ InvokeAsync(session, message, ctx)   │     │ IGpuSlot        │               │
│  │   acquire GPU slot ─────────────────────►  │ Scheduler       │               │
│  │   build MemoryChatRequest            │     │ SemaphoreSlim   │               │
│  │     (History, ToolFilter, SessionId) │◄────│ (default: 1,1)  │               │
│  │   IChatAgent.SendAsync(request)      │     └─────────────────┘               │
│  │   release GPU slot                   │                                        │
│  │   append to session history          │                                        │
│  │   write ChatTurnRecord               │                                        │
│  │   return AgentInvokeResult           │                                        │
│  └──────────────────────────────────────┘                                        │
│       │                                                                          │
│       ▼                                                                          │
│  MemoryChatAgent.SendAsync(request)                                              │
│       │ applies request.ToolFilter to tool list presented to Ollama             │
│       │ memorysmith_agent_invoke EXCLUDED from sub-agent's catalog              │
│       │ tool loop: search / get / synthesize                                    │
│       ▼                                                                          │
│  OllamaChatProvider (GPU slot already held)                                      │
│       ▼ Ollama HTTP /api/chat                                                    │
└──────────────────────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────────────────────┐
│  INTERNAL PATH (Athena → sub-agent delegation, Phase 3)                         │
│                                                                                  │
│  MemoryChatAgent [Athena, main session, holds GPU slot]                         │
│       │ tool call → memorysmith_agent_invoke                                    │
│       │ (only when NestingDepth == 0 and AvailableInAgent == true)              │
│       ▼                                                                          │
│  AgentInvokeTool.Execute()                                                       │
│       │ YIELD: ctx.InheritedGpuSlot.DisposeAsync()  ← release Athena's slot    │
│       ▼                                                                          │
│  AgentSessionService.InvokeAsync() [NestingDepth=1]                             │
│       │ acquire GPU slot independently for sub-agent                            │
│       │ ... sub-agent runs ...                                                  │
│       │ release sub-agent GPU slot                                              │
│       │ return result to Athena tool loop                                       │
│       ▼                                                                          │
│  Athena re-acquires GPU slot for next provider call                             │
└──────────────────────────────────────────────────────────────────────────────────┘
```

The critical difference between paths is **GPU slot yielding** in the internal case (§7.4). Without it, Athena holds the GPU semaphore, the sub-agent blocks waiting for it, and Athena blocks waiting for the sub-agent — deadlock.

---

## 3. Multi-Turn Session Model

### 3.1 Session Entity

```csharp
// MemorySmith.App/Services/AgentSessions/AgentSession.cs

public enum AgentSessionStatus { Active, Idle, Expired, Closed }

public sealed class AgentSession
{
    // Identity
    public required string SessionId { get; init; }          // UUID v4 ("N" format, 32 hex chars)
    public required string PrincipalId { get; init; }        // ClaimTypes.NameIdentifier

    // Scope — sealed at creation, never mutated post-creation
    public required string RequestedScope { get; init; }     // "read_only" | "standard" | "full" | "custom"
    public required IReadOnlyList<string> EffectiveToolNames { get; init; }
    public required string ModelProfileId { get; init; }     // resolved ChatModelProfileView.Id

    // Timing
    public required DateTimeOffset CreatedAt { get; init; }
    public required int MaxTurns { get; init; }              // default 10, max 50
    public required int TimeoutSeconds { get; init; }        // per-turn Ollama timeout
    public required int IdleTimeoutMinutes { get; init; }    // session idle expiry

    // Mutable state (mutated only via AgentSessionService under lock)
    public int TurnCount { get; private set; }
    public DateTimeOffset LastAccessedAt { get; private set; } = DateTimeOffset.UtcNow;
    public AgentSessionStatus Status { get; private set; } = AgentSessionStatus.Active;

    // History — mutable, thread-safe via AgentSessionService lock
    private readonly List<ChatMessage> _history = [];
    public IReadOnlyList<ChatMessage> History => _history;

    // Delegation chain (Phase 3 — internal path)
    public string? ParentSessionId { get; init; }
    public int NestingDepth { get; init; }                   // 0 for external, 1+ for internal delegation

    // Internal mutation methods — called only by AgentSessionService
    internal void AppendMessages(string userMessage, string assistantReply)
    {
        _history.Add(new ChatMessage("user", userMessage));
        _history.Add(new ChatMessage("assistant", assistantReply));
    }
    internal void IncrementTurn() { TurnCount++; LastAccessedAt = DateTimeOffset.UtcNow; }
    internal void SetStatus(AgentSessionStatus s) { Status = s; LastAccessedAt = DateTimeOffset.UtcNow; }
}
```

`AgentSession` is the only class that mutates — and only via `internal` methods called by `AgentSessionService` under a per-session lock. The `EffectiveToolNames` list is computed once at `CreateSessionAsync()` time and is immutable for the session's lifetime.

### 3.2 Session State Machine

```
                CreateSessionAsync()
                       │
                       ▼
            ┌─────── Active ───────┐
            │                      │
    InvokeAsync()           IdleTimeout exceeded
    (turn < max)            (cleanup service)
            │                      │
            │                      ▼
            │                   Idle ──────── SecondIdleTimeout ──► Expired
            │                      ▲               │
            │                      │               ▼
            │              InvokeAsync()      [No further
            │              (resumption)        transitions]
            │
    TurnCount >= MaxTurns ──► Closed   (max turns reached)
    EndSession() ────────────► Closed   (explicit close)
```

State transitions:
- `Active → Idle`: `LastAccessedAt + IdleTimeoutMinutes < UtcNow` (detected by `AgentSessionCleanupService`)
- `Idle → Active`: any `InvokeAsync()` call with a valid session ID
- `Idle → Expired`: `LastAccessedAt + 2×IdleTimeoutMinutes < UtcNow`
- `Any → Closed`: `MaxTurns` reached, or explicit `EndSession()` call
- Callers who provide an `Expired` or `Closed` session ID receive `finish_reason: "session_expired"` — the tool does not error, it returns a clean JSON response indicating they must start a new session. This allows callers to self-recover without requiring special error handling.

### 3.3 Session Identity

Session IDs are generated as `Guid.NewGuid().ToString("N")` — 32 lowercase hex characters, 122 bits of entropy (6 bits are RFC 4122 version/variant constants). For local-first deployment (LocalDev/SecureLocal), this is amply sufficient.

For `RemoteHardened` mode, session IDs are HMAC-signed using `IDataProtectionProvider.CreateProtector("MemorySmith.AgentSessions")`. The signed token is opaque to callers and unguessable without the server-side key. Verification is a one-call `Unprotect()` in `AgentSessionService.GetOrResumeAsync()` before the database lookup.

### 3.4 `IAgentSessionStore` Interface

```csharp
// MemorySmith.App/Services/AgentSessions/IAgentSessionStore.cs

public interface IAgentSessionStore
{
    Task<AgentSession?> GetAsync(string sessionId, CancellationToken ct);
    Task SaveAsync(AgentSession session, CancellationToken ct);
    Task DeleteAsync(string sessionId, CancellationToken ct);

    /// Returns sessions with LastAccessedAt < expiryBefore, for cleanup.
    Task<IReadOnlyList<AgentSession>> GetIdleOrExpiredAsync(DateTimeOffset expiryBefore, CancellationToken ct);

    /// Used to enforce MaxConcurrentSessionsPerUser.
    Task<int> GetActiveCountForPrincipalAsync(string principalId, CancellationToken ct);
}
```

**`InMemoryAgentSessionStore`** (default):
- `ConcurrentDictionary<string, AgentSession>` backing store
- Zero additional dependencies, zero I/O latency
- Sessions lost on server restart
- Appropriate for `LocalDev` and `SecureLocal` where session loss is acceptable

**`SqliteAgentSessionStore`** (opt-in via `AgentSessionOptions.PersistSessions = true`):
- New `agent_sessions` table in the existing SQLite security/audit database
- History and EffectiveToolNames stored as JSON columns
- Enables session resumption after server restart
- Uses the existing migration loading pattern from `SqliteMemorySmithDatabase`

### 3.5 Session Housekeeping Service

`AgentSessionCleanupService : BackgroundService` runs on a 5-minute timer:

1. Calls `store.GetIdleOrExpiredAsync(DateTimeOffset.UtcNow.AddMinutes(-idleTimeout * 2))`
2. For each: set `Status = Expired`, write tombstone
3. After additional grace period (configurable, default 60 min): delete tombstone
4. Logs cleaned session count at `Debug` level; logs nothing if zero (no log noise on idle systems)

The two-phase (tombstone then delete) approach means callers who return after an extended absence get a clear `"session_expired"` response rather than a `"session not found"` error that would be harder to distinguish from a bad session ID.

---

## 4. MCP Tool Schema

### 4.1 Input Schema — `memorysmith_agent_invoke`

The tool follows the existing JSON Schema pattern in `ChatToolCatalog.cs` (see `BuildMemoryCreateSchema`, `BuildContextPackSchema` for style reference):

```json
{
  "name": "memorysmith_agent_invoke",
  "description": "Invoke the MemorySmith chat agent as a scoped sub-agent with its own managed context window. On the first call (no session_id), a new multi-turn session is created and the session_id is returned in the result. Include that session_id in subsequent calls to continue the conversation. The sub-agent autonomously searches the wiki, reads memories, and collates data using the tool scope you specify — you receive a single synthesized answer without the intermediate steps polluting your context window.",
  "inputSchema": {
    "type": "object",
    "required": ["message"],
    "properties": {
      "message": {
        "type": "string",
        "description": "The task or question for the sub-agent. Be specific — the sub-agent will run tool calls to answer it before replying."
      },
      "session_id": {
        "type": "string",
        "description": "Session ID returned from a prior call to this tool. Omit to start a new session. The session must have been created by the same caller principal; cross-principal access returns a session-not-found result."
      },
      "model_profile_id": {
        "type": "string",
        "description": "Optional model profile ID. Defaults to the server's configured default model. Only profiles with AllowedRoles that include the caller's role are accepted; silently falls back to default if the profile is inaccessible."
      },
      "scope": {
        "type": "string",
        "enum": ["read_only", "standard", "full", "custom"],
        "default": "standard",
        "description": "Tool access scope for the sub-agent session. read_only: search and fetch tools only. standard: read_only tools (reserved for future task-read expansion). full: all server-enabled MCP tools the caller has permission to use. custom: specify exact tool names via allowed_tools. Scope is always intersected with server-enabled tools and caller permissions — it cannot escalate beyond what the caller could do directly."
      },
      "allowed_tools": {
        "type": "array",
        "items": { "type": "string" },
        "description": "Required when scope=custom. List of memorysmith_* tool names to enable. Names outside the caller's permission set are silently removed. The tool memorysmith_agent_invoke is always excluded regardless of what is listed here."
      },
      "system_prompt_addendum": {
        "type": "string",
        "maxLength": 2000,
        "description": "Optional additional instructions appended to the sub-agent's system prompt. Requires CanEditMemorySmith role. Has no effect in remote-hardened security mode. Use to focus the sub-agent on a specific topic area or output format."
      },
      "max_turns": {
        "type": "integer",
        "minimum": 1,
        "maximum": 50,
        "default": 10,
        "description": "Maximum conversation turns before the session is automatically closed."
      },
      "timeout_seconds": {
        "type": "integer",
        "minimum": 10,
        "maximum": 600,
        "default": 120,
        "description": "Per-turn inference timeout in seconds."
      }
    }
  }
}
```

### 4.2 Output Schema

The tool returns a JSON text result via the existing `ToolText()` helper in `McpController`. The returned string is a JSON object:

```json
{
  "session_id": "a3f2c1d4e5b67890a1b2c3d4e5f60011",
  "turn": 2,
  "message": "Based on my search of the wiki, here is what I found regarding...",
  "context_ids": ["memory:abc123", "page:project-x-overview"],
  "tool_calls_made": 3,
  "finish_reason": "stop",
  "usage": {
    "prompt_tokens": 2340,
    "completion_tokens": 412
  }
}
```

`finish_reason` values:

| Value | Meaning |
|-------|---------|
| `"stop"` | Normal completion — session remains active, further turns allowed |
| `"max_turns"` | `MaxTurns` reached — session is now `Closed`; start a new session to continue |
| `"timeout"` | Per-turn inference timeout exceeded — session remains active; retry the same message |
| `"session_expired"` | Provided `session_id` refers to an expired or closed session; start a new session |
| `"error"` | Unrecoverable internal error; `message` contains description |

### 4.3 Housekeeping Tool — `memorysmith_agent_session_end`

```json
{
  "name": "memorysmith_agent_session_end",
  "description": "Explicitly close a session created by memorysmith_agent_invoke. Frees resources immediately rather than waiting for idle timeout. Use when you know the sub-agent conversation is complete.",
  "inputSchema": {
    "type": "object",
    "required": ["session_id"],
    "properties": {
      "session_id": { "type": "string" }
    }
  }
}
```

Risk tier: `ReadOnly` (no data mutation, only session state change). `EnabledByDefaultInMcp: false` (matches the parent tool's default-off behaviour in SecureLocal/RemoteHardened).

### 4.4 Risk Tier Assignment for `memorysmith_agent_invoke`

`ChatToolRisk.Write`. Rationale:

1. When `scope = "full"`, the sub-agent can execute `memorysmith_memory_create`, `memorysmith_task_create`, etc. — genuine write operations.
2. Even at `scope = "read_only"`, the tool creates a persistent session entity and triggers LLM inference — side effects that are not present in pure read operations.
3. `Write` tier correctly requires `CanEditMemorySmith` from the caller, consistent with the existing pattern: `memorysmith_memory_create`, `memorysmith_page_save`, and all write tools carry the same tier.

Registration flags: `AvailableInMcp: true`, `AvailableInChat: false`, `AvailableInAgent: false` (Phase 1–2), `EnabledByDefaultInMcp: false`.

`AvailableInAgent: true` is enabled in Phase 3 only, after the GPU yield pattern (§7.4) is implemented. Enabling it before Phase 3 would allow Athena to call it from agent mode and trigger the deadlock described in §7.4.

---

## 5. Scope and Auth Intersection System

### 5.1 Scope-to-Tool-Set Mapping

Based on the 24 tools in `ChatToolCatalog.cs` (verified against `BuildTools()`, lines 125–1160):

**`read_only` scope** (13 tools):
All tools where `Risk == ReadOnly && AvailableInMcp == true`:
`memorysmith_search`, `memorysmith_semantic_search`, `memorysmith_hybrid_search`, `memorysmith_context_pack`, `memorysmith_get`, `memorysmith_code_search`, `memorysmith_code_search_status`, `memorysmith_page_search`, `memorysmith_page_get`, `memorysmith_unified_search`, `memorysmith_task_list`, `memorysmith_task_get`

**`standard` scope** (13 tools, same set today):
Identical to `read_only` in the current catalog. Reserved for future expansion — e.g., if a task-write tool is added that should be a "standard safe" operation but not truly write-tier. The name distinction future-proofs the API without a breaking schema change.

**`full` scope** (up to 24 tools, after CallerPermissions filter):
All server-enabled MCP tools filtered down by the caller's permissions (see §5.2).

**`custom` scope**:
Caller-specified list, validated and intersected with CallerPermissions.

### 5.2 Effective Scope Computation (`ComputeEffectiveScope`)

This is the most critical method in `AgentSessionService`. It runs once at session creation and its result is sealed into `AgentSession.EffectiveToolNames`.

```
Input:
  requestedScope      string
  allowedTools        IReadOnlyList<string>?   (for scope=custom)
  callerClaims        ClaimsPrincipal
  securityProfile     string

Algorithm:

Step 1 — CatalogSet:
  All tools in ChatToolCatalog where AvailableInMcp == true
  (24 tools currently)

Step 2 — EnabledSet:
  CatalogSet ∩ IsMcpToolEnabled(tool)
  Checks: DisabledTools list, EnabledTools list, EnabledByDefaultInMcp flag
  (Replicates McpController.IsMcpToolEnabled() exactly — same logic, different call site)

Step 3 — CallerSet:
  EnabledSet filtered by risk tier vs. caller's authorization:
    Risk.ReadOnly       → always included
    Risk.SensitiveRead  → include iff CanReadSourceBundle(callerClaims)
    Risk.Write          → include iff CanEditMemorySmith(callerClaims)

Step 4 — ScopeSet:
  match requestedScope:
    "read_only"  → ReadOnly tools in EnabledSet
    "standard"   → ReadOnly tools in EnabledSet  (same today)
    "full"       → CallerSet (all risk tiers caller can use)
    "custom"     → allowedTools ∩ CallerSet  (unrecognized names silently dropped)

Step 5 — ProfileCap (SecurityProfile ceiling):
  "local-dev"       → no cap (ScopeSet unchanged)
  "secure-local"    → remove Write-risk tools from ScopeSet
  "remote-hardened" → remove SensitiveRead- and Write-risk tools from ScopeSet

Step 6 — SafeSet (self-exclusion):
  ScopeSet after ProfileCap, minus:
    "memorysmith_agent_invoke"
    "memorysmith_agent_session_end"
  (unconditional — sub-agents cannot spawn or manage sessions)

Return SafeSet as IReadOnlyList<string>
```

The intersection is expressed as a pure function with no side effects, making it straightforwardly unit-testable. Six test cases cover the meaningful combinations: `full/LocalDev/Write-capable caller`, `full/RemoteHardened/Write-capable caller`, `custom/SecureLocal/Write-capable caller`, `read_only/any-profile/ReadOnly-only caller`, `custom-with-unknown-names/any`, `full/LocalDev/ReadOnly-only caller`.

### 5.3 Scope Immutability After Session Creation

`AgentSession.EffectiveToolNames` is an `IReadOnlyList<string>` sealed at construction. If the server configuration changes after session creation (e.g., an admin disables a tool via `McpOptions.DisabledTools`), the session continues with its creation-time tool set until it expires or is explicitly closed. The reason: changing tool availability mid-session would produce LLM confusion — Athena might reference a tool it used in turn 1 that is no longer available in turn 2.

Administrators who need to revoke a tool urgently should close affected sessions manually via the admin UI (§12, Phase 2).

### 5.4 Audit Trail

`AgentSession.RequestedScope` and `AgentSession.EffectiveToolNames` are both persisted. This makes it auditable: "what scope did session X have, and what did the computation actually produce from that request?"

---

## 6. Model Selection Wiring

### 6.1 `model_profile_id` Resolution Chain

```csharp
private async Task<ChatModelProfileView> ResolveModelProfileAsync(
    string? requestedProfileId,
    ClaimsPrincipal caller,
    CancellationToken ct)
{
    // 1. Try the requested profile
    ChatModelProfileView? profile = null;
    if (requestedProfileId is not null)
        profile = await _modelProfileService.GetByIdAsync(requestedProfileId, ct);

    // 2. Fall back to default
    profile ??= await _modelProfileService.GetDefaultAsync(ct);

    // 3. Enforce AllowedRoles — silent downgrade to default
    //    (no error to avoid leaking profile existence)
    if (profile.AllowedRoles.Count > 0)
    {
        var callerRoles = caller.FindAll(ClaimTypes.Role)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!profile.AllowedRoles.Any(r => callerRoles.Contains(r)))
            profile = await _modelProfileService.GetDefaultAsync(ct);
    }

    // 4. Disabled profile → default
    if (!profile.Enabled)
        profile = await _modelProfileService.GetDefaultAsync(ct);

    return profile;
}
```

The resolved `ChatModelProfileView.Model` flows into `MemoryChatRequest.Model` and `ChatModelProfileView.Provider` flows into `MemoryChatRequest.Provider`. `MemoryChatAgent` already handles model/provider resolution from these fields via `ResolveProvider()` (confirmed in `SendAsync`, line 1778). No new plumbing is needed here — the existing infrastructure carries it.

### 6.2 Context Window Enforcement

`ChatModelProfileView.ContextWindowTokens` feeds the `num_ctx` Ollama option via `BuildOllamaRequestOptions()` — the fix for TRAIN-001 (now closed per Audit #9). Sub-agent sessions inherit this correctly for any profile. No additional work required.

### 6.3 `AgentSessionOptions.DefaultSubAgentModelProfileId`

Optional configuration letting administrators route sub-agent sessions to a different default model than the main Athena sessions:

```json
// appsettings.json example:
"AgentSession": {
  "DefaultSubAgentModelProfileId": "profile-id-of-fast-4b-model",
  "PersistSessions": false,
  "MaxConcurrentSessionsPerUser": 3,
  "IdleTimeoutMinutes": 30
}
```

If unset, falls back to the global `Chat:DefaultModelProfileId`. This is the idiomatic way to run "fast 4B for sub-agent searches, leave main profile for Athena's complex tasks" — a common local deployment pattern on constrained VRAM.

---

## 7. GPU Slot Scheduling

### 7.1 The Problem

`OllamaChatProvider` makes direct HTTP calls to `http://localhost:11434/api/chat`. Ollama does serialize requests internally, but MemorySmith has no visibility into the Ollama queue — it only sees responses. This creates two failure modes:

1. **OOM at large context windows**: A 24K-token KV cache for session A occupies ~3–4 GB of the 8 GB budget. A concurrent 24K session B tries to load simultaneously → OOM or severe degradation. Ollama's HTTP 200 response arrives only after memory allocation, which may already have failed.
2. **Latency cliff**: Even below OOM threshold, simultaneous large-context requests cause KV cache thrashing. A 10-second call becomes 40 seconds.

The existing codebase has no GPU scheduling (confirmed: no `SemaphoreSlim`, no `GpuSlot` pattern in `OllamaChatProvider.cs`).

### 7.2 `IGpuSlotScheduler` Interface

```csharp
// MemorySmith.App/Services/IGpuSlotScheduler.cs

/// <summary>
/// Controls access to a single local GPU inference backend (Ollama).
/// Prevents concurrent inference sessions from OOM'ing the device.
/// </summary>
public interface IGpuSlotScheduler
{
    /// <summary>
    /// Acquires an inference slot. Blocks until a slot is available or ct is cancelled.
    /// Dispose the returned handle to release the slot.
    /// </summary>
    Task<IAsyncDisposable> AcquireAsync(string reason, CancellationToken ct);

    /// Current count of callers waiting for a slot — exposed for health/metrics.
    int WaitingCount { get; }
}
```

**`OllamaGpuSlotScheduler`** (single concrete implementation):

```csharp
// MemorySmith.App/Services/OllamaGpuSlotScheduler.cs

public sealed class OllamaGpuSlotScheduler : IGpuSlotScheduler
{
    private readonly SemaphoreSlim _semaphore;
    private int _waiting;

    public OllamaGpuSlotScheduler(IOptionsMonitor<MemorySmithOptions> options)
    {
        // MaxParallelOllamaRequests defaults to 1 (serial).
        // Users with 16+ GB VRAM or cloud providers may set to 2.
        var maxParallel = Math.Max(1, options.CurrentValue.Chat.MaxParallelOllamaRequests);
        _semaphore = new SemaphoreSlim(maxParallel, maxParallel);
    }

    public int WaitingCount => Volatile.Read(ref _waiting);

    public async Task<IAsyncDisposable> AcquireAsync(string reason, CancellationToken ct)
    {
        Interlocked.Increment(ref _waiting);
        try
        {
            await _semaphore.WaitAsync(ct);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
        return new SlotHandle(_semaphore);
    }

    private sealed class SlotHandle(SemaphoreSlim sem) : IAsyncDisposable
    {
        private int _disposed;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) sem.Release();
            return ValueTask.CompletedTask;
        }
    }
}
```

A `NullGpuSlotScheduler` is registered when the provider is `GitHubCopilot` (cloud provider, no local VRAM constraint):

```csharp
public sealed class NullGpuSlotScheduler : IGpuSlotScheduler
{
    public static readonly NullGpuSlotScheduler Instance = new();
    public int WaitingCount => 0;
    public Task<IAsyncDisposable> AcquireAsync(string reason, CancellationToken ct)
        => Task.FromResult<IAsyncDisposable>(NullDisposable.Instance);
}
```

### 7.3 `OllamaChatProvider` Integration

The injection is minimal and backward-compatible (optional parameter):

```csharp
// Modified OllamaChatProvider constructor (ChatServices.cs, currently line 455):
public OllamaChatProvider(
    HttpClient httpClient,
    IOptionsMonitor<MemorySmithOptions> options,
    IGpuSlotScheduler? gpuSlots = null,
    ILogger<OllamaChatProvider>? logger = null)

// Modified CompleteAsync (currently line 472):
public async Task<ChatProviderResponse> CompleteAsync(ChatProviderRequest request, CancellationToken ct)
{
    await using var slot = _gpuSlots is not null
        ? await _gpuSlots.AcquireAsync("ollama-complete", ct)
        : NullDisposable.Instance;

    // ... existing body unchanged ...
}

// StreamAsync: same acquire/release pattern around the streaming loop
```

`GitHubCopilotChatProvider` receives no `IGpuSlotScheduler` injection — its HTTP calls go to Microsoft's servers and don't consume local VRAM.

### 7.4 The Double-Call Hazard (Internal Delegation)

This is the hardest part of the design and the primary reason Phase 3 (internal delegation) is deferred.

**The problem:**

```
Timeline (broken — Athena calls memorysmith_agent_invoke from agent-mode tool loop):

  T=0  Athena:      AcquireGpuSlot() ──────────── holds slot
  T=1  Athena:      CompleteAsync() (Ollama streaming)
  T=8  Athena:      receives "tool call: memorysmith_agent_invoke"
  T=8  Athena:      awaits AgentInvokeTool.Execute()
  T=8  Sub-agent:   AcquireGpuSlot() ←── BLOCKS: Athena still holds it
  T=8  Athena:      awaiting sub-agent result ←── DEADLOCK
```

**Solution: yield-before-delegate**

`AgentInvokeTool.Execute()` must release Athena's GPU slot before acquiring the sub-agent's slot. This requires the slot handle to be passed through `ChatToolExecutionContext`:

```csharp
// ChatToolExecutionContext extension (ChatToolCatalog.cs, line 33):
public sealed record ChatToolExecutionContext(
    MemoryApplicationService Memories,
    IPageService Pages,
    string Transport,
    ClaimsPrincipal User,
    AuthOptions Auth,
    string? DefaultPageMinimumRole,
    VarResolver Vars,
    ITaskService? Tasks,
    CodeSearchService? CodeSearch,
    bool AgentWritesEnabled,
    bool AgentWriteAutoAccept,
    int NestingDepth = 0,                          // new — anti-recursion (§8)
    string? ParentSessionId = null,                // new — delegation chain logging
    IAsyncDisposable? InheritedGpuSlot = null);    // new — yield-before-delegate
```

`MemoryChatAgent`'s tool-execution loop (the agentic call path in `CompleteWithToolCallsAsync`) passes `InheritedGpuSlot = currentSlotHandle` when constructing the `ChatToolExecutionContext` for agent-mode tool calls. The slot handle is the one held by the current `OllamaChatProvider.CompleteAsync()` invocation.

`AgentInvokeTool.Execute()`:

```csharp
public override async Task<ChatToolExecutionResult> Execute(
    JsonObject args, ChatToolExecutionContext ctx, CancellationToken ct)
{
    // Phase 3 only: yield Athena's GPU slot before acquiring sub-agent's
    if (ctx.InheritedGpuSlot is not null)
        await ctx.InheritedGpuSlot.DisposeAsync();

    var result = await _sessionService.InvokeAsync(sessionId, message, ctx, ct);

    // Caller (MemoryChatAgent) re-acquires GPU slot on next OllamaChatProvider call
    return ChatToolExecutionResult.Text(SerializeResult(result));
}
```

After `AgentInvokeTool.Execute()` returns, `MemoryChatAgent` continues its tool loop. The next iteration calls `OllamaChatProvider.CompleteAsync()` which re-acquires the GPU slot normally. The slot gap (between yield and re-acquire) is the sub-agent's execution window — serial, correct.

This coupling between tool execution and slot handles is the reason Phase 3 requires care. It touches the inner tool-loop of `MemoryChatAgent` and must be verified against the streaming path (`StreamAsync`) as well.

---

## 8. Anti-Recursion Design

### 8.1 Nesting Depth Tracking

`ChatToolExecutionContext.NestingDepth` (added in §7.4) starts at 0 for all external MCP calls (`McpController.DelegateToCatalogAsync` sets it explicitly). When `AgentSessionService` creates a new session from an internal invocation, it sets `NestingDepth = ctx.NestingDepth + 1`.

`AgentSessionOptions.MaxNestingDepth` (default: 1) is the hard ceiling.

### 8.2 Self-Exclusion via Scope Computation

Step 6 of `ComputeEffectiveScope()` (§5.2) unconditionally removes `memorysmith_agent_invoke` and `memorysmith_agent_session_end` from the sub-agent's tool set. This is:

- **Depth-independent**: even a depth-0 external session cannot have these tools in its catalog (though it created a session, it cannot create nested sessions via tool calls)
- **Belt-and-suspenders**: the `AvailableInAgent: false` flag (Phase 1–2) provides a second layer

When `NestingDepth >= MaxNestingDepth`, the scope computation removes the agent-invoke tools AND `AgentSessionService.CreateSessionAsync()` returns an error for any further nesting attempts.

### 8.3 Session Chain Logging

`AgentSession.ParentSessionId` (nullable) creates an auditable parent-child relationship between sessions. A query like:

```sql
SELECT session_id, parent_session_id, turn_count, effective_scope
FROM agent_sessions
WHERE parent_session_id = 'athena-session-id-here'
```

recovers the full delegation tree. Combined with `ChatTurnRecord.ParentSessionId` (§11), each sub-agent transcript turn is linkable back to the Athena turn that spawned it.

---

## 9. `MemoryChatRequest` Extension

The cleanest, most minimal way to pass the effective tool filter to `MemoryChatAgent` is a new optional positional parameter on `MemoryChatRequest`:

```csharp
// ChatServices.cs — existing record (line 154), with one new parameter:
public sealed record MemoryChatRequest(
    string Message,
    MemoryChatMode Mode = MemoryChatMode.Chat,
    IReadOnlyList<ChatMessage>? History = null,
    string? Model = null,
    IReadOnlyList<ChatAttachment>? Attachments = null,
    string? Provider = null,
    ChatRunControl? RunControl = null,
    bool RequireAgentWriteApproval = false,
    string? SessionId = null,
    IReadOnlyList<string>? ToolFilter = null);   // ← new optional, null = all tools available
```

`MemoryChatAgent` applies the filter in the method that presents tools to the provider. Based on the agent's structure (confirmed: `_toolCatalog` is `ChatToolCatalog`, tools are iterated when building `ChatProviderToolDefinition` list), the change is approximately:

```csharp
// Pseudocode of the targeted change — localized to one method in MemoryChatAgent:
private IReadOnlyList<ChatProviderToolDefinition> GetPresentableTools(MemoryChatRequest request)
{
    var tools = request.Mode == MemoryChatMode.Agent
        ? _toolCatalog.AgentTools
        : _toolCatalog.ChatTools;

    if (request.ToolFilter is { Count: > 0 } filter)
        tools = tools.Where(t => filter.Contains(t.Name)).ToList();

    return tools.Select(BuildProviderToolDefinition).ToList();
}
```

This is approximately 3 lines added to `MemoryChatAgent`. No other class is modified.

**Why not create a filtered `ChatToolCatalog` subclass?** The `MemoryChatAgent` constructor accepts `ChatToolCatalog?` (line 1744), which looks like a clean injection point. But `ChatToolCatalog.BuildTools()` (the private factory that creates all 24 tool definitions) is not designed for subclassing — overriding it would require deep knowledge of the class internals and would be brittle against catalog changes. The `ToolFilter` approach is strictly additive and touches one method.

---

## 10. Security Analysis

### 10.1 Session ID Entropy

| Profile | Session ID format | Entropy | Notes |
|---------|------------------|---------|-------|
| LocalDev | `Guid.NewGuid().ToString("N")` | 122 bits | Sufficient for single-actor local |
| SecureLocal | Same | 122 bits | Acceptable; network is localhost-only |
| RemoteHardened | `IDataProtectionProvider.Protect(rawGuid)` | ~256 bits (AES-256 sealed) | Prevents crafted-ID attacks over network |

### 10.2 Session Ownership

Every `InvokeAsync()` call begins:

```csharp
if (session.PrincipalId != caller.FindFirstValue(ClaimTypes.NameIdentifier))
    return AgentInvokeResult.SessionNotFound();  // returns same error as "not found"
```

The "same error as not found" pattern prevents an attacker from distinguishing "session doesn't exist" from "session belongs to someone else" — a subtle but important information-hiding property. This follows the same approach used in security-sensitive web frameworks (e.g., OWASP IDOR guidance).

### 10.3 Scope Downgrade Only

`ComputeEffectiveScope()` (§5.2) is a pure intersection. It produces a result that is a subset of what the caller could exercise by calling tools directly. This invariant is maintained even if:
- The caller requests `scope = "full"` but lacks `CanEditMemorySmith` — write tools are removed
- A custom `allowed_tools` list names a `SensitiveRead` tool the caller lacks permission for — it is silently removed
- The SecurityProfile is `RemoteHardened` — the ceiling removes both SensitiveRead and Write tools regardless of what was requested

There is no code path that adds a tool to the effective set that the caller cannot already invoke directly.

### 10.4 `system_prompt_addendum` Risk Analysis

**Threat**: A `CanEditMemorySmith` caller injects instructions into the sub-agent's system prompt, overriding its governance rules or redirecting its tool use.

**Mitigations in this design:**
1. **Role gate**: Only callers with `CanEditMemorySmith` can supply this parameter. Without the role, the parameter is silently ignored (not rejected — to avoid leaking role information through error responses).
2. **Append-only**: The addendum is appended to the existing system prompt, never replaces it. The Athena persona, governance rules, and wiki context instructions remain intact at the head of the prompt.
3. **Length cap**: 2,000 characters maximum (enforced in schema validation and in `AgentSessionService`).
4. **RemoteHardened disable**: In `RemoteHardened` mode, `system_prompt_addendum` is always a no-op, regardless of role. This is the right default for network-exposed deployments.
5. **Audit log**: The use of `system_prompt_addendum` should be recorded in the audit log as a security event, providing an investigation trail.

**Residual risk**: A `CanEditMemorySmith` user could craft an addendum that causes the sub-agent to behave unexpectedly. This is acceptable given that a `CanEditMemorySmith` user can already modify memories, pages, and system configuration directly. The addendum grants them no capability they don't already have.

### 10.5 Concurrent Session Cap

`AgentSessionOptions.MaxConcurrentSessionsPerUser` by SecurityProfile:

| Profile | Default cap | Rationale |
|---------|-------------|-----------|
| `local-dev` | 10 | Experimentation; no real threat model |
| `secure-local` | 3 | Prevents resource exhaustion; single trusted user |
| `remote-hardened` | 1 | Minimal attack surface; network exposure |

The cap is enforced in `CreateSessionAsync()` via `store.GetActiveCountForPrincipalAsync()`. Exceeding the cap returns `HTTP 429 Too Many Requests` at the MCP tool level (a `ChatToolExecutionResult` with `IsError = true` and an appropriate message).

### 10.6 Interactions with Existing Audit Findings

| Finding | Interaction |
|---------|-------------|
| **SEC-XSS-01** (GenericAttributes, still open) | Sub-agent output flows through `ToolText()` JSON serialization in `McpController`, then back to the calling agent as a JSON string. It does not pass through Markdig rendering. Not affected. |
| **SEC-INJECT-01** (Audit #7 — indirect prompt injection via malicious memory content) | Sub-agent sessions preload memories into context, inheriting the same injection risk as direct Athena sessions. Mitigation: sub-agent sessions should use a tighter context budget (fewer preloaded memories) via a `AgentSessionOptions.MaxContextItems` cap. This does not fix SEC-INJECT-01 but limits the attack surface per sub-agent session. |
| **SEC-ROLE-01** (Audit #7 — arbitrary role assignment via `/api/admin/users/{id}/roles/{roleName}`) | If an attacker escalates their role via SEC-ROLE-01, then creates a sub-agent session, the session inherits the escalated scope. Remediate SEC-ROLE-01 independently — it pre-dates this feature and is orthogonal to it. |
| **Audit #5 Clipboard-paste external image fetch** | Sub-agent has no Blazor UI component. Not applicable. |
| **Audit #5 Mermaid `innerHTML` XSS** | Sub-agent responses don't render in the Blazor Mermaid component. Not applicable. |

---

## 11. Logging and Transcript Integration

### 11.1 `ChatTurnRecord` Extension

One new optional field is added to `ChatTurnRecord` (currently at `Training/ChatTurnRecord.cs`):

```csharp
public sealed record ChatTurnRecord
{
    // ... all existing fields unchanged ...

    public string? ParentSessionId { get; init; }   // ← new, null for non-delegation turns
}
```

This is backward-compatible — the field is nullable and not `required`. Existing readers ignore it. The `IChatTranscriptWriter` interface signature does not change.

### 11.2 `ModeIntent` Value for Sub-Agent Turns

Sub-agent sessions write `ModeIntent = "sub_agent"` to `ChatTurnRecord.ModeIntent`. This enables:

1. **Training corpus filtering**: `harness.py`'s `load_training_data()` should filter `mode_intent == "sub_agent"` turns by default (via `TrainingOptions.IncludeSubAgentTurns`, default `false`). Sub-agent turns have different quality distribution (shorter, task-specific, no persona) and should not dilute Athena's training signal.
2. **Audit queries**: `SELECT * FROM chat_turns WHERE mode_intent = 'sub_agent' AND parent_session_id = 'X'` recovers all sub-agent work tied to a specific Athena session.
3. **Metrics segmentation**: sub-agent turn latency and token counts are separate from Athena's, enabling independent performance tracking.

### 11.3 Transcript Redaction

`ChatTranscriptWriter` already applies `BearerPattern` and `SecretPattern` redaction via `Redact()`. Sub-agent turns write through the same writer and receive identical redaction treatment. No changes to `ChatTranscriptWriter` are required for logging.

### 11.4 Session Lifecycle Events in the Audit Log

The existing `AuditLogService` should record four new event types:

| Event | Trigger |
|-------|---------|
| `AgentSessionCreated` | Session successfully created |
| `AgentSessionResumed` | Existing session continued |
| `AgentSessionClosed` | Explicit close or MaxTurns reached |
| `AgentSessionExpired` | Cleanup service marks session expired |

These use the existing `AuditEventType` pattern (extend the enum or constant class). The events are **not** HMAC-chained (they are informational, not security-critical) — consistent with the existing separation between security events (HMAC-chained) and informational events.

---

## 12. Implementation Roadmap

### 12.1 New Files

| File | Est. LOC | Notes |
|------|----------|-------|
| `Services/AgentSessions/AgentSession.cs` | ~90 | Entity, status enum, internal mutation methods |
| `Services/AgentSessions/IAgentSessionStore.cs` | ~30 | 5-method interface |
| `Services/AgentSessions/InMemoryAgentSessionStore.cs` | ~75 | ConcurrentDictionary + per-session locks |
| `Services/AgentSessions/SqliteAgentSessionStore.cs` | ~140 | SQLite impl + migration (Phase 2) |
| `Services/AgentSessions/AgentSessionService.cs` | ~260 | Core orchestration, scope intersection, model resolution |
| `Services/AgentSessions/AgentSessionCleanupService.cs` | ~55 | BackgroundService, 5-min timer, tombstone pattern |
| `Services/IGpuSlotScheduler.cs` | ~45 | Interface + NullGpuSlotScheduler + NullDisposable |
| `Services/OllamaGpuSlotScheduler.cs` | ~65 | SemaphoreSlim impl |
| **Total new** | **~760 LOC** | |

### 12.2 Modified Files

| File | Specific change | Risk |
|------|----------------|------|
| `Services/ChatServices.cs` | (a) Add `ToolFilter` param to `MemoryChatRequest`. (b) Add `IGpuSlotScheduler?` to `OllamaChatProvider` constructor; wrap `CompleteAsync` and `StreamAsync` with acquire/release. (c) Add `IReadOnlyList<string>? GetPresentableTools` filter in `MemoryChatAgent`. | Medium — 4000-line file; three targeted, additive changes |
| `Services/ChatToolCatalog.cs` | (a) Add `memorysmith_agent_invoke` tool definition. (b) Add `memorysmith_agent_session_end` tool definition. (c) Add `NestingDepth`, `ParentSessionId`, `InheritedGpuSlot` to `ChatToolExecutionContext` record. | Medium — 1700-line file; two new tools (follow existing patterns), three record fields |
| `Services/MemorySmithOptions.cs` | Add `AgentSessionOptions` class; add `MaxParallelOllamaRequests` to `ChatOptions`; add `MaxConcurrentSessionsPerUser` to `McpOptions`. | Low — additive, follows existing option patterns |
| `Services/Training/ChatTurnRecord.cs` | Add `string? ParentSessionId`. | Low — one nullable field |
| `Controllers/McpController.cs` | Pass `NestingDepth = 0` explicitly when constructing `ChatToolExecutionContext` in `DelegateToCatalogAsync`. | Low — one-line change in existing construction site |
| DI registration (`Program.cs` or extension) | Register `IAgentSessionStore` (default: `InMemoryAgentSessionStore`), `AgentSessionService`, `AgentSessionCleanupService`, `IGpuSlotScheduler` (default: `OllamaGpuSlotScheduler`). | Low |
| **Total modified** | **~55 lines across 6 files** | |

### 12.3 Phase Plan

**Phase 1 — Core (~1 week)**
Delivers: external MCP callers can invoke the sub-agent, get a session ID, continue the session, and explicitly close it. GPU scheduling enforced. Scope intersection enforced. Auth fully respected.

Commits:
1. `feat: add IGpuSlotScheduler + OllamaGpuSlotScheduler` — standalone, no behavior change until wired
2. `feat: wrap OllamaChatProvider HTTP calls with GPU slot acquire/release` — first behavior-changing commit; existing behavior unchanged (slot count = 1, serial)
3. `feat: AgentSession store layer (entity, interface, in-memory impl, cleanup service)` — no external surface yet
4. `feat: AgentSessionService with scope intersection, model resolution, and session lifecycle`
5. `feat: ToolFilter on MemoryChatRequest + MemoryChatAgent filter application`
6. `feat: register memorysmith_agent_invoke and memorysmith_agent_session_end in ChatToolCatalog`
7. `feat: wire McpController → AgentSessionService for the two new tools`

**Phase 2 — Polish and Persistence (~3 days)**

8. `feat: SqliteAgentSessionStore with migration` — enable `PersistSessions: true` opt-in
9. `feat: system_prompt_addendum with CanEditMemorySmith gate and RemoteHardened no-op`
10. `feat: ChatTurnRecord.ParentSessionId + ModeIntent="sub_agent" in transcript writer`
11. `feat: SecurityProfile-driven defaults for AgentSessionOptions`
12. `feat: /admin/sessions Blazor page — active session list, manual close`

**Phase 3 — Internal Delegation (~1 week, aligned with sub-agent Sprint G/H)**

13. `feat: InheritedGpuSlot in ChatToolExecutionContext`
14. `feat: yield-before-delegate in MemoryChatAgent tool loop`
15. `feat: set AvailableInAgent=true for memorysmith_agent_invoke`
16. `feat: nesting depth enforcement in AgentSessionService`
17. `feat: session chain display in /admin/sessions UI`

### 12.4 Testing Requirements

**Unit tests (high priority, Phase 1):**
- `ComputeEffectiveScope()`: 6 core test cases (scope × profile × permission combinations)
- Session state machine: all valid transitions; invalid transitions return expected errors
- Self-exclusion: `memorysmith_agent_invoke` is never present in any computed SafeSet
- Session ownership: cross-principal `InvokeAsync()` returns `SessionNotFound` (same as not-found, not `Forbidden` — see §10.2)
- `ToolFilter` application: agent presented only allowed tools

**Unit tests (Phase 3):**
- GPU yield-before-delegate: `InheritedGpuSlot.DisposeAsync()` is called before sub-agent `AcquireAsync()`; no deadlock
- Nesting depth: depth 1 sessions do not contain `memorysmith_agent_invoke` in their tool catalog

**Integration test (Phase 1):**
- Full round-trip: `POST /mcp` with `memorysmith_agent_invoke` → new session created → follow-up call with returned `session_id` → conversation continued → `memorysmith_agent_session_end` → session closed
- Scope enforcement: `read_only` session cannot execute `memorysmith_memory_create`

---

## 13. Open Questions

| # | Question | Recommendation | Confidence |
|---|----------|----------------|------------|
| OQ-1 | Session persistence default: in-memory (ephemeral) or SQLite-backed? | In-memory default. SQLite opt-in via `AgentSessionOptions.PersistSessions = true`. For local-first use, surviving server restarts is rarely needed. The SQLite path adds migration complexity that should be opt-in. | 0.85 |
| OQ-2 | Idle timeout defaults: 30 min (LocalDev), 10 min (SecureLocal), 5 min (RemoteHardened)? | Yes, these are reasonable starting values. Expect to tune downward after observing real usage patterns. | 0.70 |
| OQ-3 | Should `system_prompt_addendum` be in Phase 1 or Phase 2? | Phase 2. It requires additional audit-log events, SecurityProfile gating, and the admin UI to make it discoverable. It is not needed for the core use case. | 0.80 |
| OQ-4 | Should active sub-agent sessions be visible in the Blazor admin UI? | Yes — in Phase 2. A minimal `/admin/sessions` page (or a tab on `/admin/training`) showing session ID prefix, caller, scope, turn count, last-accessed, status. Admin can close manually. This is essential for debugging and for SEC-ROLE-01 mitigation pending that fix. | 0.85 |
| OQ-5 | `EnabledByDefaultInMcp`: opt-in (false) or opt-out (true) for this tool? | `false` in SecureLocal and RemoteHardened; `true` in LocalDev. Consistent with existing pattern: all Write-tier tools default to `EnabledByDefaultInMcp = false`. This is the safest default — administrators must explicitly enable the tool in production. | 0.90 |
| OQ-6 | Should `model_profile_id` accept raw model names (e.g., `"qwen3.5:4b"`) in addition to profile IDs? | No. Require profile IDs only. Raw model names bypass `AllowedRoles` and `ContextWindowTokens` enforcement. The profile system is the governance layer for model access — circumventing it with raw names would create an unmaintainable split. | 0.85 |

---

## 14. Cross-References

| Reference | Relevance |
|-----------|-----------|
| **Sub-Agent Architecture design doc** (`[[FILE_4fnzo8t5]]`, 2026-05-31) | This design covers the "Sprint G" primitives from that doc (`VramScheduler` → `IGpuSlotScheduler`, `SubAgentRunner` → `AgentSessionService`). The `AgentDefinition` data model from that doc is explicitly NOT included here — this design is narrower and shippable in 2 weeks. Sprint G/H can build on top of this foundation. |
| **Audit #5, SEC-INJECT-01** | Indirect prompt injection via malicious memory content applies equally to sub-agent sessions. Recommend `AgentSessionOptions.MaxContextItems` cap for sub-agent sessions pending a broader fix. |
| **Audit #7, SEC-XSS-01** | GenericAttributes XSS does not affect sub-agent output path (JSON → `ToolText()`, never Markdig). |
| **Audit #7, SEC-ROLE-01** | Sub-agent scope inherits from caller claims. Fix SEC-ROLE-01 independently. |
| **Training harness design** (2026-05-28) | `ChatTurnRecord.ParentSessionId` and `ModeIntent = "sub_agent"` enable training corpus pipeline to distinguish sub-agent turns. `TrainingOptions.IncludeSubAgentTurns` (default `false`) provides explicit corpus control. |
| **UX supplement** (`[[FILE_b7ma928f]]`, 2026-05-28) | Admin sessions page is a natural extension of the `/admin/training` page proposed in the supplement. Phase 2 of this roadmap should coordinate with the supplement's commit 5–7 plan. |
| **Audit #9 P0: `num_ctx` in modelfile** | Already closed. Context window enforcement via `ContextWindowTokens` on model profiles works correctly; sub-agent sessions inherit this. |
| **PR #45 (batched QLoRA fix)** | Unrelated to this design but the same branch. Phase 1 of this design should land before or after PR #45 to minimize conflict surface on `ChatServices.cs`. |

---

## 15. Assumptions

All source evidence is first-hand: files read via GitHub MCP integration from master @ `b69542fc`. The branch `feature/code-search-high-roi-batch8` was not accessible (not found in remote branch list). The following assumptions are made:

| Assumption | Confidence | Evidence basis |
|------------|------------|----------------|
| `MemoryChatRequest` fields and `IChatAgent` interface signature on master match the active dev branch | 0.80 | master and the active branch share the same commit ancestry through Audit #6 review |
| `MemoryChatAgent` constructor's optional `toolCatalog` parameter at line 1744 is the correct hook for filtered catalogs | 0.90 | Verified directly: `_toolCatalog = toolCatalog ?? new ChatToolCatalog()` |
| `MaxParallelOllamaRequests = 1` (serial) is safe for RTX 5060 8 GB with 16K context windows | 0.85 | VRAM heuristic from UX supplement: 16K context ≈ 3.5 GB KV cache; two sessions = 7 GB + weights = OOM |
| `AgentModelPreference` does not exist in the codebase | 0.95 | GitHub search across all branches, zero results |
| `VramScheduler`, `SubAgentRunner`, `IAgentOrchestrator` do not exist | 0.95 | GitHub search across all branches, zero results |
| `ChatToolCatalog.BuildTools()` is not safely overridable via subclassing — `ToolFilter` on `MemoryChatRequest` is the better extension point | 0.85 | Structural inference from 1710-line file; direct subclassing would require internal access |
| `GitHubCopilotChatProvider` does not require GPU slot scheduling (cloud-based inference) | 0.95 | Provider makes outbound HTTPS calls to Azure; no local VRAM consumed |

---

*End of design document.*
*Next step: user approval → Phase 1 scaffold (separate implementation turn).*
