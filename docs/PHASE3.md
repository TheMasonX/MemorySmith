# Phase 3 — Internal Delegation (Deferred)

**Status:** NOT IMPLEMENTED in this PR. Phase 3 is explicitly deferred to a separate PR
aligned with the Sub-Agent Architecture design work (see [`docs/Design_AgentAsMCPTool.md`](Design_AgentAsMCPTool.md), 2026-05-31).

## Why Deferred

Phase 3 requires modifying `MemoryChatAgent`'s streaming tool-loop to yield the GPU semaphore
before spawning a sub-agent. Implementing it prematurely — before the sub-agent orchestration
architecture is designed — risks introducing deadlocks in the live tool loop.

## Phase 3 Scope (when implemented)

### 1. Enable Internal Delegation

Set `AvailableInAgent: true` on `memorysmith_agent_invoke` in `ChatToolCatalog.BuildTools()`.
This is the only gating flag. Today the tool is handled only in `McpController`; Phase 3 moves
it into `ChatToolCatalog` as a proper `ChatToolDescriptor` entry accessible from agent-mode loops.

### 2. Yield-Before-Delegate Pattern

In `MemoryChatAgent`'s tool execution loop, before dispatching `memorysmith_agent_invoke`:

```csharp
// Phase 3 change in MemoryChatAgent.ExecuteToolCallAsync:
var ctx = new ChatToolExecutionContext(
    _memories, _pages,
    Transport: "chat",
    CurrentUser: _currentUser,
    Auth: _options.Value.Auth,
    DefaultPageMinimumRole: _options.Value.Pages.DefaultMinimumRole,
    NestingDepth: 0,
    ParentSessionId: null,
    InheritedGpuSlot: _currentGpuSlot);   // ← pass current GPU slot handle
```

In `AgentSessionService.InvokeAsync` (already has the stub):

```csharp
// In AgentInvokeTool.Execute (Phase 3):
if (ctx.InheritedGpuSlot is not null)
    await ctx.InheritedGpuSlot.DisposeAsync();  // yield Athena's GPU slot
```

### 3. NestingDepth Ceiling Enforcement

In `AgentSessionService.CreateSessionAsync` (TODO comment already present):

```csharp
// TODO (Phase 3): enforce MaxNestingDepth ceiling
if (callerNestingDepth >= _options.Value.AgentSession.MaxNestingDepth)
    return CreateSessionResult.Fail("Maximum agent nesting depth reached.");
```

### 4. model_profile_id AllowedRoles Enforcement (F13)

Replace raw `model`/`provider` string parameters with proper `model_profile_id` lookup:

```csharp
// Phase 3: wire ChatModelProfileService
var profile = await _modelProfileService.GetByIdAsync(modelProfileId, ct);
// enforce profile.AllowedRoles contains caller's role
```

### 5. Admin Sessions UI — Parent-Child Chain

Extend `AdminSessions.razor` to show an expandable tree view of parent and child sessions,
using `AgentSession.ParentSessionId` to reconstruct the delegation chain.

### 6. system_prompt_addendum Injection

Add `SystemPromptAddendum` to `MemoryChatRequest`:

```csharp
// Phase 3 addition to MemoryChatRequest:
string? SystemPromptAddendum = null
```

In `MemoryChatAgent.BuildMessages`:

```csharp
if (!string.IsNullOrWhiteSpace(request.SystemPromptAddendum))
    messages.Add(new ChatMessage("system", request.SystemPromptAddendum));
```

## Code Entry Points (TODOs already in codebase)

| File | Symbol | TODO comment |
|------|--------|-------------|
| `AgentSession.cs` | `NestingDepth` field | "TODO (Phase 3): AgentSessionService must enforce MaxNestingDepth ceiling here" |
| `AgentSessionService.cs` | `CreateSessionAsync` | "TODO (Phase 3): enforce MaxNestingDepth ceiling" |
| `AgentSessionService.cs` | `ComputeEffectiveScopeAsync` | "TODO (Phase 3): When AvailableInAgent=true is enabled..." |
| `AgentSessionService.cs` | `CreateSessionAsync` | "NOTE (Phase 2 — F13): model AllowedRoles bypass deferred to Phase 2/3" |
| `ChatToolCatalog.cs` | `ChatToolExecutionContext.InheritedGpuSlot` | "TODO (Phase 3): Populate this in MemoryChatAgent's agent-mode tool loop" |

## Acceptance Criteria (for Phase 3 PR)

- [ ] Internal Athena delegation: `agent.SendAsync` can call `memorysmith_agent_invoke` without deadlock
- [ ] GPU yield confirmed: Athena's slot is released before sub-agent acquires it
- [ ] Recursion blocked: a sub-agent cannot call `memorysmith_agent_invoke` (depth=1, max=1)
- [ ] `model_profile_id` AllowedRoles enforced end-to-end
- [ ] Council review gate passes (special focus: deadlock-freedom under concurrent tool-loop scenarios)
