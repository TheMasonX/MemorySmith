# Council Review: Chat Harness Capability Uplift (Context Window, Compaction, Memory, Prompting, Tooling)

## Decision
Adopt a staged capability plan: ship convention-first context compaction and per-chat memory summaries with strict traceability gates now, then promote selective parts to schema and automated policies only after retrieval and quality metrics pass.

## Scope
- Mixed impact: retrieval/search behavior, chat context planning, tool-loop behavior, prompt/runtime instruction quality, user-facing chat conventions, and optional schema evolution.

## Evidence Reviewed
- [Data/Pages/search-and-chat.md](search-and-chat.md)
- [Data/Pages/llm-council.md](llm-council.md)
- [Data/Pages/phase4-chat-context-planner-native-tool-council-review-20260522.md](phase4-chat-context-planner-native-tool-council-review-20260522.md)
- [Data/Memories/Core/project-wiki-chat-configuration-current.json](../Memories/Core/project-wiki-chat-configuration-current.json)
- [Data/Memories/Core/project-wiki-chat-agent-provider.json](../Memories/Core/project-wiki-chat-agent-provider.json)
- [Data/Memories/Core/project-wiki-chat-local-storage-persistence.json](../Memories/Core/project-wiki-chat-local-storage-persistence.json)
- [MemorySmith.App/Services/ChatContextPlanner.cs](../../MemorySmith.App/Services/ChatContextPlanner.cs)
- [MemorySmith.App/Services/ChatServices.cs](../../MemorySmith.App/Services/ChatServices.cs)
- [MemorySmith.App/Services/MemorySmithOptions.cs](../../MemorySmith.App/Services/MemorySmithOptions.cs)
- [MemorySmith.App/Controllers/ChatController.cs](../../MemorySmith.App/Controllers/ChatController.cs)
- [MemorySmith.App/Components/Pages/Chat.razor](../../MemorySmith.App/Components/Pages/Chat.razor)
- [MemorySmith.Tests/PagesAndChatTests.cs](../../MemorySmith.Tests/PagesAndChatTests.cs)
- [MemorySmith.Tests/ChatToolCatalogAndInterceptTests.cs](../../MemorySmith.Tests/ChatToolCatalogAndInterceptTests.cs)

## Findings
| Seat | Recommendation | Confidence | Blocking Concern |
|---|---|---:|---|
| Source-Grounded Archivist | Add a compacted-context artifact per turn and optional per-chat memory ledger, but keep every compaction step trace-linked to source turns and tools. | 0.91 | No compaction without reversible provenance (input turn ids, summary model/provider, timestamp, token deltas, and checksum). |
| Data Model Architect | Start convention-first in chat/session storage; defer persistent schema fields until usage patterns stabilize and parser/validator needs are proven. | 0.86 | Premature schema hardening may lock in wrong abstractions for compacted memory chunks and summary lineage. |
| Retrieval Specialist | Introduce token-budget-aware context packing with deterministic budget partitions (system/history/context/tools/attachments/output reserve), plus optional /compact command and auto-compact threshold near context window saturation. | 0.89 | Aggressive summarization can silently degrade recall unless retrieval checks confirm that key facts remain discoverable. |
| Human Learning Advocate | Add explicit UX controls: /compact, "why compacted" explanation, compacted-context chips, and a quick "expand sources" action in Trace/References. | 0.84 | Hidden compression erodes user trust; users must see when and why history was condensed. |
| Skeptical Reviewer | Treat auto-compact as opt-in initially and gate by measurements; require rollback switches for all new behaviors. | 0.79 | Automatic compaction may mask model failures and produce false confidence if quality probes are weak. |
| Synthesizer | Approve phased rollout: instrumentation and manual compact first, then guarded auto-compact, then optional schema promotion. | 0.88 | Need measurable quality gates before default-on automation. |

## Synthesis
### Changes now (Phase A: measurable convention-first)
1. Add a deterministic token budget planner in the chat harness runtime:
   - Keep explicit per-turn budget slices for system prompt, recent history, preloaded context, tool results, attachments, and output reserve.
   - Emit trace events containing budget plan and over-budget reasons.
2. Add manual /compact command in Chat mode:
   - Summarize older turns into a compact context artifact.
   - Preserve provenance links to source turn ids and any tool results folded into the summary.
3. Add per-chat memory ledger (session-local first):
   - Store durable, user-visible "working memory" notes generated only via explicit user action or approved auto-step.
   - Inject ledger selectively into prompt context with size bounds.
4. Upgrade runtime prompt assembly:
   - Keep current capability framing, but add explicit compaction policy clause and anti-hallucination rule for compacted summaries.
   - Require the model to state uncertainty when compacted evidence is insufficient.
5. Expose richer tooling hooks for agents:
   - Add read-only helper tools for context diagnostics (budget snapshot, context contributors, summary lineage).
   - Keep write behavior approval-gated.

### Deferred (Phase B/C: only after validation)
1. Default-on automatic compaction near context window threshold.
2. Persistent schema additions for compacted artifacts and per-chat memory record types.
3. Advanced summarization strategies (hierarchical rolling summaries, semantic dedup, retrieval-time expansion).

## Dissent
- Skeptical Reviewer dissents from enabling automatic compaction by default in early rollout.
- Retrieval Specialist argues for earlier auto-compact if guardrails are strict.
- Resolution: ship manual and instrumentation-first flow, enable auto-compact only behind admin configuration and after quality probes pass.

## Assumptions
- Current planner and tool loop are the right integration points for context budgeting and compaction.
- Existing trace infrastructure can carry compaction provenance without major UI redesign.
- Local storage/session model can safely host a first version of per-chat memory ledger before schema promotion.

## Risks
1. Summary drift: compacted text could omit critical constraints.
2. Tool-loop inflation: compacting at the wrong time could trigger extra tool calls and latency.
3. UX opacity: users may not realize answer quality is affected by compaction.
4. Provider variance: context-window metadata reliability differs across providers.
5. Migration debt: introducing persistent schema too early could require costly backfills.

## Acceptance Criteria
- A budget trace event appears for each turn with token allocation and any truncation/compaction reason.
- Manual /compact produces a compact artifact with source turn lineage and visible references in Trace.
- No-context smoke prompt still skips preload and remains unaffected by compaction behavior.
- Retrieval prompts retain equivalent or improved success on targeted chat retrieval tests after compaction.
- Auto-compact remains disabled by default and is configurable with explicit thresholds and rollback toggle.
- Compacted context can be expanded back to source turns for audit.
- Agent write proposals remain approval-gated and read-only tools remain bounded.

## Validation Gates Before Implementation
1. Unit/integration tests:
   - Extend [MemorySmith.Tests/PagesAndChatTests.cs](../../MemorySmith.Tests/PagesAndChatTests.cs) with compact/manual/auto-threshold scenarios.
   - Extend [MemorySmith.Tests/ChatToolCatalogAndInterceptTests.cs](../../MemorySmith.Tests/ChatToolCatalogAndInterceptTests.cs) for context diagnostic tools.
2. Regression commands:
   - `dotnet test MemorySmith.Tests --filter "FullyQualifiedName~PagesAndChatTests|FullyQualifiedName~ChatToolCatalogAndInterceptTests"`
   - `dotnet test MemorySmith.Tests`
3. Chat quality probes:
   - Exact reply no-context probe.
   - Retrieval-heavy probe requiring at least one tool/intercept and reference chips.
4. Rollback proof:
   - Verify disabling compaction flags restores prior behavior without data loss.

## Open Questions
1. Should per-chat memory ledger stay browser-local first or sync to server-side session storage from day one?
2. Which compaction trigger should lead: absolute token threshold, percentage of context window, or combined latency+token heuristic?
3. Should compact artifacts be treated as regular history messages or as a distinct non-model-authored context type?
4. How should compacted summaries interact with Agent mode proposed writes to avoid writing summary artifacts as facts?
5. What minimum citation density (source links per summary chunk) is required before allowing auto-compact default-on?

## Confidence And Readiness
- Overall confidence in phased recommendation: 87%.
- Readiness for immediate implementation: medium, contingent on acceptance gates above.
