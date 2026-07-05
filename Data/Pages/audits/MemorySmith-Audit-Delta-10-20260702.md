# MemorySmith Code Audit — Delta Report #10 (2026-07-02, continued)

**Scope of this document:** deltas only, on top of reports #1–#9. First pass into genuinely different territory — Python, not C#. Covered `MemorySmith.Training/harness.py` (1,093 lines), the LoRA fine-tuning script backing TSK-0203 (flagged as an in-progress task all the way back in Report #1, still unverified against source at that point). One clear, well-evidenced bug; one DRY/consolidation note; one brittle-assumption note.

---

## Headline deltas

| # | Finding | Type | Confidence |
|---|---|---|---|
| 1 | **The script's own docstring claims a bug fix that the code doesn't actually implement.** `resolve_hyperparameters`'s docstring lists "warmup_steps default is 10 (not 0)" as one of four deliberate changes fixing "the v7 regression" — but the actual default-value expression still falls back to `0`: `warmup_steps = max(0, min(int(hp.get("warmupSteps") or 0), 100000))`. Every training run that doesn't explicitly pass `warmupSteps` gets zero warmup (immediate cosine decay from step 0), exactly the behavior the docstring says was fixed. | 🔴 New | **93%** |
| 2 | **Boolean-flag coercion logic (`{"1","true","yes","on"}` string matching) is copy-pasted three times** (`include_starter_examples`, `include_transcript_examples`, `resolve_trust_remote_code`), each a near-identical ~8-line method differing only in the field name and default. `resolve_trust_remote_code` is itself called twice from two different points in the file rather than resolved once and passed down. | 🟢 New (consolidation) | **95%** |
| 3 | **LoRA `target_modules` is hardcoded to Llama-family attention projection names** (`q_proj`/`k_proj`/`v_proj`/`o_proj`), which is correct for the script's default and aliased models (all Qwen-family, which follows this convention) — but `resolve_model_id` also accepts an arbitrary raw HuggingFace model ID via an unguarded escape hatch (`if "/" in candidate: return candidate`), for which this hardcoded list would likely cause `get_peft_model` to fail outright on any non-Llama-architecture model, with no validation or warning pointing at the actual cause. | 🟢 New (brittle assumption) | **80%** |

---

## 1. `warmupSteps` default contradicts its own documented fix

**Evidence:**
```python
def resolve_hyperparameters(self) -> dict[str, Any]:
    """Returns a flat dict of all hyperparameters with safe defaults.

    Key changes from upstream:
    - max_train_steps default is 200 (not None/unlimited)
    - gradient_accumulation_steps default is 4 (not 1)
    - warmup_steps default is 10 (not 0)          # ← documented claim
    - learning_rate default is 1e-4 (not 2e-4)
    ...
    """
    ...
    warmup_steps = max(0, min(int(hp.get("warmupSteps") or 0), 100000))   # ← actual fallback is 0
```
Note also that the docstring's *other* claimed default doesn't match either, in a smaller way worth flagging while I'm here: it says "max_train_steps default is 200," but the actual fallback later in the same method is `max_train_steps = 75  # Fixed budget — keeps wall-clock bounded (default set to 75)` — a *second* stale docstring claim, this one at least self-correctly commented at the point of actual use, but still leaving the class-level docstring wrong. Two of the four "key changes from upstream" claims listed in this docstring don't match the code beneath them.

**Why this matters beyond "a comment is wrong":** this docstring isn't incidental documentation — it's the changelog for a rewrite whose entire stated purpose is fixing specific, named regressions ("Fixes four root causes of the v7 regression," per the module-level docstring at the top of the file). If `warmupSteps` silently defaulting to 0 was genuinely one of the four root causes of the v7 regression, this specific fix never actually shipped — every default-configuration training run still gets the old, apparently-problematic zero-warmup behavior, while every log line, status event, and human or agent reading this code has good reason to believe (from the docstring) that it's getting a 10-step warmup. This is exactly the kind of "the fix is documented as done but isn't" gap that's easy to miss precisely because the comment reads as confirmation rather than a claim needing verification — which is why I checked the actual expression rather than trusting the docstring.

**Consequence if a caller doesn't already know to pass `warmupSteps` explicitly:** immediate cosine-decay learning-rate schedule from step 0 (no ramp-up), rather than the linear-warmup-then-cosine-decay the scheduler code is otherwise built to do (`if step < warmup: ... else: progress = cosine(...)` — with `warmup=0`, every step immediately takes the `else` branch). This is a real training-quality concern for LoRA fine-tuning (abrupt high-LR start is a documented cause of training instability, which is plausibly related to whatever "v7 regression" symptom prompted this rewrite in the first place), not just a cosmetic doc issue.

**Recommendation:** Change the fallback to match the documented intent: `warmup_steps = max(0, min(int(hp.get("warmupSteps") if hp.get("warmupSteps") is not None else 10), 100000))` (using explicit `is not None` rather than truthy-`or`, which also matters here — see the note below). Separately, fix the `max_train_steps` docstring claim (200) to match the actual code default (75), or vice versa if 200 was the intended value and `75` is itself the bug — I can't tell from the code alone which of the two numbers is the "real" intended default versus a later change that didn't get the docstring updated; flagging as an open question rather than guessing.

**A related, smaller correctness note while reading this line:** the pattern `hp.get("warmupSteps") or 0` (and the same `or`-based fallback pattern used for several other hyperparameters in this method: `hp.get("epochs") or 1`, `hp.get("learningRate") or 1e-4`, etc.) treats an explicit `0` the same as "not provided." For most of these fields that's harmless (nobody legitimately wants `epochs=0`), but for `warmupSteps` specifically, `0` is a perfectly valid, meaningful, *intentionally chosen* value (an operator who deliberately wants no warmup) — and this `or`-based pattern makes it indistinguishable from "the field was omitted," silently overriding an explicit `0` with whatever the fallback is. Once Finding 1's fallback is corrected to `10`, this distinction starts to matter: an operator who explicitly sets `warmupSteps: 0` to disable warmup on purpose would silently get `10` instead, because `0 or 10` evaluates to `10` in Python. Worth using `hp.get("warmupSteps") if hp.get("warmupSteps") is not None else 10` rather than the `or` shorthand once the default itself is fixed.

**Confidence: 93%** — the docstring-vs-code mismatch is a direct textual comparison, unambiguous. The training-quality impact (immediate-decay-from-step-0 being meaningfully worse than a short warmup) is a well-established practice in LLM fine-tuning generally, though I don't have this specific project's empirical loss curves to confirm it's actually the mechanism behind whatever "v7 regression" motivated this rewrite — that's inference from the surrounding context (the docstring explicitly ties this default to fixing that regression), not something I can verify without access to prior training run logs.

---

## 2. Boolean-coercion helper duplicated three times

**Evidence:**
```python
def include_starter_examples(self) -> bool:
    value = self.request.get("includeStarterExamples")
    if value is None: return False
    if isinstance(value, bool): return value
    if isinstance(value, str): return value.strip().lower() in {"1", "true", "yes", "on"}
    return bool(value)

def include_transcript_examples(self) -> bool:
    value = self.request.get("includeTranscriptExamples")
    if value is None: return False
    if isinstance(value, bool): return value
    if isinstance(value, str): return value.strip().lower() in {"1", "true", "yes", "on"}
    return bool(value)

def resolve_trust_remote_code(self) -> bool:
    raw = self.request.get("trustRemoteCode", False)
    if isinstance(raw, bool): return raw
    if isinstance(raw, str):
        normalized = raw.strip().lower()
        return normalized in {"1", "true", "yes", "on"}
    return bool(raw)
```
Three copies of the same `{"1","true","yes","on"}` truthy-string-parsing logic (plus at least two more inline equivalents inside `resolve_hyperparameters` for `shuffleEachEpoch`, `loadIn4Bit`, and `gradientCheckpointing` — five occurrences of the same pattern total in this one file). `resolve_trust_remote_code` is also called twice (once for tokenizer loading, once for model loading, ~400 lines apart) rather than resolved once into a local variable and threaded through — harmless today since `self.request` doesn't change mid-call, but it's needless re-computation and a small future footgun if the two call sites' request-derived state ever diverges.

**Recommendation:** Extract a single `_coerce_bool(value: Any, default: bool = False) -> bool` module-level function and route all five+ call sites through it. Purely mechanical, no behavior change if done carefully (worth double-checking the two `include_*` methods' `None → False` short-circuit matches the shared helper's intended default exactly, and that `resolve_trust_remote_code`'s `self.request.get(key, False)` — a different `.get` signature than the others' `self.request.get(key)` then `is None` check — collapses cleanly into the same helper without altering behavior for the `False`-vs-`None` distinction, which happen to behave identically here but are worth confirming explicitly during the refactor rather than assumed).

**Confidence: 95%** — pure duplication observation, directly quoted from source.

---

## 3. Hardcoded LoRA target modules assume Llama-family architecture

**Evidence:**
```python
lora_config = LoraConfig(
    r=hp["loraRank"],
    lora_alpha=hp["loraAlpha"],
    lora_dropout=0.05,
    target_modules=["q_proj", "k_proj", "v_proj", "o_proj"],
    bias="none",
    task_type="CAUSAL_LM",
)
```
`resolve_model_id` defaults to `"Qwen/Qwen3.5-4B"` and its only other named aliases are also Qwen-family (`qwen3.5`/`qwen3` prefixes) — all of which use this exact projection-layer naming convention, so the hardcoded list is correct for every model this script is *designed* to be pointed at. But the same method has an unguarded escape hatch:
```python
if "/" in candidate:
    return candidate   # any raw HuggingFace model ID, unvalidated
```
Point this at an architecture with different internal layer names (GPT-2/NeoX-style `c_attn`, Phi's `Wqkv`, etc.) and `get_peft_model` would almost certainly raise at LoRA-attachment time with no upstream check or friendlier error message pointing at "your model's architecture doesn't match the hardcoded target_modules list" as the actual cause — a user would just see a PEFT library exception deep in a stack trace.

**Why this is low-urgency rather than a real current bug:** given this project's actual usage pattern (a home-lab setup fine-tuning models for a specific local-agent use case, per the wider context established across this whole audit), it's plausible nobody ever exercises the raw-model-ID escape hatch with a non-Qwen model in practice — this is a "brittle if used outside its apparent intended envelope" finding, not "currently broken for its actual use case."

**Recommendation:** Either remove the raw-model-ID escape hatch if it's not actually meant to be used (forcing all model selection through the validated alias list), or add a lightweight architecture check/mapping (a small dict from architecture family → appropriate `target_modules`, with a clear error if the loaded model's architecture isn't in the map) so a mismatch fails with an informative message instead of a raw PEFT stack trace.

**Confidence: 80%** — the code-level mismatch is directly confirmed; the "how likely is this to ever matter" assessment is inference about actual usage patterns I can't verify from the repo alone.

---

## 4. Coverage note

This pass covered `harness.py` (1,093 lines) in full. Not yet covered: `docs/build_pages_site.py` (611 lines) and the dozen smaller `Scripts/*.py` utilities (eval/spotcheck/corpus-consolidation tooling, ~1,700 lines combined), the PowerShell release/deployment scripts (23 `.ps1` files), and the remaining ~2,000 unread lines of `CodeSearchService.cs` first flagged as outstanding after Report #2.
