# Plan: Lean Core Memory System Improvements for AI Agents

## 1. Executive Summary
While structured schemas (adding distinct fields for Relations, Constraints, Intents) offer precision, they also introduce significant bloat: complex migrations, cluttered JSON files, and a heavy UI burden. This revised plan outlines how to radically improve AI autonomous agent reasoning within MemorySmith by leveraging **conventions over configuration**, avoiding changes to `memory.schema.json` wherever possible.

## 2. Anti-Bloat Strategy (Conventions > Schema)
Instead of mutating the C# models and JSON schema, we will leverage the existing fields (`Content`, `Tags`, `UsageCount`, `LastUpdated`, `Confidence`) with stricter conventions and smarter processing on the MCP side.

### A. Tag-Driven Semantics (Removing Field Bloat)
Instead of adding new fields for `Priority`, `Intent`, or `ValidUntil`, we overload the existing `Tags` array with structural namespaces.
*   **Priority/Intent:** Use tags like `#invariant`, `#rule`, `#background`. The MCP context packer can apply multiplier weights based on these.
*   **Temporal Decay:** Use `#expires:YYYY-MM` or `#review-after:YYYY-MM`. A background service or agent can filter records possessing these tags.
*   **Relation Typing:** Instead of an elaborate `Relations` graph object, use `#supersedes:[MemoryId]` to hint to the agent why it was linked.

**AI Benefit:** Zero database schema changes. The AI agent understands these hashtag conventions natively, and `MemoryRecord.Tags` remains a simple string array.

### B. Standardized Markdown Content Segregation
Instead of breaking `Content` into `Constraints` and `DetailedContent` JSON nodes, we enforce GitHub Flavored Markdown (GFM) alerts or standard headers within the `Content` string.
```markdown
> [!IMPORTANT]
> (Rule) Never use pure Event Sourcing for UI configuration.

General architectural context goes here without alert blocks.
```
*   **MCP RAG Benefit:** When the `mcp_memorysmithwi_memorysmith_context_pack` tool runs, it can use simple RegEx to extract GFM alert blocks and prepend them as `<StrictRules>` in the AI's prompt, isolating them from general RAG context. No JSON schema changes needed.

### C. Heuristic Temporal Decay (Calculated Relevance)
Rather than manually managing a `ValidUntil` date, we combine existing metadata to compute an implicit "Temperature" or "Staleness" score during semantic search:
*   **Equation:** `Score = BaseSemanticSimilarity * (Confidence) * (f(UsageCount) / f(AgeFromLastUpdated))`
*   Underutilized, old memories naturally drop out of RAG injection.
*   If a memory is tagged `#invariant`, the aging penalty is ignored.

### D. Native Resolution via `Conflicts`
If Record A is in Record B's `Conflicts` array, and Record B has a newer `LastUpdated` or higher `Confidence`, the system implicitly treats Record B as the superseding memory. No need for a custom "ResolvedBy" relationship field.

## 3. Implementation Plan (Lean Approach)

1.  **Phase 1: Convention Enforcements (Days 1-2)**
    *   Update `Data/Memories/Core/` records to adopt GFM alerts (`> [!IMPORTANT]`) for hard rules, rather than narrative prose.
    *   Inject structured tags (`#invariant`, `#supersedes:ID`) where priorities and relations apply.

2.  **Phase 2: RAG Pipeline Overhaul (Days 3-5)**
    *   Modify `MemorySmith.Core/Indexing/` and the MCP context packizer (`mcp_memorysmithwi_memorysmith_context_pack`).
    *   Update the RAG packer to extract GFM alert blocks and format them explicitly as `<Rules>` at the top of the LLM context prompt.
    *   Implement the heuristic decay score in the retrieval scoring pipeline.

3.  **Phase 3: Agent Prompt Tweaks (Day 6)**
    *   Update `.github/copilot-instructions.md` instructing the agent to always use GFM alerts for rules and namespaced tags when creating or updating memory boundaries.

## 4. Assumptions & Open Questions

*   **Assumption:** LLMs parse and respect GFM alert syntax exceptionally well (Confidence: 98%).
*   **Assumption:** Regex extraction of Markdown on the C# side (for separating rules) is robust enough for our payload constraints (Confidence: 90%).
*   **Open Question:** Will manual users adhere to `#namespace:tag` styling, or should the UI automatically convert certain dropdown selections into these tags to prevent typos?
*   **Open Question:** If the heuristic decay formula hides older working memory, does it risk burying uncompleted architectural tasks?

*Confidence Level for Lean Plan Viability:* 95%
