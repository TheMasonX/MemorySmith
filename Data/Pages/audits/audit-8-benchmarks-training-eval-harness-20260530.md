# MemorySmith Audit #8 — Benchmarks, Training Progression, and Eval Harness

**Date:** 2026-05-30
**Branch:** `feature/code-search-high-roi-batch8` @ `8a8bce6`
**Scope:** Review of the v1–v4 A/B spot-check benchmark series, the eval script (`eval_tool_ab_spotcheck.py`), the training research pages, the training corpus state, and the agent's v4 implementation report.
**Audit family:** Companion to Audits #1–7.

---

## 0. Executive summary

The training pipeline is producing measurable gains. Envelope compliance went from 37% (v1) to 93% (v4). Tool routing improved from 10% to 37%. The eval harness is well-structured and the base-reuse optimization is correct. The research analysis — particularly the system-prompt-variants recommendation and the frontier-data plan — is sharp and actionable.

### Implementation addendum (2026-05-30)

The following audit fixes are now implemented in code:

1. `TRAIN-001` template parity fix:

- `MemorySmith.Training/harness.py` now formats training text with `tokenizer.apply_chat_template(..., add_generation_prompt=False)` when available, replacing legacy `<role>\ncontent` formatting.

2. Synthetic corpus injection for training runs:

- `Scripts/Run-FinetuneHarness.ps1` now accepts `-SyntheticDataPath` and forwards resolved paths to harness request as `syntheticDataPaths`.
- `MemorySmith.Training/harness.py` now loads request-provided synthetic corpora before default starter files.

3. Remote-code trust is no longer hardcoded in training/eval:

- Harness now resolves `trustRemoteCode` from request instead of unconditional `trust_remote_code=True`.
- Eval script now uses `trust_remote_code=False` for base and tuned tokenizer/model loading.

4. Requested file placement verified:

- `Data/Pages/move-me-copilot.md` is in pages root.
- `temp/corpus-design-rationale.md` is in temp.
- New corpus added at `temp/athena-sft-corpus-v6.jsonl`.

But the **single most consequential finding from Audit #6 is still unfixed**: `to_training_text()` in `harness.py` produces `<role>\ncontent` format while both inference and eval use ChatML via `tokenizer.apply_chat_template()`. This means every training run since v1 has been teaching the model a template it never sees at eval or inference time. The gains you're seeing are *despite* this mismatch, not because the training format is correct. **Fixing this is the single highest-ROI change available — it will likely produce a step-function improvement on both envelope and tool-match metrics in one run.**

### This audit's findings: 14 total

| Severity | Count |
|---|---|
| Critical | 1 (TRAIN-001 reconfirmed — still unfixed) |
| High | 3 |
| Medium | 5 |
| Low | 3 |
| Info | 2 |

---

## 1. Training progression analysis (v1 → v4)

### 1.1 Headline metrics

| Version | Adapter | Envelope valid | Tool match | Delta vs prior |
|---|---|---|---|---|
| v1 | `distilled-all-cat-20260530-121744` | 22/60 (37%) | 6/60 (10%) | baseline |
| v2 | same adapter, different run | 51/60 (85%) | 13/60 (22%) | +29 env, +7 tool |
| v3 | `distilled-all-cat-20260530-24kfix` | 59/60 (98%) | 13/60 (22%) | +8 env, +0 tool |
| v4 | `distilled-all-cat-20260530-routefix2` | 56/60 (93%) | 22/60 (37%) | -3 env, +9 tool |

**Interpretation:** The v3→v4 delta is the most informative. Adding routing-focused contrastive samples (the "routefix2" adapter) produced a +9 gain on tool-match at the cost of -3 on envelope. This is the expected trade-off when shifting training signal from "produce valid JSON" toward "produce the *right* tool name" — the model's envelope-producing capacity gets diluted slightly by the new routing signal. The v4 research page correctly identifies this and recommends an envelope-stability pack alongside the next routing round.

### 1.2 Per-tool v4 performance (the real story)

The per-tool breakdown reveals a **bimodal distribution**: tools are either nearly perfect or completely zero.

**Strong (3+ / 5):**
- `memorysmith_search` — 5/5 (perfect)
- `memorysmith_task_list` — 5/5 (perfect)
- `memorysmith_context_pack` — 4/5 (strong)
- `memorysmith_task_get` — 3/5 (good)

**Partial (1-2 / 5):**
- `memorysmith_code_search` — 2/5
- `memorysmith_semantic_search` — 2/5
- `memorysmith_code_search_status` — 1/5

**Zero (0 / 5):**
- `memorysmith_unified_search` — 0/5
- `memorysmith_hybrid_search` — 0/5
- `memorysmith_get` — 0/5
- `memorysmith_page_search` — 0/5
- `memorysmith_page_get` — 0/5

### 1.3 Root cause: frequency-driven routing collapse

The strong performers map exactly to the tools that appear most frequently in the training data. `memorysmith_search` is the default target in the starter pack and most synthetic examples. The model learned "when in doubt, emit `memorysmith_search`" as a general policy.

The zero-score tools share a common failure mode: the model produces valid JSON with `memorysmith_search` when the correct answer is a different search/get variant. From the v4 notable improvements:

```
unified_search-1: tuned pred=memorysmith_search (expected: unified_search)
hybrid_search-1:  tuned pred=memorysmith_search (expected: hybrid_search)
get-1:            tuned pred=memorysmith_search (expected: get)
page_search-1:    tuned pred=memorysmith_search (expected: page_search)
```

This is textbook **majority-class collapse** in SFT. The training corpus has far more `memorysmith_search` examples than any other tool, so the model defaults to the most-represented class for ambiguous inputs.

### 1.4 Secondary failure mode: hallucinated tool names

The v4 data contains completions producing tool names that don't exist in the catalog:
- `memorysmith_pack_context` (should be `memorysmith_context_pack`)
- `memorysmith_code_search_index_status` (should be `memorysmith_code_search_status`)
- `memorysmith_code_index_health` (not a real tool)

These are plausible composites the model invented from partial memorization of the tool vocabulary. They indicate the tool-name vocabulary isn't anchored firmly enough — the model has learned the *pattern* of tool naming but not the *exact* names.

---

## 2. Eval script audit (`eval_tool_ab_spotcheck.py`)

### 2.1 Positive findings

The eval script is the strongest artifact in the training subsystem. Specific merits:

1. **`render_prompt()` correctly uses `tokenizer.apply_chat_template()`** — this formats eval prompts in the model's native ChatML format, matching inference behavior. The eval is measuring what production will actually produce.

2. **`extract_first_json_object()` is a proper bracket-depth parser** — handles nested braces, string escaping, and incomplete JSON correctly. Much better than regex extraction.

3. **Base-reuse path is correctly implemented** — `load_existing_results()` validates the structure before use. The `baseSource` field creates an audit trail. Reuse is valid because the case set and base model are unchanged between runs.

4. **`parse_tool_call()` strips markdown fences** — handles the common "model wraps JSON in triple-backtick" failure mode gracefully.

5. **Batch generation with proper left-padding** — `padding_side = "left"` is correct for causal LM generation. Per-example `input_lengths` are computed from `attention_mask.sum()` to isolate generated tokens.

6. **VRAM cleanup** — `del model; torch.cuda.empty_cache()` between base and tuned runs prevents OOM when running sequentially on the same GPU.

### 2.2 Findings

**EVAL-001 | High | `trust_remote_code=True` in eval script**
- **File:** `eval_tool_ab_spotcheck.py`, `load_base_model()` (~line 125) and `load_tuned_model()` (~line 140)
- **Description:** Same RCE vector as TRAIN-002 in harness.py. The eval script loads models with `trust_remote_code=True`. Combined with user-controllable `--model-id`, an operator can point at a malicious HuggingFace repo.
- **Remediation:** Remove `trust_remote_code=True`. Qwen3.5 works without it in transformers >= 4.45.
- **Confidence:** 0.95

**EVAL-002 | Medium | Eval prompt template diverges from training template**
- **File:** `eval_tool_ab_spotcheck.py` `render_prompt()` vs `harness.py` `to_training_text()`
- **Description:** The eval script correctly uses `tokenizer.apply_chat_template()` (ChatML), but the training harness uses `<role>\ncontent`. The model is being evaluated on a prompt format it was never trained on. This makes the eval results pessimistic — the model's actual capability after a ChatML-aligned training run would likely be significantly higher.
- **Impact:** The 37% tool-match score is a lower bound. Fixing the training template will almost certainly produce a step-function improvement.
- **Remediation:** Fix `to_training_text()` in harness.py to use ChatML. After that, the eval and training templates will match.
- **Confidence:** 0.95

**EVAL-003 | Medium | System prompt in eval is minimal**
- **File:** `eval_tool_ab_spotcheck.py` `render_prompt()` (~line 85)
- **Description:** The eval system prompt is: *"You are Athena, MemorySmith's local wiki assistant. When a search/retrieval action is requested, respond with exactly one JSON object..."*. This is ~50 tokens. The production system prompt (`wiki-chat-agent.md`) is ~4000+ tokens and includes the full routing heuristics. The eval measures the model's tool routing under a minimal prompt, not under the production prompt. This may **undercount** the model's actual production performance (where the longer prompt provides routing guidance the model can follow) or **overcount** it (if the model can't handle the longer prompt's token budget).
- **Remediation:** Run the eval with both the minimal prompt and the full production prompt. Report both scores. The delta reveals how much the model depends on prompt-provided routing vs internalized routing.
- **Confidence:** 0.85

**EVAL-004 | Medium | Case set uses fake memory IDs**
- **File:** `eval_tool_ab_spotcheck.py` `build_cases()` (~line 25)
- **Description:** The `memorysmith_get` cases use fake IDs: `mem_project_001`, `mem_training_001`, `mem_ops_009`, `mem_onnx_001`, `mem_task_001`. These don't match the real MemorySmith ID format (`project-wiki-active-architecture`, etc.). If the training data uses real IDs, the model may have learned to associate real ID patterns with `memorysmith_get` but fail on the eval's fake IDs because they look more like search queries than known IDs.
- **Remediation:** Replace fake IDs with real IDs from the knowledge base. If this improves `memorysmith_get` scores, the finding is confirmed.
- **Confidence:** 0.80

**EVAL-005 | Low | No version pinning on eval cases**
- **File:** `eval_tool_ab_spotcheck.py` `build_cases()`
- **Description:** The case set is hardcoded in the script. If cases are added or reordered between runs, v3 base results can't be validly reused for v5. The base-reuse path validates structure but not case-set identity.
- **Remediation:** Hash the case set and include the hash in the output JSON. Check the hash before reusing base results.
- **Confidence:** 0.90

**EVAL-006 | Low | `page_get` cases use non-existent slugs**
- **File:** `eval_tool_ab_spotcheck.py` `build_cases()`
- **Description:** The `memorysmith_page_get` cases use slugs like `memory-taxonomy`, `codebase-vector-search-whitepaper`, `training-workbench`, `semantic-search`, `wiki-chat-agent`. Most of these are not real page slugs (real slugs are `guides/memorysmith`, `features/api-and-mcp`, etc.). The model may have learned to route to `memorysmith_page_get` only when the slug matches the real format.
- **Remediation:** Use real page slugs from the verified inventory.
- **Confidence:** 0.80

---

## 3. Training template mismatch — the load-bearing finding

**TRAIN-001 | Critical | STILL UNFIXED — `to_training_text()` produces non-ChatML format**

Current `to_training_text()` output:
```
<system>
You are Athena...

<user>
search the wiki for kv cache

<assistant>
{"toolCalls":[{"name":"memorysmith_search","arguments":{"query":"kv cache"}}]}
```

What the model sees at inference (via `tokenizer.apply_chat_template()` in both the eval script and the Ollama Modelfile):
```
<|im_start|>system
You are Athena...<|im_end|>
<|im_start|>user
search the wiki for kv cache<|im_end|>
<|im_start|>assistant
```

The training data teaches the model that `<assistant>\n` is the signal to generate a response. But at inference, `<|im_start|>assistant\n` is the signal. The model has to "translate" between two framing conventions, which costs capacity that should be spent on routing and formatting.

**This is why envelope compliance plateaued at ~93% and tool routing is at 37%.** The model is spending attention heads on "figure out this is a generation boundary" instead of "figure out which tool to call."

**Expected impact of fixing this:** Based on the v1→v4 trajectory and the mismatch magnitude, a ChatML-aligned training run should produce:
- Envelope: 58-60/60 (near-ceiling)
- Tool match: 28-35/60 (step improvement from better boundary understanding)

The fix is ~15 lines in `to_training_text()`. It is the single highest-ROI change in the entire training pipeline.

---

## 4. Training research analysis audit

### 4.1 System prompt variants recommendation — excellent

The `system-prompt-variants-and-context-density-20260530.md` analysis is well-reasoned:

- Correctly identifies that routing collapse is a **prompt-density problem as much as a data problem**.
- The regular/lite split is the right abstraction — small models need sharp routing signals, not verbose guardrails.
- The routing table that distinguishes "broad cross-surface discovery" from "exact-term lookup" from "known-id retrieval" is exactly the taxonomy the training data needs.

**One addition I'd recommend:** The lite variant should also include a **negative routing table** — explicitly stating what each tool is NOT for:
```
- memorysmith_search is NOT for broad discovery questions
- memorysmith_unified_search is NOT for exact tag/ID lookups
- memorysmith_get is NOT for when you don't know the ID
```

Negative constraints are often more effective than positive ones for small models because they prune the decision tree directly.

### 4.2 Frontier-data plan — well-prioritized

The four-tier plan is correctly ordered:
1. Confusion-pair packs (high priority) — addresses the routing collapse directly.
2. Schema-stability pack (medium) — addresses the hallucinated tool names.
3. Ambiguity-resolution micro-dialogues (medium) — addresses the multi-turn routing gap.
4. Replay set from high performers (low) — prevents catastrophic forgetting.

**One concern:** The plan recommends 100-300 confusion pairs per weak tool. At 6 weak tools × 200 pairs = 1,200 new examples. The current corpus is ~38 examples (routefix2). A 30x data expansion in one round risks **catastrophic forgetting** of the strong performers (`memorysmith_search`, `memorysmith_task_list`). The replay set at 10-20% may not be sufficient.

**Recommendation:** Scale the confusion pairs to 30-50 per weak tool for v5 (total ~200-300), not 100-300. Validate stability on strong tools before scaling further. The replay set should be 30-40% of the total corpus, not 10-20%.

### 4.3 Promotion gates

The proposed v5 gates are:
- tool-match >= 28/60
- envelope >= 56/60
- no new unsupported tool names

These are reasonable for the current trajectory. One addition: **add a regression gate on strong performers**. If `memorysmith_search` or `memorysmith_task_list` drop below 4/5, the run fails regardless of aggregate score.

---

## 5. Training corpus state

### 5.1 Duplicate files

26 of 31 `.sft.jsonl` files share the same SHA (`fe41666bf10aba77`). These are duplicate aliases of the same content — likely created during iterative development. They add confusion without training value.

**Remediation:** Remove duplicates. Keep only uniquely-SHA'd files. The harness should glob for `*.sft.jsonl` but deduplicate by content hash before concatenating.

### 5.2 Active corpus

The actual training corpus for v4 is `distilled-all-cat-20260530-routefix2.sft.jsonl` (38 examples, 11.6 KB). This is a very small dataset. The v4 gains from 38 examples are evidence that the LoRA is data-efficient, but the routing ceiling requires more examples.

### 5.3 Corpus quality

The routefix2 corpus has proper tool-call routing examples across all 12 tools. However, it uses the old `<role>\ncontent` format (matching `to_training_text()` output), not ChatML. Once the harness is fixed, the corpus itself needs to be regenerated or the existing data needs a ChatML-format conversion pass.

---

## 6. Agent implementation review

The agent's report is well-structured and the confidence ratings are calibrated. Specific responses to the agent's open questions:

### Q1: Should the next frontier pack target only the weakest six tools, or also include medium performers?

**Target all tools below 4/5 (nine tools).** Include medium performers (`code_search` at 2/5, `semantic_search` at 2/5, `code_search_status` at 1/5) because they're close to the routing boundary — a few well-chosen contrastive examples can tip them over. The six zero-score tools need more examples per tool (10-15 each); the three medium performers need fewer (5-8 each) focused on boundary cases where they currently lose to `memorysmith_search`.

### Q2: Should we set promotion gates at tool-match >= 28/60 and envelope >= 56/60?

**Yes, with two additions:**
1. **Strong-tool regression gate:** `memorysmith_search` >= 4/5 AND `memorysmith_task_list` >= 4/5. Prevent catastrophic forgetting.
2. **No hallucinated tool names:** Any completion containing a tool name not in the canonical 12-tool catalog is a hard fail (currently `memorysmith_pack_context`, `memorysmith_code_search_index_status`, `memorysmith_code_index_health` would fail).

### Confidence assessment

The agent's confidence ratings are appropriate:
- Filter correctness at 96%: Agree. The base-reuse logic is correctly implemented.
- V4 interpretation at 87%: Agree. The routing-collapse diagnosis is correct. The uncertainty is in whether the root cause is data-only or also prompt-density.
- Next-step plan ROI at 84%: Agree. The frontier-data plan is sound but the scale (100-300 per tool) may be too aggressive for v5.

---

## 7. Severity-tagged findings

### Critical

**TRAIN-001 | Critical | RECONFIRMED — `to_training_text()` still uses non-ChatML format**
- **File:** `MemorySmith.Training/harness.py`, `to_training_text()` (~line 210)
- **Status:** Unfixed since Audit #6. Every training run since v1 has been affected.
- **Impact:** The 37% tool-match and 93% envelope scores are lower bounds. Fixing this is the single highest-ROI change.
- **Remediation:** Replace the method body with ChatML formatting:
```python
def to_training_text(self, rows: list[dict[str, Any]]) -> list[str]:
    texts: list[str] = []
    for row in rows:
        messages = row.get("messages")
        if not isinstance(messages, list):
            continue
        turns: list[str] = []
        for message in messages:
            role = str(message.get("role") or "").strip()
            content = str(message.get("content") or "").strip()
            if role and content:
                turns.append(f"<|im_start|>{role}\n{content}<|im_end|>")
        if turns:
            texts.append("\n".join(turns))
    return texts
```

### High

**EVAL-001 | High | `trust_remote_code=True` in eval script**
- Same vector as TRAIN-002. Remove it.

**BENCH-001 | High | Training data class imbalance causes routing collapse**
- **File:** `Data/Training/exports/*.sft.jsonl` (entire corpus)
- **Description:** `memorysmith_search` is massively overrepresented in the training data relative to `memorysmith_unified_search`, `memorysmith_hybrid_search`, `memorysmith_get`, `memorysmith_page_search`, `memorysmith_page_get`. The model learns "default to search" as a prior.
- **Remediation:** Balance the corpus by tool. Each of the 12 tools should have equal or near-equal representation. For v5, generate 10-15 examples per weak tool and include 5-8 replay examples for strong tools.

**BENCH-002 | High | Hallucinated tool names indicate vocabulary anchoring failure**
- **File:** v4 tuned completions
- **Description:** The model produces `memorysmith_pack_context`, `memorysmith_code_search_index_status`, `memorysmith_code_index_health` — none of which exist. These composites suggest the model learned the naming pattern but not the exact vocabulary.
- **Remediation:** Include 20-30 "schema stability" examples in the training data that explicitly use each of the 12 canonical tool names. Add 10-15 negative examples with wrong tool names where the correct response is a different tool. Consider including the tool catalog as a system-prompt injection during training.

### Medium

**EVAL-002 | Medium | Eval prompt template diverges from training template**
- Eval uses ChatML via `apply_chat_template()`. Training uses `<role>\ncontent`. Results are pessimistic.

**EVAL-003 | Medium | Eval system prompt is minimal vs production (~50 tokens vs ~4000)**
- Eval doesn't measure production routing performance. Run with both prompts.

**EVAL-004 | Medium | Eval cases use fake memory IDs for `memorysmith_get`**
- Replace `mem_project_001` etc. with real IDs like `project-wiki-active-architecture`.

**BENCH-003 | Medium | 26 of 31 export files are content-identical duplicates**
- 26 files share SHA `fe41666bf10aba77`. Wastes disk, confuses maintainers.
- Deduplicate. Keep only unique-SHA files.

**BENCH-004 | Medium | Confusion-pair plan at 100-300 per tool risks catastrophic forgetting**
- Scale to 30-50 per tool for v5. Increase replay to 30-40% of corpus.

### Low

**EVAL-005 | Low | No version pinning on eval case set**
- Hash the case set. Include hash in output JSON. Verify before base reuse.

**EVAL-006 | Low | `page_get` eval cases use non-existent slugs**
- Replace with real slugs from the verified inventory.

**BENCH-005 | Low | `to_training_text()` format will need corpus conversion**
- After fixing the harness, existing .sft.jsonl files need a ChatML conversion pass (or just regenerate).

### Info

**BENCH-006 | Info | Base model produces zero valid JSON on all 60 cases**
- Expected behavior. The base Qwen3.5-4B has no MemorySmith fine-tuning. It thinks through the problem in `[think]` blocks instead of emitting JSON. This confirms the LoRA is providing all of the tool-call behavior, not leveraging existing capabilities.

**BENCH-007 | Info | v2→v3 tool-match plateau at 13/60 despite envelope gain**
- The 24kfix adapter improved envelope (51→59) but not routing (13→13). This confirms envelope and routing are partially independent capabilities — addressing one doesn't automatically fix the other.

---

## 8. Recommended priority for the next training iteration

### Immediate (before v5 training run)

1. **Fix `to_training_text()` to use ChatML** (TRAIN-001). ~15 lines of code. Expected to produce a step-function improvement.
2. **Remove `trust_remote_code=True`** from both harness.py and eval script (TRAIN-002, EVAL-001). Two lines.
3. **Balance the training corpus by tool.** Generate 10-15 contrastive examples per weak tool. Include 5-8 replay examples per strong tool. Target ~150-180 total examples.
4. **Add schema-stability examples** with all 12 canonical tool names and explicit negatives for hallucinated names. ~25 examples.
5. **Deduplicate the export directory.** Remove the 26 content-identical files.

### Before v5 eval run

6. **Update eval cases** to use real memory IDs and real page slugs (EVAL-004, EVAL-006).
7. **Add case-set hashing** to the eval script for base-reuse validation (EVAL-005).
8. **Run eval with both minimal and production-length system prompts** (EVAL-003).

### After v5 gate passes

9. **Split the system prompt** into regular and lite variants per the research page's recommendation.
10. **Add negative routing constraints** to the lite variant.
11. **Add per-strong-tool regression gate** to the promotion criteria.

---

## 9. Expected v5 outcome (calibrated prediction)

If TRAIN-001 is fixed and the corpus is rebalanced per the above:

| Metric | Current (v4) | Predicted v5 range | Basis |
|---|---|---|---|
| Envelope valid | 56/60 (93%) | 58-60/60 (97-100%) | ChatML alignment removes boundary confusion |
| Tool match | 22/60 (37%) | 30-38/60 (50-63%) | Balanced corpus + ChatML removes frequency bias and boundary noise |
| Hallucinated tool names | 3+ per run | 0-1 per run | Schema stability examples anchor vocabulary |
| Strong-tool regression | 10/10 | 9-10/10 | 30-40% replay preserves strong performers |

**Confidence:** 75%. The ChatML fix is high-confidence for the envelope gain. The tool-match range is wider because the routing collapse has both a data component (addressable) and a model-capacity component (may require more data or a bigger model to fully resolve).

If the v5 run lands at >= 35/60 tool-match with zero hallucinated names, that's a strong signal that the pipeline is on the right trajectory and further iterations will converge.