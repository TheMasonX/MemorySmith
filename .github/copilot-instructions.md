# Copilot Instructions

## Project Guidelines
- User prefers NUnit over xUnit for test projects.
- MVVM with minimal code-behind for UI components.
- Treat `MemorySmith.App` as the active single-host app. Legacy `MemorySmith.Worker` and `MemorySmith.Dashboard` code has been removed from the active repository; references to them are historical only.
- Keep `Data/Memories` stable: it is the live project wiki and realistic test fixture source. Tests should copy it to temp storage before mutation.
- Treat `/tasks`, `/tags`, and maintenance proposal workflows as active product surfaces when updating docs or validating behavior.
- Do not stop work because extra changed files are present. Files under `Data/Pages` and `Data/Memories` are expected in normal workflows and should be included with related changes unless the user explicitly asks to exclude them.

## Execution Standards
- Use evidence-backed updates: include file paths, commands, test results, or page references for non-trivial claims.
- Use `/tasks` as the first-class planning artifact for audits and implementation. Audit findings should map to explicit task records and status transitions.
- Use `Scripts/Validate-Repo.ps1` as the default local validation entrypoint; add `-IncludeCoverage`, `-IncludeE2E`, or `-IncludeDocs` when the change scope warrants it.
- Consolidate workflow guidance around `.github/agents/smith.agent.md` first, then backfill related docs/instructions so supporting guidance follows the Smith contract.

## Tool Activation (CRITICAL — Read Before Assuming Tools Are Missing)

MCP tool groups are **dormant until activated**. If you cannot find a tool you expect (e.g., `memorysmith_task_create`), do not assume it is unavailable — call the corresponding `activate_*` tool first:

| If you need... | Call this activation tool |
|----------------|--------------------------|
| `memorysmith_task_create`, `task_get`, `task_list`, `task_set_status`, `task_update`, `task_add_comment` | `activate_memorysmith_task_management` |
| `memorysmith_memory_create` | `activate_memorysmith_task_management` (same group) |
| Wiki page create/update/delete tools | `activate_memorysmith_wiki_management` |
| Source bundle / back-map tools | `activate_memorysmith_source_management` |
| MemorySmith search tools (may already be directly available) | `activate_memorysmith_search_tools` |
| Pylance diagnostics / import analysis / environment tools | `activate_pylance_*` (multiple) |
| Browser interaction tools | `activate_browser_interaction_tools` |
| Network request monitoring | `activate_network_request_tools` |

**Rule:** Before concluding any MCP tool is unavailable, scan your available `activate_*` tools. Their descriptions name the category they unlock. Call the matching one — the tools will appear in your next turn.

## Knowledge Hub - Where to Find Things

The current project map starts at `README.md`, `Data/Memories`, and `MemorySmith.Core/Docs`.

### Docs Directory Map

| Path | Purpose |
|---|---|
| `Data/Memories/Core/` | Active structured project wiki records. Search or read these before planning substantial changes. |
| `Data/Memories/Working/` | In-progress structured memories. Treat as less authoritative than Core. |
| `Data/Memories/Unconsolidated/` | Raw memory inbox. Do not treat as authoritative without verification. |
| `Data/Pages/` | Markdown-backed project wiki pages and longer-form notes. |
| `MemorySmith.Core/Docs/Plans/` | Architecture and implementation plans. `MemorySmith_FinalRefactorDesign_20260507.md` is the current broad refactor blueprint. |
| `MemorySmith.Core/Docs/Reviews/` | Review/audit reports. Useful, but verify against code because older reports can be stale. |
| `MemorySmith.Core/Docs/ProgressReports/` | Historical progress snapshots. Verify against current code before relying on them. |
| `MemorySmith.Core/Docs/Prompts/` | Prompt source of truth used by chat/agent and maintenance workflows. Keep prompt text aligned with runtime capabilities. |
| `Schemas/` | JSON schema and related data contracts. |

### Key Files to Read First
- `README.md` - current product shape, routes, configuration, validation commands.
- `Data/Memories/Core/` - current structured project knowledge.
- `MemorySmith.Core/Docs/Plans/MemorySmith_FinalRefactorDesign_20260507.md` - active broad architecture plan.
- `MemorySmith.Core/Docs/Plans/SemanticSearch.md` - current semantic search/vector update plan when working on semantic retrieval.
- `MemorySmith.Core/Docs/Prompts/wiki-chat-agent.md` - canonical chat/agent system prompt used by the app.
- `MemorySmith.Core/Docs/Prompts/wiki-chat-agent.modelfile` - Athena/Ollama prompt package; keep in sync with the canonical chat prompt.
- `MemorySmith.Core/Docs/Prompts/maintenance-agent-task.md`, `maintenance-proposal-generation.md`, and `maintenance-revision-cycle.md` - maintenance-agent prompt contracts.

### Reviewing Plans and Progress
When reviewing plans or progress reports, cross-reference with the actual codebase to verify that the documented state matches reality.
Plans may be aspirational and not yet fully implemented, while progress reports may be snapshots that have since evolved.
Always check the latest code for the true source of truth.
Always include any relevant code file paths in your notes.
Always note assumptions and open questions that arise during review to facilitate discussion.
Give confidence levels where appropriate.