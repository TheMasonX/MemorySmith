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

## V4 Deep-Dive (B-Only Rerun)

To avoid wasting time on an unchanged base model, the benchmark runner now supports reusing prior base rows while evaluating only the tuned adapter.

Run shape:

- Base source reused from v3 results.
- Tuned side evaluated on adapter `distilled-all-cat-20260530-routefix2`.
- Output: `tool-ab-spotcheck-20260530-v4.data.json` and `tool-ab-spotcheck-20260530-v4.md`.

Headline deltas versus v3:

- Tuned tool match improved from `13/60` to `22/60` (+9 absolute).
- Tuned envelope validity decreased from `59/60` to `56/60` (-3 absolute).

Interpretation:

- Routing improved materially, so the added contrastive samples are helping.
- Minor envelope regression suggests we should add a small envelope-stability pack while continuing routing work.

Per-tool result summary (tuned v4):

- Strong:
  - `memorysmith_search` 5/5
  - `memorysmith_task_list` 5/5
  - `memorysmith_context_pack` 4/5
  - `memorysmith_task_get` 3/5
- Partial:
  - `memorysmith_code_search` 2/5
  - `memorysmith_semantic_search` 2/5
  - `memorysmith_code_search_status` 1/5
- Still weak:
  - `memorysmith_unified_search` 0/5
  - `memorysmith_hybrid_search` 0/5
  - `memorysmith_get` 0/5
  - `memorysmith_page_search` 0/5
  - `memorysmith_page_get` 0/5

Primary remaining failure mode:

- Search-family collapse into `memorysmith_search` still dominates broad/conceptual prompts.

Secondary failure mode:

- Occasional invalid/unsupported tool names in completions (for example `memorysmith_pack_context`) indicate schema drift from target catalog.

## Frontier-Model Data Plan

If generation capacity is effectively unlimited, the best ROI is a targeted hard-negative and boundary-contrast pipeline, not generic bulk expansion.

Recommended data production plan:

1. Build confusion-pair packs (high priority)

- For each weak tool, generate 100-300 pairs where prompts differ by one intent cue:
  - broad wiki discovery vs exact lexical lookup
  - conceptual recall vs exact term search
  - known-id retrieval vs search/list
  - code-status check vs code-content lookup

2. Add schema-stability pack (medium priority)

- 150-300 examples that enforce exact tool-name vocabulary and one-object envelope shape.
- Include explicit negatives that demonstrate wrong names are invalid.

3. Add ambiguity-resolution micro-dialogues (medium priority)

- 100-200 two-turn samples where first intent is ambiguous and second turn resolves to get/list/search.

4. Keep a small replay set from high performers (low priority)

- Preserve `memorysmith_search` and `memorysmith_task_list` performance by replaying 10-20% stable positives each round.

## Prompt Variant Rollout Proposal

To maximize context payload density while preserving quality, move to an explicit two-variant contract:

- Regular: full guardrails, richer explanations, cloud/large local models.
- Lite: compressed routing table + strict output contract, small local models.

Operationally:

- Keep shared invariant sections (priority, trust boundary, JSON contract).
- Swap only the routing/detail sections by model profile.
- Track A/B separately per variant, because a single merged prompt can hide small-model regressions.

## Immediate Next Steps

1. Produce a `weak-tools-v5` dataset focused on:

- `unified`, `hybrid`, `get`, `page_search`, `page_get`, `code_search_status`.

2. Run one epoch retrain with the same optimizer settings used in v4.

3. Execute B-only benchmark again (reuse v3 base) and gate on:

- tool-match >= 28/60
- envelope >= 56/60
- no new unsupported tool names.

4. If the gate passes, publish v5 report and freeze that corpus slice as a baseline branch point.