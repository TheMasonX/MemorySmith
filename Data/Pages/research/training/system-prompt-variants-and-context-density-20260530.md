# System Prompt Variants and Context Density

## Executive Summary

The current tuning work shows a clear split between two problems:

1. The model now usually emits valid tool-call JSON.
2. The model still confuses tool boundaries, especially broad search vs exact lookup and search vs get/list behavior.

That means the next gain is not more formatting instruction. It is more precise routing guidance with better payload density.

I recommend splitting the chat system prompt into two variants:

- A regular variant for large cloud or local models with enough headroom to carry richer tool-routing guidance.
- A lite variant for small local models where every token should bias toward intent selection, tool boundaries, and response shape.

## Why This Matters

The latest benchmark pattern is consistent:

- Envelope compliance is high.
- Tool-match is still the bottleneck.
- The model repeatedly collapses into `memorysmith_search` when the correct answer is `memorysmith_unified_search`, `memorysmith_hybrid_search`, `memorysmith_semantic_search`, or a known-id `*_get` call.

That is a prompt-density problem as much as a data problem. If the prompt spends tokens on repeated mission statements or broad prose, it leaves less space for the exact routing rules the small model actually needs.

## Recommended Prompt Strategy

### Regular Variant

Use this for larger cloud or local models that can handle a fuller instruction payload.

Keep:

- Mission and instruction priority.
- Trust boundary rules for retrieved data.
- Tool-selection heuristics.
- A short routing table that distinguishes:
  - broad cross-surface discovery
  - conceptually related recall
  - exact-term lookup
  - known-id retrieval
  - page/task/code search and status checks
- A short reminder about concise answers and source citations.

Best fit:

- Cloud-hosted models
- Local models with more stable instruction following
- Higher-context runs where the prompt can afford more guardrails

### Lite Variant

Use this for smaller local models where prompt payload density matters more than exhaustiveness.

Keep only the non-negotiables:

- Instruction priority.
- Untrusted-data boundary.
- Minimal tool-routing rules.
- One-line guidance for when to use each major tool family.
- Short answer style constraints.

Strip or compress:

- Repeated mission framing.
- Long prose about workflow.
- Redundant examples.
- Any instruction that does not directly help choose a tool or shape the output.

Best fit:

- Small local models
- Tight context windows
- Prompt-sensitive runs where the model benefits more from fewer, sharper tokens than from completeness

## Concrete Routing Guidance To Keep

The lite variant should still preserve these distinctions:

- `memorysmith_unified_search` for broad cross-surface questions.
- `memorysmith_hybrid_search` for balanced conceptual discovery.
- `memorysmith_semantic_search` for strong conceptual recall.
- `memorysmith_search` for exact terms, ids, tags, and literal source words.
- `memorysmith_get` / `memorysmith_page_get` / `memorysmith_task_get` for known-id retrieval.
- `memorysmith_page_search` / `memorysmith_task_list` / `memorysmith_code_search` / `memorysmith_code_search_status` for discovery or operational status.

## Next Training Step

The current augmentation batch should be followed by another contrastive pass that explicitly trains:

- broad discovery versus exact lookup on the same topic
- known-id fetch versus search
- page/task get versus page/task list
- code search versus code search status

If possible, add two prompt packs to the source tree:

- `wiki-chat-agent.regular.md` for large-context deployments.
- `wiki-chat-agent.lite.md` for small local deployments.

## Recommendation

Do not try to solve this by only making the single canonical prompt longer. Split the prompt by model class and optimize for payload density:

- regular = fuller guidance, broader model compatibility
- lite = smaller, sharper, fewer words, stronger routing signal

That is the most likely way to maximize effective context usage without bloating the small-model prompt.