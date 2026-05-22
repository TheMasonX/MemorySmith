# Council Review: Phase 4 Chat Context Planner And Native Tool Spike

## Decision

Ship Phase 4 as a deterministic chat context planner plus provider capability metadata and shared chat/MCP tool registry extraction, while documenting native GitHub Copilot tool registration as blocked by the current SDK integration surface.

## Evidence Reviewed

- `Data/Pages/application-next-steps-council-review-20260521.md` Phase 4 goals and acceptance gates.
- `MemorySmith.App/Services/ChatContextPlanner.cs` for preload/tool planning.
- `MemorySmith.App/Services/ChatServices.cs` for provider capability metadata, prompt guidance, tool loop fallback, and trace events.
- `MemorySmith.App/Services/ChatToolCatalog.cs` for shared executable tool definitions.
- `MemorySmith.App/Controllers/McpController.cs` for MCP delegation to the shared catalog.
- `MemorySmith.App/Controllers/ChatController.cs` for provider capability metadata in `/api/chat/config`.
- `MemorySmith.Tests/PagesAndChatTests.cs`, `ChatToolCatalogAndInterceptTests.cs`, and `McpAndSemanticSearchTests.cs` for planner, capability, bounded tool-loop, and shared MCP coverage.
- Validation: focused chat/MCP/tool tests 73/73; `dotnet build MemorySmith.slnx -v minimal`; full NUnit suite 249/249; benchmark smoke across lexical, lexical diagnostics, semantic, hybrid, chat-context, and context-pack paths.

## Findings

| Seat | Recommendation | Confidence | Blocking Concern |
|---|---|---:|---|
| Source-Grounded Archivist | Approve because planner decisions, provider capability limits, and SDK non-support are now explicit in source and tests. | 0.91 | Keep project wiki records updated after merge. |
| Data Model Architect | Approve because runtime capability metadata is additive and does not alter persisted schemas. | 0.88 | `ChatRuntimeConfiguration` JSON shape is additive; UI consumers should tolerate the new field. |
| Retrieval Specialist | Approve because preload is narrower by intent while deterministic intercepts and JSON-text tools remain available for retrieval prompts. | 0.87 | A page-only heuristic could miss mixed-memory evidence; fallback tool prompt mitigates this. |
| Human Learning Advocate | Approve because stream trace now explains why context was loaded or skipped. | 0.86 | Non-stream send paths do not expose trace events yet; capability prompt still carries the plan. |
| Skeptical Reviewer | Approve with caution because native tool registration is documented as unavailable rather than implemented. | 0.82 | Revisit if the GitHub Copilot SDK exposes a stable tool-registration API. |
| Synthesizer | Ship Phase 4 and move to Agent write governance. | 0.89 | None blocking after validation. |

## Synthesis

Phase 4 reduces context bloat by replacing the old preload predicate with a `ChatContextPlanner` that selects no preload, memory preload, page preload, or mixed preload based on intent and configured budgets. It keeps deterministic intent intercepts ahead of preload and keeps JSON-text tool calls as the reliable fallback when provider-native tools are unavailable.

MCP duplication is reduced by routing all shared MemorySmith tools through `ChatToolCatalog`; only `memorysmith_source_bundle` and `memorysmith_find_by_source` remain controller-local because they are MCP-only source-link bridge tools with distinct authorization needs. Provider capabilities are now explicit for streaming, image input, structured response support, context-window reporting, and native tool support.

The GitHub Copilot provider spike found no stable native app-supplied tool registration hook in the current SDK path used here. The provider still supports streaming, image attachments, model listing, and usage/context metadata, so MemorySmith continues to use deterministic JSON-text tool calls and intercepts as the supported tool path.

## Dissent

The Skeptical Reviewer would prefer delaying Phase 4 until native provider tools can be fully registered. The counterargument is that the current implementation improves reliability today by documenting the blocker, keeping fallbacks deterministic, and reducing duplicated tool definitions without waiting on SDK surface area.

## Acceptance Criteria

- Simple direct prompts skip preload and do not leak unrelated wiki records.
- Page-only prompts preload pages without memory noise when budgets allow.
- Retrieval prompts still trigger deterministic intercepts or receive clear JSON-text tool guidance.
- Stream trace includes a context planner event explaining preload/skip decisions.
- Provider capability metadata records native tool non-support and image/stream/context-window capabilities.
- MCP shared tools are listed and executed from `ChatToolCatalog`, with source bundle/find-by-source remaining MCP-local.
- Existing bounded tool-loop behavior remains covered by focused tests.

## Open Questions

- Should Phase 5 expose planner decisions in non-stream chat responses as structured metadata? Current confidence: 70%, useful but not needed for Phase 4 acceptance.
- Should `memorysmith_source_bundle` and `memorysmith_find_by_source` become catalog tools with richer risk classes? Current confidence: 74%, but they need authorization semantics that differ from chat-safe tools.
- Should planner heuristics eventually use measurement-baseline probes instead of regex intent classes? Current confidence: 78%, likely after Phase 6 page chunking/embeddings.
