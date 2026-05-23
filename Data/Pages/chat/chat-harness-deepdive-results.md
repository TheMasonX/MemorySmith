Two Deep Dive Research Reports:
[Review 1](https://copilot.microsoft.com/shares/tasks/GVzQRU2JAmfdjp2pvZ15e)

Review 2:
# Chat Harness Enhancements: Context, Compaction, Memory, and Prompting

We recommend treating **context management as the agent’s working memory**【24†L152-L160】. In practice, the harness should allocate tokens to distinct “regions” – e.g. the system prompt, recent chat history, retrieved memory items, tool outputs, and a reserve for the upcoming reply.  Each turn should log a *token budget plan* (slices and reasons) and any truncation event (e.g. why older content was pruned)【52†L93-L95】. This ensures *traceability* of what the model “sees.” For example, one implementation uses a lightweight token counter (via sampling) to track usage without costly full tokenization【52†L93-L95】. By instrumenting these budgets in the trace, we can diagnose when and why context overflow occurs. In short, **context engineering = memory engineering**【24†L152-L160】, so we must explicitly manage *what stays and what goes* in each prompt.

- **Segmentation and Retention:** Reserve a fixed budget for the system prompt and any pinned context (e.g. agent instructions or “memory blocks”).  Keep the freshest user/assistant turns uncompressed, and optionally preserve a *retention window* of the last few exchanges out of compaction【28†L115-L118】【52†L159-L164】. This ensures the model always has the most recent context intact, avoiding abrupt context loss. For example, Forge’s system preserves the latest 6 messages by default during compaction【28†L115-L118】【52†L159-L164】.
- **Budget Reporting:** Emit a trace event each turn showing the token split (system vs history vs memory vs tool outputs vs output reserve). If truncation or compaction is performed, log the trigger (e.g. “token threshold 85% exceeded, auto-compact”). Such diagnostics let developers and users see *why* earlier content was summarized or dropped.

## Context Compaction (Summarization) Strategy

When the conversation grows too large, we fold older parts into a summary.  We suggest initially making compaction **manual** (an explicit `/compact` command) with *strict lineage links* to original turns, then gradually add guarded automation. Key points from related systems:

- **Manual `/compact` Command:** Allow the user (or agent) to invoke a `/compact [optional instructions]` action. This should prompt the LLM to “create a detailed summary of the conversation so far, preserving what was accomplished, current tasks, key decisions, and next steps”【19†L304-L312】【52†L105-L114】.  For example, Forge’s summarizer instructs: “Include completed work, current state of files, in-progress tasks, next steps, constraints, and any critical context”【19†L304-L312】. The harness would then start a *new sub-session* where that summary is injected as hidden context, followed by the user’s pending message. Crucially, **every summary artifact must record its provenance**: keep the IDs of source turns, tools used, and timestamp.  In effect, the summary is an auditable “handoff” note, not a black-box overwrite of history.
- **Summarization Prompt:** Empirically, high-quality compaction uses a focused summarization prompt. The prompt should emphasize continuity (preserve file changes, code state, constraints) and clarity. For instance, one best practice is to say *“This summary will be used by the next assistant.”* in order to guide the model to produce a continuation-ready handoff【16†L124-L133】【52†L105-L114】.  We also add a rule: if the model cannot confidently summarize something (e.g. details were too scattered or truncated), it must state uncertainty (“I don’t have enough info to summarize X”) rather than hallucinate. Studies show explicitly encouraging “I don’t know” responses significantly cuts down confident errors【42†L103-L110】【42†L111-L118】. We will include a clause in the system prompt like “If details are missing or unclear, admit uncertainty rather than invent details” (similar to “Say ‘I don’t know’ if you can’t answer from context”【42†L111-L118】).
- **Automatic Triggers (Phased):** Research suggests **triggers around 85–90%** of the context limit tend to yield better results than waiting until 95%【19†L318-L324】【52†L159-L164】. We recommend starting with auto-compaction **opt-in or behind a feature flag**. When enabled, after each assistant turn check “`if (tokensUsed/contextLimit > 0.85)` then run compaction.”  The compactor should first prune or compress only large tool outputs (as some systems do) before summarizing conversational content【19†L318-L324】. Importantly, implement a user-visible *warning* when auto-compaction runs, as Codex users report it “goes off the rails” when they are mid-task【16†L126-L133】. Likewise, provide a config to disable auto-compaction entirely (following best practices【19†L318-L324】). In early stages, we leave auto-compaction **off by default** until we gather metrics.  

### Design Safeguards

- **Provenance & Reversibility:** Never lose the ability to audit summaries. Each compacted summary should be trace-linked to its source turns. This may mean storing a map of summary→original-turn-IDs and checksums. Users or developers should be able to “expand” a summary chunk to see the raw history it condensed. This aligns with the principle that compaction must be reversible for debugging.
- **Quality Monitoring:** We will measure task performance (especially retrieval tasks) on representative chats before and after compaction. If answers degrade (e.g. key facts vanish), we must adjust thresholds or summarization prompts. The council review warned that multiple compressions cause *cumulative information loss*【16†L126-L133】, so we will track that. Unit tests and chat-based probes will verify that vital information remains discoverable (see “Acceptance Criteria” below).
- **Fallback / Rollback:** The system must be able to recover the pre-compaction state (since the raw turns are still stored). A rollback switch (or disabling compaction) should revert to the old behavior on-the-fly without losing anything.

## Memory and Retrieval (Per-Chat Memory Ledger)

Beyond the immediate context, the harness should support a **separate memory store** for durable facts or notes that persist across turns or sessions. Key ideas:

- **Memory Ledger (Working Memory):** Treat this as user-approved notes. For example, if the user says “Remember that Alice likes jazz music,” we store that in the memory ledger. Unlike raw chat history, these are structured as **key-value notes** or “memory blocks.”  They might come from explicit user commands (“/remember X”) or from verified agent outputs. On each turn, selected memory items (subject to a size cap) are retrieved and inserted into the prompt. This is analogous to the “core memory blocks” concept where pieces like user preferences are pinned in context【24†L177-L185】.
- **External Storage & Retrieval:** We should index older dialogues to keep long chats coherent. A proven pattern is to use a vector+keyword store of past conversation chunks. As one community suggestion notes, *“store older conversations in a database and retrieve them when necessary”* so the assistant can reference them【50†L54-L58】. In practice, this means every few turns we could embed recent segments and save them. Later, during a new turn (or retrieval probe), we query this store (e.g. using BM25+embeddings) for relevant past information. This augmented retrieval “remembers” key topics and names from earlier in the chat【50†L54-L58】【50†L26-L29】. For example, if the user refers to “his earlier project,” the agent can fetch the summary of that discussion from memory. Letta et al. describe this as “recall memory” – a searchable archive of all past interactions outside the active window【24†L185-L193】. We will start by keeping the memory ledger in browser-local session storage (like client-side JSON) and later consider syncing to a server side store.
- **Selective Injection:** We won’t dump the entire memory back into context. Instead, we tag memory entries by topic or priority and inject only a subset per turn (similar to a prioritized memory bank). For instance, facts tagged “in-progress task” or “user preference” get higher weight. We’ll cap the injected memory tokens (e.g. 500 tokens) to fit in the budget. This aligns with “memory blocks” in Letta’s model where each block has a character limit【24†L222-L230】.

## Prompting and Guardrails

To guide the model’s behavior around compaction and memory, we will update system prompts and instructions:

- **Explicit Compaction Clause:** The system prompt will state the compaction policy. E.g.: “If told to compact or when your context is nearly full, summarize the prior chat as directed. If details are missing, state uncertainty.” Citing Lakhera’s guidance【42†L111-L118】, we explicitly authorize the assistant to say “I don’t know” when context is insufficient. For example:
  > “You are a helpful, factual assistant. If you lack enough information from the previous summary or memory to answer a question confidently, reply with ‘I don’t know’ or ‘I need more information’ instead of guessing.”  
  This discourages the model from hallucinating facts from a thin summary【42†L111-L118】.
- **Anti-Hallucination Rules:** Beyond “I don’t know,” we’ll reinforce fact-checking. For example: “Whenever using a compacted summary or memory, cite the source turn or indicate if you’re uncertain.” The harness can then highlight sources in the UI. The idea is to make the model explicitly track where it got each fact (akin to provenance). In practice, we may add tool-assisted citation to original chat messages.
- **Memory Use Instruction:** The prompt will also clarify how to use memory entries. E.g.: “Use known user preferences (from memory) as background context, but only answer based on those and the current dialog. Do not invent personal details.” This guides the LLM to treat the memory ledger as facts, much like retrieved knowledge.

## Agent Tools and Diagnostics

To support these features, we’ll augment the agent toolkit:

- **Context Diagnostics Tools (Read-Only):** For debugging, expose tools like `contextSnapshot` (reports current token usage per region), `listContributors` (lists which turns/memory blocks are in context), and `traceSummaries` (shows the lineage of any compacted summary). These read-only probes help developers audit what was sent to the LLM. For example, one can invoke `!contextBudget` to see “System: 200 tokens, History: 1500 tokens, Memory: 300 tokens, Reserve: 200 tokens (Total=2200/4096)” and any overflows【52†L93-L95】.
- **Memory Tools (Read-Only):** Tools to query the memory ledger (e.g. search for notes by keyword) can help the agent decide whether to use a memory fact. These return curated results but do not write; any writing to memory must be explicitly approved (via user command or a gated action).
- **Compaction Tools (Write-Only):** The `/compact` handler itself is a “tool” that writes a summary. Its output should create a new “message” in history (with a special flag) and purge or hide the old messages. Access to run it programmatically should be guarded (e.g. only when our planner decides the threshold is reached, or the user typed `/compact`).

## Phased Implementation Plan

Based on our synthesis of research and current architecture, we propose a phased rollout:

1. **Phase 1 (Convention-First):**  
   - **Manual Tools & Tracing Only:** Implement `/compact`, budget tracing, and memory ledger as local storage, all behind explicit user actions. Do not alter existing default behavior.  
   - **Prompt Updates:** Update system prompt to include the compaction and uncertainty clauses.  
   - **Testing:** Write unit tests (e.g. in `PagesAndChatTests.cs` and `ChatToolCatalogAndInterceptTests.cs`) covering manual compaction, disabling features, and verifying that `/compact` summarization actually creates a new message with correct lineage. Add retrieval tests ensuring answers from past chat still resolve correctly after compaction.
   - **User Feedback/Early Use:** Expose these features to power users or on dev environments to gather real-case feedback on wording and usability.

2. **Phase 2 (Guarded Auto-Compaction):**  
   - **Threshold Checking:** Enable the auto-trigger at configurable thresholds (start at ~85%). Ensure it’s disabled by default in production.  
   - **Instrumentation:** Collect metrics on how often auto-compaction fires, and how it affects retrieval-of-information and answer quality.  
   - **Roll-Back Switch:** Provide an admin flag to completely disable compaction features if needed.
   
3. **Phase 3 (Optional Schema Upgrade):**  
   - If usage stabilizes, consider adding database schema fields for “CompactionSummary” and “MemoryNote” entries, migrating the in-memory/session storage to a persistent model. But only do this after confirming usage patterns (so we don’t hard-code a wrong structure).

## Tests, Metrics, and Rollout Gates

Before fully enabling auto-compaction, we will enforce clear acceptance criteria:

- **Trace Events:** Every turn should log a `TokenBudget` event showing all slices and any compaction reason. We can assert this in logs.  
- **Manual Compaction Test:** In tests, issuing `/compact` should produce a summary message containing references to original turn IDs. The trace should show that those old turns are marked “summarized” or hidden.  
- **No-Change Baseline:** A simple “echo” dialog (no preloaded context) should behave exactly as before; compaction logic should skip if no history.  
- **Retrieval Accuracy:** We will set up retrieval-centric chat tests (e.g. “recall a fact from 10 turns ago”) and ensure success before vs. after compaction. The system must maintain or improve these scores.  
- **Hallucination Checks:** Introduce test queries where the answer is *only* known from context (not from the model’s training). The model should explicitly say “I’m not sure” if compaction removed the needed info. For example, ask about a detail we had as a user note, and ensure the model either retrieves it or admits ignorance.  
- **Rollback Verified:** Toggling off compaction flags (both manual and auto) should revert to the old behavior seamlessly (no summaries injected, full history preserved). We’ll validate this via unit tests and end-to-end flows.
- **User Experience:** Get feedback on UX elements: the “why compacted” notification, visibility of summaries as special chat chips, etc. As one reviewer noted, hidden compression erodes trust【16†L126-L133】, so we should make compaction explicit in the UI.

## Challenges and Open Questions

- **Schema vs. Flexibility:** Should we hard-code new database fields for summaries and memory notes now, or rely on our JSON traces until patterns solidify? Premature schema changes may box us in. We lean to defer until phase 3.  
- **Trigger Tuning:** Is an absolute token count, a percentage of window, or a combined heuristic (e.g. latency+count) best for triggering compaction? We may experiment to see which yields smoother user experiences.  
- **Compacted Artifacts as Context:** For subsequent turns, do we treat a summary like a normal “assistant” message in history, or tag it specially so that tools (e.g. QA tools) know it’s auto-generated content? This affects whether agents might accidentally double-store summary facts.  
- **Agent Writes vs Summaries:** If the agent proposes a writing action after compaction, how do we avoid it writing a summary artifact as if it’s a grounded fact? We’ll likely need a check to prevent writing back summaries.  
- **Citation Density:** When compaction is enabled automatically, how many source links or context examples should each summary include before it’s “trusted”? We might require each summary to cite at least N distinct turns to ensure it’s well-grounded.

## Summary

In sum, we propose a **staged approach**: first implement explicit compaction and memory features with full traceability, then carefully enable automation once we verify quality. All compaction should be reversible/auditable and include uncertainty-handling clauses to avoid silent errors【16†L126-L133】【42†L103-L110】. With thorough testing and user controls (disable flags, notifications), we can significantly extend chat sessions’ effective scope while keeping the system reliable. 

**Sources:** We draw on state-of-the-art practices in context management and compaction (e.g. Claude, Forge, OpenAI Codex)【19†L318-L324】【52†L105-L114】, memory system design【24†L152-L160】【24†L185-L193】, and prompt engineering to reduce hallucination【42†L111-L118】. These inform our design for MemorySmith’s chat harness upgrades.