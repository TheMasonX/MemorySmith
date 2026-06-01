# Request: MCP Authoring Usability And Chat Tool Parity

## Request Summary

Close the gap between MemorySmith's runtime MCP mutation capabilities and what coding/chat agents can practically use, while enforcing prompt/runtime parity for chat tool usage.

## Why This Request Exists

During the 2026-05-29 MCP task backlog pass, task records were created by writing JSON files directly instead of using MemorySmith MCP task tools. That was not because the product lacks task/page mutation tools. The runtime already exposes MCP task and page mutation tools, but this VS Code coding-agent environment does not have those repo-local MemorySmith MCP tools wired in as callable tools.

At the same time, the chat prompt intentionally allows only a smaller intercepted tool subset and routes memory/page writes through proposal JSON. That is valid policy, but the split between:

- runtime MCP tool descriptors,
- chat prompt tool lists,
- agent-mode mutation allowances,
- and external coding-agent tool availability

is large enough that parity and usability drift are easy to introduce.

## Evidence

- `README.md` documents MCP mutation tools including `memorysmith_task_create`, `memorysmith_task_update`, `memorysmith_task_set_status`, `memorysmith_task_add_comment`, `memorysmith_page_save`, and `memorysmith_page_delete`.
- `MemorySmith.App/Services/ChatToolCatalog.cs` exposes those task/page mutation tools with `AvailableInMcp: true`, while chat availability stays off for mutation operations.
- `MemorySmith.App/Controllers/McpController.cs` confirms MCP calls can execute any tool marked `AvailableInMcp` and enabled by config.
- `MemorySmith.Core/Docs/Prompts/wiki-chat-agent.md` limits intercepted chat tools to read-oriented tools and states that task mutation tools are agent-only and memory/page changes must flow through proposal JSON.
- This coding-agent session had no callable MemorySmith MCP task/page tools, so direct file authoring was the only executable path for creating task records from here.

## Recommendations

### 1. Add MCP-backed authoring helpers for agent workflows

- Classification: `Now`
- Impact: high
- Effort: medium
- Confidence: 94%
- Recommendation: provide a first-class bridge so coding agents can create/update tasks and draft page writes through MemorySmith MCP without hand-authoring JSON files.
- Preferred shape:
  - expose the local MemorySmith MCP server to the coding-agent environment as callable tools, or
  - add a thin repo script/helper that submits validated MCP `tools/call` requests to the local app for task/page authoring.

### 2. Make chat tool usage parity a first-class contract

- Classification: `Now`
- Impact: high
- Effort: medium
- Confidence: 92%
- Recommendation: define and test explicit parity rules across `README.md`, `wiki-chat-agent.md`, `ChatToolCatalog.cs`, and any agent capability surfaces so tool availability differences are intentional, documented, and verifiable.
- Minimum parity goal: perfect agreement on what Chat mode, Agent mode, MCP mode, and coding-agent integrations can and cannot call.

### 3. Add authoring-oriented MCP ergonomics

- Classification: `Next`
- Impact: medium-high
- Effort: medium
- Confidence: 89%
- Recommendation: add `dryRun`/`validateOnly`, task-template defaults, id/key allocation hints, and structured envelopes for authoring results so agents can create artifacts without building full JSON by hand.

### 4. Add parity-audit automation

- Classification: `Next`
- Impact: medium-high
- Effort: low-medium
- Confidence: 90%
- Recommendation: add a focused parity verifier that compares documented tool sets and mode flags against the runtime catalog, with special attention to chat tool usage.

### 5. Keep proposal-first memory/page mutation policy explicit

- Classification: `Later`
- Impact: medium
- Effort: low
- Confidence: 88%
- Recommendation: retain the policy distinction where direct task mutation may be agent-capable but memory/page mutation remains proposal-first unless governance explicitly widens it.

## Status

`InProgress`

Active tracking:

- `TSK-0224` (primary parity contract task)

## Follow-Up

- Track execution in `TSK-0223` and `TSK-0224`.
- Fold any resulting implementation into the prompt/runtime parity audit cadence and MCP recommendation backlog.

### 2026-05-30 Self-Review Ordering

- Now (confidence 92%): keep parity requirements explicit across prompt/runtime/docs and continue `TSK-0224` as the contract owner.
- Next (confidence 90%): add scriptable parity drift checks that compare runtime tool catalog mode flags against documented/intercepted tool sets.
- Later (confidence 88%): add coding-agent surface conformance checks once MCP bridge ergonomics from `TSK-0223` stabilize.
