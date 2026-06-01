# Training Corpus Review - 2026-05-30

## Summary

I reviewed the active distilled corpus and recent A/B evaluation results between base and tuned models.

Key conclusion: the corpus has improved tool-call envelope compliance, but still under-trains **tool routing precision**. In the latest continuation benchmark, tuned model envelope validity reached 85%, yet expected tool match remained 21.7%, with over-selection of `memorysmith_search`.

## Executive Summary

The current corpus is good at getting the model to emit valid tool-call JSON, but it is still not good enough at choosing the right tool. The model keeps collapsing ambiguous routing prompts into `memorysmith_search`, especially when the correct answer should be `memorysmith_unified_search`, `memorysmith_hybrid_search`, `memorysmith_semantic_search`, or a known-id `*_get` call.

I added a focused tool-selection augmentation shard to address that gap, but the latest benchmark still shows routing as the primary remaining problem. The practical next step is more contrastive examples that separate broad discovery from exact lookups and from known-id retrieval.

## Evidence

- Corpus reviewed: `Data/Training/distilled-all-categories-20260529/distilled-all-categories-20260529.sft.jsonl`
- A/B v1 report: `Data/Pages/research/training/tool-ab-spotcheck-20260530.md`
- A/B v2 report: `Data/Pages/research/training/tool-ab-spotcheck-20260530-v2.md`

Observed pattern in v2:

- `memorysmith_search` routes are strong.
- `memorysmith_unified_search`, `memorysmith_hybrid_search`, and `memorysmith_semantic_search` often collapse to `memorysmith_search`.
- `*_get` tools (`memorysmith_get`, `memorysmith_page_get`, `memorysmith_task_get`) are under-selected when user wording is ambiguous.

## Improvements Applied

### 1) Added targeted tool-selection augmentation shard

New file:

- `Data/Training/distilled-all-categories-20260529/distilled-tool-selection-augment-20260530.sft.jsonl`

What it adds:

- Strict JSON-only tool-call turns (no prose/thinking wrappers) for routing-focused examples.
- Contrastive routing coverage for all major read-only tools:
  - `memorysmith_unified_search`
  - `memorysmith_hybrid_search`
  - `memorysmith_semantic_search`
  - `memorysmith_search`
  - `memorysmith_get`
  - `memorysmith_page_search`
  - `memorysmith_page_get`
  - `memorysmith_task_list`
  - `memorysmith_task_get`
  - `memorysmith_code_search`
  - `memorysmith_code_search_status`
- Additional refusal/no-tool examples for unsupported asks (to reduce hallucinated tool calls).

### 2) System-prompt specialization for routing

The augmentation shard uses a stronger tool-selection system prompt that explicitly defines routing boundaries between similar tools. This is intended to reduce:

- `unified_search -> search` collapse
- `hybrid/semantic -> search` collapse
- known-id lookups being handled as broad search

### 3) Added disambiguation mini-set

The shard includes mini contrast examples for:

- exact-term lookup (`memorysmith_search`)
- conceptual recall (`memorysmith_semantic_search`)
- broad cross-surface discovery (`memorysmith_unified_search`)

### 4) Continued refinement after benchmark review

The shard now also includes explicit contrast cases for:

- broad discovery vs exact-term search
- known-memory-id retrieval via `memorysmith_get`
- known-page retrieval via `memorysmith_page_get`
- task lookup via `memorysmith_task_get` and `memorysmith_task_list`

## Additional Recommendations (Next Batch)

1. Add **negative pair training** for tool confusion cases:
   - same user intent phrased two ways, one mapping to specialized tool and one to broad search.
2. Increase multi-turn “repair” examples where first tool call is wrong and corrected on follow-up.
3. Add schema-level lint before export:
   - enforce one-tool-call object only when tool-use is required.
   - reject outputs containing prefaces like `<think>` or prose before JSON.
4. Add weighted sampling by underperforming tools (`hybrid`, `semantic`, `*_get`, `code_search_status`).
5. Introduce a routing score gate in pre-train validation to block corpus updates that regress expected-tool match.

## Confidence

- 92% that routing-focused augmentation will improve tool-match in the next A/B pass.
- 80% that strict prompt wording alone is insufficient without additional contrastive samples (hence this shard).
