# MemorySmith Audit #6 — Training Harness, MCP Changes, and Branch State

**Date:** 2026-05-29
**Branch:** `feature/code-search-high-roi-batch8` @ `abd614f6a910fd1091c224f62f3a8ae53b75a80f`
**Base:** master @ `c4d7a28ade1a2878d270f1479bfb255f5058482b`
**Scope:** All new code on the feature branch (~43 commits since master). Focus on the training harness, training workbench UI, MCP tool changes, and integration correctness.
**Audit family:** Companion to Audits #1–5. Cross-references inlined.

---

## 0. Executive summary

The branch introduces a functional training harness and workbench — a real, running pipeline from Blazor UI → C# process manager → Python PEFT LoRA loop → GPU. The MCP surface gains three new tools. The `num_ctx` bug from Audit #5 is **fixed**. The Modelfile has been migrated to ChatML (Path B, as recommended).

However, the training pipeline has **two Critical-severity findings** that will cause silent quality degradation or create a remote code execution surface. The most consequential: `harness.py` formats training data using a bare `<role>\ncontent` format while the Modelfile and Ollama inference path use ChatML (`<|im_start|>role\ncontent<|im_end|>`). Every training run executed under the current code fine-tunes the model on a template it never sees at inference time. This is the single highest-priority fix in the entire audit.

### Severity rollup

| Severity | Count | Net-new vs prior audits |
|---|---|---|
| Critical | 2 | 2 new |
| High | 5 | 4 new, 1 carried (TRAIN-005 = new pattern of Audit #5 configurability gap) |
| Medium | 8 | 7 new, 1 reconfirmed (MCP protocol version) |
| Low | 3 | 3 new |
| Info | 3 | 3 new |
| **Total** | **21** | **19 net-new** |

### Closures from prior audits

| Prior finding | Status | How |
|---|---|---|
| `num_ctx` display-only bug (Audit #5 §4.4) | **Closed** | `BuildOllamaRequestOptions()` now sends `num_ctx` when `OllamaContextWindowTokens > 0` |
| Chat template drift hazard (Audit #5 §7.1) | **Partially closed** | Modelfile uses ChatML. But `harness.py` does NOT use ChatML for training data — see TRAIN-001 |
| Zero server-side chat logging (Audit #5 §4.6) | **Closed** | `ChatTranscriptWriter` added with metadata-only default + optional content companion |
| Zero feedback mechanism (Audit #5 §4.7) | **Closed** | `ChatFeedbackStore` added (SQLite-backed, thumbs up/down) |

---

## 1. Branch overview and commit log

**Default branch:** `master` (protected, still at `c4d7a28a`)
**Active feature branch:** `feature/code-search-high-roi-batch8` — 43+ commits, last commit 2026-05-29 17:09 UTC

### Key commits (newest first)

| Date | SHA prefix | Message | Audit relevance |
|---|---|---|---|
| 05-29 17:09 | `abd614f6` | Record API key test harness requirement | Config |
| 05-29 16:57 | `b7e76c60` | UI: admin refresh, training workbench nav shortcut removal, distillation task page | UX |
| 05-29 16:09 | `e458f3f3` | Training: fix workbench harness path resolution | Bug fix |
| 05-29 15:41 | `a5fa5941` | Docs: fix ultra codebase audit markdown structure | Docs |
| 05-29 15:40 | `aca7dcae` | Skills: council rename, core inheritance, hooks, request pages | Feature |
| 05-29 15:13 | `b313fb59` | Training: persist HF auth context in run status artifacts | Security |
| 05-29 15:11 | `2e54e207` | Training: surface HF auth presence in active run status | Telemetry |
| 05-29 15:09 | `d2a49cb4` | Training: show HF auth in UI run launch | UX |
| 05-29 15:08 | `09c81607` | Training: wire optional HF token env for UI harness runs | Security |
| 05-29 15:05 | `b14a5396` | Training: support optional HF token in harness runner | Security |
| 05-29 15:03 | `34a1d8e0` | Training: surface train progress in final status metrics | Telemetry |
| 05-29 15:00 | `b97fdb76` | Training: add completedEpochs to LoRA telemetry | Telemetry |
| 05-29 14:55 | `207f2e89` | Training: replace deprecated torch_dtype with dtype | Compat |
| 05-29 14:51 | `93052596` | Training: enable multi-epoch loss telemetry and configurable step caps | Feature |
| 05-28 05:03 | `3502bf93` | Harden code search embedding failure handling | Reliability |
| 05-28 04:59 | `5793893d` | Resolve PR review findings, add vector search whitepaper notes | Cleanup |
| 05-28 04:04 | `ce2bdf4e` | Fix: catch ArgumentException in slug normalization | Bug fix |
| 05-28 03:57 | `1d751e6c` | Add code search batch embedding benchmarks | Perf |
| 05-28 02:55 | `4d414141` | Reduce semantic prewarm startup log noise | UX |
| 05-28 02:54 | `7f3a43b3` | Add semantic prewarm and code search timing telemetry | Observability |
| 05-27 23:04 | `961f69fa` | Add configurable ONNX execution providers | Feature |
| 05-27 19:10 | `826d7324` | Fix code search cache invalidation | Bug fix |
| 05-27 19:07 | `27d5e812` | Harden code search indexing workflow | Reliability |
| 05-27 17:01 | `2840bd23` | Document memorysmith.home.arpa MCP alias setup | Docs |
| 05-27 13:43 | `f8f5fccf` | Close PR43 follow-up and backlog slice | Cleanup |
| 05-26 21:56 | `58f983f0` | Audit untracked architecture gaps | Docs |
| 05-26 20:46 | `3c756252` | Checkpoint UI sweep and repo updates | UX |
| 05-26 19:06 | `8cf4aaca` | TSK-0130 rebalance pages narrow layout | UX |

---

## 2. New file inventory

### Training subsystem (entirely new)

| File | Size | Purpose |
|---|---|---|
| `MemorySmith.Training/harness.py` | ~400 lines | Python training orchestrator: data loading, LoRA training, inference comparison, telemetry |
| `MemorySmith.Training/synthetic/starter_sft.jsonl` | 2.5 KB | Synthetic starter SFT examples |
| `MemorySmith.Training/synthetic/starter_sft.expanded.jsonl` | 6.6 KB | Expanded synthetic SFT examples |
| `MemorySmith.App/Components/Pages/TrainingWorkbench.razor` | ~800 lines | Blazor admin page at `/training-workbench` |
| `MemorySmith.App/Services/TrainingOptions.cs` | ~120 lines | Configuration block bound at `MemorySmith:Training` |
| `MemorySmith.App/Services/Training/TrainingHarnessRunnerService.cs` | ~350 lines | C# process manager: spawn, probe deps, timeout/kill |
| `MemorySmith.App/Services/Training/TrainingPathResolver.cs` | ~150 lines | Project-root and venv path resolution |
| `MemorySmith.App/Services/Training/ChatTranscriptWriter.cs` | ~200 lines | JSONL transcript persistence with redaction |
| `MemorySmith.App/Services/Training/ChatTurnRecord.cs` | ~80 lines | Turn record DTOs |
| `MemorySmith.App/Services/Training/ChatFeedbackStore.cs` | ~180 lines | SQLite-backed thumbs feedback |
| `Data/Training/exports/` | directory | Export target (`.gitkeep` only) |

### Changed files (substantive modifications)

| File | What changed |
|---|---|
| `MemorySmith.App/Services/MemorySmithOptions.cs` | Added `TrainingOptions Training` property |
| `MemorySmith.App/Services/ChatToolCatalog.cs` | 3 new tools: `code_search_merge_shard`, `page_save`, `page_delete` |
| `MemorySmith.App/Services/ChatServices.cs` | `BuildOllamaRequestOptions()` sends `num_ctx`; transcript writer integration |
| `MemorySmith.Core/Docs/Prompts/wiki-chat-agent.modelfile` | ChatML template migration, sampling parameter tuning |
| `MemorySmith.App/Controllers/McpController.cs` | Protocol version `2025-06-18` |

---

## 3. Training harness deep audit (`harness.py`)

### 3.1 Architecture

The harness is a single Python script that:
1. Reads `request.json` from a work directory (written by the C# side).
2. Loads chat examples from transcript JSONL files + synthetic starters.
3. Attempts real LoRA training via PEFT/transformers.
4. Falls back to "simulated" training if PEFT or CUDA fails.
5. Runs inference comparison (base vs fine-tuned) on eval prompts.
6. Writes `status.json` atomically via rename.
7. Emits JSON event lines on stdout for the C# wrapper to consume.

### 3.2 Training data format — THE CRITICAL BUG

**Finding TRAIN-001** (see § 7). The `to_training_text()` function formats messages as:

```
system
<content>

user
<content>

assistant
<content>
```

But the Modelfile uses ChatML:

```
<|im_start|>system
<content><|im_end|>
<|im_start|>user
<content><|im_end|>
<|im_start|>assistant
```

This means every training run teaches the model to produce outputs wrapped in a format it will never be prompted with at inference time. The model learns to emit text after `assistant\n` when at inference time it will be asked to emit text after `<|im_start|>assistant\n`. The mismatch causes:
- Degraded instruction following (model "forgets" the ChatML boundary tokens).
- Increased hallucination rate (model has weaker boundary understanding).
- Tool-call discipline regression (the JSON envelope boundary is blurred by the wrong template).

**This is the single most impactful fix available.** Remediation: use `tokenizer.apply_chat_template()` to format training data, or manually construct ChatML sequences matching the Modelfile's `TEMPLATE` directive.

### 3.3 LoRA configuration

| Parameter | Value | Assessment |
|---|---|---|
| rank | 8 | Conservative for 4B model. Rank 16 would give more capacity without significant VRAM cost. |
| alpha | 16 | Alpha/rank ratio = 2. Standard. |
| dropout | 0.05 | Fine for >100 examples; negligible effect under 50. |
| target_modules | `q_proj k_proj v_proj o_proj` | Standard attention targets. Missing MLP projections (`gate_proj`, `up_proj`, `down_proj`) which would improve quality for tool-call and formatting behaviors. |
| bias | "none" | Correct. |
| task_type | "CAUSAL_LM" | Correct. |

### 3.4 CUDA and VRAM handling

No pre-flight VRAM check. No gradient accumulation. Model is loaded in bf16 (or float32 fallback), moved to CUDA with `.to(training_device)`. For Qwen3.5-4B:
- bf16 weights: ~8 GB
- LoRA adapters (r=8): ~30 MB
- Optimizer state (AdamW): ~120 MB
- Activations at seq_len=1024: ~2-3 GB with no gradient checkpointing

**Total: ~10-11 GB minimum.** This will not fit on the 8 GB RTX 5060 without QLoRA (4-bit base). The code does not use `BitsAndBytesConfig` or Unsloth's 4-bit loading. **The current harness will OOM on the target hardware.**

### 3.5 Hyperparameter clamping

The harness clamps:
- `epochs` to [1, 3]
- `sequence_length` to [128, 1024]
- `learning_rate` to [1e-6, 5e-3]
- `max_train_steps` to [1, 256]

These are safe ranges. The sequence length cap at 1024 is conservative — it prevents VRAM blowout but limits the model's ability to learn long-context behaviors. Consider raising to 2048 once QLoRA is implemented.

### 3.6 Eval gate

The minimum data threshold is `records >= 2`. With one synthetic starter and one logged transcript, training "passes" on 2 examples. This is meaningless — the model learns nothing useful from 2 examples but the harness reports success.

### 3.7 Model ID resolution

The `resolve_model_id()` function maps Ollama tags to HuggingFace paths:
- `qwen3.5` → `Qwen/Qwen3.5-4B` (hardcoded)
- `qwen3` → `Qwen/Qwen3-4B` (hardcoded)
- Everything else → passed through verbatim

Combined with `trust_remote_code=True`, this is a remote code execution vector (see TRAIN-002).

### 3.8 HuggingFace token handling

Recent commits (`09c81607`, `b14a5396`, `2e54e207`, `b313fb59`) wire the `HF_TOKEN` environment variable from `TrainingOptions.HuggingFaceTokenEnvironmentVariable`. The token is:
- Read from the OS environment by the C# side.
- Passed into the Python process environment.
- **Persisted in run status artifacts** (`b313fb59`).

This last point is a concern: if `status.json` or events.jsonl is ever exposed (e.g., via the diagnostics endpoint), the HF token leaks. The existing `TranscriptRedactionEnabled` pattern should be extended to status artifacts.

---

## 4. Training workbench UI audit (`TrainingWorkbench.razor`)

### 4.1 Architecture

- **Route:** `/training-workbench`
- **Auth:** `[Authorize(Policy = MemorySmithPolicies.CanAdminMemorySmith)]` — admin-only. Correct.
- **Pattern:** Blazor Server with 2-second `PeriodicTimer` polling for run status.
- **Features:** Run list with status, start new run, dependency probe, export list, settings editor with import/export, artifact browser.

### 4.2 Positive findings

- Admin-only authorization is correctly applied.
- Settings import validates against known keys (unknown keys silently ignored, not injected).
- Artifact browsing constrains allowed paths via `GetTrainingArtifactRoots()`.
- Process spawn uses `UseShellExecute = false` — no shell injection.
- Status file reads handle parse errors gracefully (returns null, surfaces warning).
- CSRF is inherently mitigated by Blazor Server's SignalR transport.

### 4.3 Concerns

- **Polling overhead (TRAIN-015):** 2-second poll fires continuously. Each poll re-scans up to 40 run directories. When idle, this burns I/O for no value.
- **CancellationToken.None in StartRunInternalAsync (TRAIN-008):** Navigation away doesn't cancel the launch. The training run itself is fire-and-forget by design, but the launch sequence (dependency probe, request.json write) should respect a scoped token.
- **No way to cancel a running job from the UI:** The harness has no cancellation protocol. The C# side can kill the process, but there's no "Cancel run" button wired up.
- **Process argument construction inconsistency (TRAIN-007):** Probe uses safe `ArgumentList` API; run uses manual `Quote()` + string join. Both have `UseShellExecute = false` so the risk is low, but the inconsistency invites bugs.

### 4.4 UX observations

- **No context-window picker** in the chat UI. The design's § 1 dropdown (2K/4K/8K/16K/24K/32K/Custom) has not been implemented.
- **No VRAM heuristic** displayed before starting a run. The user has no idea if the run will fit on their GPU.
- **No estimated training time** displayed. The design's § 11 cost/time estimates are not surfaced.
- **No model promotion UI.** The workbench shows runs and their status but there is no "Promote to active" button wired through `appsettings.json` mutation.
- **No eval score display** in the run summary. The eval gate runs but results aren't surfaced in the UI.
- **Training nav shortcut was removed** (commit `b7e76c60`). Users must navigate directly to `/training-workbench`. Consider adding it to the admin sidebar.

---

## 5. MCP tool changes

### 5.1 New tools

**`memorysmith_code_search_merge_shard`** — Write risk, MCP-only, disabled by default.
- Purpose: Merge an external shard SQLite DB into the code search index.
- Concern: The `shardPath` parameter accepts an arbitrary filesystem path with **no containment validation** at the tool level. See TRAIN-006.

**`memorysmith_page_save`** — Write risk, MCP-only, disabled by default, available in Agent mode.
- Purpose: Create or update a wiki page.
- Positive: Proper slug validation, markdown content validation, minimumRole authorization check.

**`memorysmith_page_delete`** — Write risk, MCP-only, disabled by default.
- Purpose: Delete a wiki page by slug.
- Positive: Proper slug validation and authorization.

### 5.2 Protocol version

Still `2025-06-18` — forward-dated relative to the MCP spec (current spec: `2025-03-26`). See TRAIN-009.

### 5.3 `num_ctx` fix confirmed

`BuildOllamaRequestOptions()` in `ChatServices.cs` now reads `chatOptions.OllamaContextWindowTokens` and includes `"num_ctx"` in the options dict when the value is set and > 0. Both streaming and non-streaming payloads receive the options. **The Audit #5 display-only bug is closed.**

---

## 6. Modelfile and prompt changes

### 6.1 ChatML migration — complete

The Modelfile now uses:
```
TEMPLATE """{{- if .System }}<|im_start|>system
{{ .System }}<|im_end|>
{{ end }}{{- range .Messages }}<|im_start|>{{ .Role }}
{{ .Content }}<|im_end|>
{{ end }}<|im_start|>assistant
"""
```

This matches Qwen3.5's native ChatML. Path B (recommended in the design doc) is implemented.

### 6.2 Missing `num_ctx` in Modelfile

The Modelfile does **not** set `PARAMETER num_ctx`. While the API layer now sends `num_ctx` via `BuildOllamaRequestOptions()`, direct Modelfile invocations (e.g., `ollama run`) will use the model's default. Given the system prompt is ~4000+ tokens, a default of 2048 would truncate it. Recommend adding `PARAMETER num_ctx 8192` at minimum.

### 6.3 Double repetition penalty

Both `repeat_penalty 1.25` and `presence_penalty 0.6` are set. Ollama applies both, which can cause coherence degradation on longer responses. Recommend using one or the other.

### 6.4 Sampling parameters

| Parameter | Value | Assessment |
|---|---|---|
| temperature | 0.4 | Conservative for factual wiki. Good. |
| top_p | 0.9 | Standard. |
| top_k | 40 | Reasonable. |
| repeat_penalty | 1.25 | Strong. See § 6.3. |
| presence_penalty | 0.6 | Combined with repeat_penalty, may be excessive. |
| frequency_penalty | 0.4 | Moderate. Stacks with the above. |

---

## 7. Configuration surface

### 7.1 TrainingOptions

New configuration class bound at `MemorySmith:Training`. All defaults are off/conservative:

| Option | Default | Assessment |
|---|---|---|
| `ChatTranscriptEnabled` | false | Safe default. |
| `StoreChatContent` | false | Safe default. |
| `TranscriptRedactionEnabled` | true | Good security posture. |
| `FeedbackEnabled` | false | Safe default. |
| `PythonVenvPath` | `.venv` | Relative path, resolved by TrainingPathResolver. |
| `PythonHarnessScript` | `MemorySmith.Training/harness.py` | Relative. |
| `HuggingFaceTokenEnvironmentVariable` | `HF_TOKEN` | Reads from OS env, not stored in config. |
| `MaxRunMinutes` | 360 | 6-hour cap. Reasonable. |
| `PreferenceFormat` | FilteredSft | Correct for v1. |
| `ShadowEvalEnabled` | false | Correct — would OOM on 8GB. |

### 7.2 SecurityProfile gap (TRAIN-005)

No check prevents training from running in `RemoteHardened` profile. Spawning Python processes and downloading HuggingFace models should be disabled in hardened mode.

---

## 8. Chat transcript and feedback

### 8.1 ChatTranscriptWriter

Implemented per the scaffold design. Metadata-only default with optional content companion. `TranscriptRedactionEnabled` runs regex patterns for Bearer tokens, API keys, secrets, and connection strings.

**Redaction concern (TRAIN-013):** The `BearerPattern` regex `\bBearer\s+[A-Za-z0-9._\-]+` misses JWT base64 characters (`+`, `/`, `=`). JWTs are the most common Bearer token format. The pattern should be `\bBearer\s+[A-Za-z0-9._\-+/=]+`.

### 8.2 ChatFeedbackStore

SQLite-backed upsert with `_initialized` flag. Double-checked locking pattern with non-volatile bool (TRAIN-014 — low severity).

---

## 9. Severity-tagged findings

### Critical

**TRAIN-001 | Critical | Training/inference chat template mismatch**
- **File:** `MemorySmith.Training/harness.py`, `to_training_text()` (~line 230)
- **Description:** Training data formatted as bare `<role>\ncontent` but inference uses ChatML (`<|im_start|>role\ncontent<|im_end|>`). Every LoRA fine-tune under the current code degrades the model's instruction-following because it learns the wrong template boundaries.
- **Impact:** Trained models will produce worse output than the base model on ChatML-prompted turns. Tool-call discipline, citation formatting, and mode-switching all regress.
- **Remediation:** Replace `to_training_text()` with a function that constructs proper ChatML sequences. Use `tokenizer.apply_chat_template()` if available, or manually build `<|im_start|>role\ncontent<|im_end|>` strings matching the Modelfile TEMPLATE.
- **Confidence:** 0.98

**TRAIN-002 | Critical | `trust_remote_code=True` with user-controllable model ID**
- **File:** `MemorySmith.Training/harness.py`, `train_lora()` (~line 243), `infer_lora()` (~line 283)
- **Description:** `AutoModelForCausalLM.from_pretrained()` and `AutoTokenizer.from_pretrained()` called with `trust_remote_code=True`. The model ID derives from admin-configurable settings. A compromised admin or settings file can point at a malicious HuggingFace repo → arbitrary Python code execution in the harness process.
- **Impact:** Remote code execution. The Python process runs with the same privileges as the MemorySmith app. On Windows service deployments, this may be SYSTEM.
- **Remediation:** Remove `trust_remote_code=True` (Qwen3.5 works without it in transformers >= 4.45). Maintain an allowlist of known-safe model IDs. As defense-in-depth, run the harness in a restricted user context.
- **Confidence:** 0.95

### High

**TRAIN-003 | High | No CUDA OOM handling or VRAM budget check**
- **File:** `MemorySmith.Training/harness.py`, `train_lora()`
- **Description:** Qwen3.5-4B in bf16 = ~8 GB weights alone. With LoRA + optimizer + activations at seq_len=1024, total is ~10-11 GB. No QLoRA (4-bit) loading, no gradient checkpointing, no `BitsAndBytesConfig`. Will OOM on RTX 5060 (8 GB).
- **Remediation:** Add 4-bit QLoRA via `BitsAndBytesConfig(load_in_4bit=True)` or use Unsloth's `FastLanguageModel.from_pretrained(load_in_4bit=True)`. Add `torch.cuda.mem_get_info()` pre-flight check. Add gradient checkpointing.
- **Confidence:** 0.95

**TRAIN-004 | High | No `requirements.txt` for Python training deps**
- **File:** `MemorySmith.Training/` (missing)
- **Description:** `dtype` parameter needs `transformers >= 4.45`. No version pinning anywhere. Dependency probe checks existence, not version.
- **Remediation:** Add `requirements-training.txt` with minimum: `torch>=2.1`, `transformers>=4.45`, `peft>=0.12`, `datasets>=2.19`, `bitsandbytes>=0.43`.
- **Confidence:** 0.99

**TRAIN-005 | High | No SecurityProfile gating for training**
- **File:** `MemorySmith.App/Services/Training/TrainingHarnessRunnerService.cs`
- **Description:** Training spawns Python processes and downloads HuggingFace models regardless of security profile. Should be disabled in `RemoteHardened`.
- **Remediation:** Check `MemorySmithOptions.SecurityProfile` in `StartRunAsync()`. Return error if `RemoteHardened`.
- **Confidence:** 0.90

**TRAIN-006 | High | `memorysmith_code_search_merge_shard` path traversal**
- **File:** `MemorySmith.App/Services/ChatToolCatalog.cs`, `memorysmith_code_search_merge_shard` handler
- **Description:** `shardPath` parameter passes an arbitrary filesystem path to `MergeShardAsync()` with no containment check. An MCP caller with write permission could read/corrupt any SQLite file on the filesystem.
- **Remediation:** Validate `shardPath` is within an allowed directory (e.g., the code search index root). Return error for paths outside the allowed root.
- **Confidence:** 0.90

**TRAIN-020 | High | HF token leaks into status artifacts**
- **File:** `MemorySmith.Training/harness.py`, commit `b313fb59`
- **Description:** The "persist HF auth context in run status artifacts" commit writes HF token presence (and potentially the token value) into `status.json` or `events.jsonl`. If these artifacts are ever served (diagnostics endpoint, admin page artifact browser), the token leaks.
- **Remediation:** Store only a boolean "hf_token_present" flag, never the token value. Extend the redaction pattern to status artifact writes.
- **Confidence:** 0.85 (need to verify exactly what is persisted — the commit message says "context" not "token", but the boundary is unclear)

### Medium

**TRAIN-007 | Medium | Process argument construction inconsistency**
- **File:** `TrainingHarnessRunnerService.cs`, `RunHarnessAsync()`
- **Description:** Probe uses safe `ArgumentList`; run uses manual `Quote()` + string join. Inconsistent.
- **Remediation:** Use `ArgumentList` consistently.

**TRAIN-008 | Medium | No cancellation support in Python harness**
- **File:** `MemorySmith.Training/harness.py`
- **Description:** No signal handler for SIGTERM/SIGINT. No cancel.flag polling. C# kill-process is the only stop mechanism.
- **Remediation:** Add `signal.signal(signal.SIGTERM, handler)`. Check a cancellation flag per training step.

**TRAIN-009 | Medium | Forward-dated MCP protocol version**
- **File:** `McpController.cs`, `BuildInitializeResult()`
- **Description:** `2025-06-18` is not an official MCP spec version. May break spec-validating clients.
- **Remediation:** Use `2025-03-26` or a clearly-custom version string.

**TRAIN-010 | Medium | Eval gate too permissive (records >= 2)**
- **File:** `MemorySmith.Training/harness.py`, `run()`
- **Description:** 2 examples is meaningless. One synthetic + one real = passes gate.
- **Remediation:** Raise to >= 10. Add unique token count check.

**TRAIN-011 | Medium | Double repetition penalty in Modelfile**
- **File:** `wiki-chat-agent.modelfile`
- **Description:** `repeat_penalty 1.25` + `presence_penalty 0.6` double-penalize repetition.
- **Remediation:** Use one or the other.

**TRAIN-012 | Medium | No `num_ctx` in Modelfile**
- **File:** `wiki-chat-agent.modelfile`
- **Description:** Direct `ollama run` uses default context. System prompt alone is ~4000 tokens.
- **Remediation:** Add `PARAMETER num_ctx 8192` minimum.

**TRAIN-013 | Medium | Transcript redaction regex misses JWT tokens**
- **File:** `ChatTranscriptWriter.cs`
- **Description:** Bearer pattern excludes `+/=`, common in JWTs.
- **Remediation:** Expand to `[A-Za-z0-9._\-+/=]+`.

**TRAIN-021 | Medium | Missing MLP target modules in LoRA config**
- **File:** `MemorySmith.Training/harness.py`, LoRA config
- **Description:** Only targets attention projections (`q_proj`, `k_proj`, `v_proj`, `o_proj`). Missing `gate_proj`, `up_proj`, `down_proj` MLP projections. For tool-call and formatting tasks, MLP layers carry significant formatting behavior. Including them would improve formatting discipline at modest VRAM cost.
- **Remediation:** Add `"gate_proj", "up_proj", "down_proj"` to `target_modules` when VRAM allows.

### Low

**TRAIN-014 | Low | Non-volatile `_initialized` flag in ChatFeedbackStore**
- **File:** `ChatFeedbackStore.cs`, `EnsureSchemaAsync()`
- **Description:** Double-checked locking with non-volatile bool. Safe on x86 but technically a data race.
- **Remediation:** Mark `volatile` or use `Volatile.Read()`.

**TRAIN-015 | Low | Polling fires every 2s even when idle**
- **File:** `TrainingWorkbench.razor`, `PollRunsAsync()`
- **Description:** Continuous 2-second poll with no idle backoff.
- **Remediation:** 10s when idle, 2s when active run detected.

**TRAIN-016 | Low | Hardcoded fallback training example**
- **File:** `harness.py`, `load_chat_examples()` fallback
- **Description:** Fallback reveals internal architecture details.
- **Remediation:** Require minimum real examples; fail explicitly.

### Info

**TRAIN-017 | Info | `dtype` parameter compatibility**
- **File:** `harness.py`, `train_lora()`, `infer_lora()`
- **Description:** `dtype` requires transformers >= 4.45. No version check.
- **Remediation:** Runtime check or requirements.txt.

**TRAIN-018 | Info | Inference comparison parameters inconsistency**
- **File:** `harness.py`, `infer_lora()`
- **Description:** `do_sample=False` with `top_p=0.9` and `temperature=0.7`. Sampling params ignored in greedy mode.
- **Remediation:** Remove `top_p`/`temperature` when `do_sample=False`.

**TRAIN-019 | Info | TrainingPathResolver walks entire directory tree**
- **File:** `TrainingPathResolver.cs`, `EnumerateCandidateBaseDirectories()`
- **Description:** Walks up to filesystem root. Could resolve to unexpected locations.
- **Remediation:** Limit upward walk to 3 levels or the repository root.

---

## 10. UX recommendations

### 10.1 Immediate wins (ship with the branch)

1. **Add num_ctx to the Modelfile** — prevents OOM on `ollama run` invocations. One line.
2. **Surface eval scores in run summaries** — the eval gate runs but results aren't shown in the workbench.
3. **Add a "Cancel run" button** — even if it's just process kill, users need a stop mechanism.
4. **Add training nav link to admin sidebar** — the shortcut was removed; users can't discover the page.

### 10.2 Medium-term (next sprint)

5. **Context-window dropdown** (design supplement § 1) — users have no way to adjust context without editing config.
6. **VRAM heuristic pre-flight** (design supplement § 2) — display estimated memory before starting a run.
7. **Estimated training time** — based on dataset size and hardware profile.
8. **Model promotion button** — one-click swap of `ActiveModelTag` through the UI.
9. **Training data quality dashboard** — show topic coverage, example count per category, avg token length.

### 10.3 Longer-term

10. **SignalR for live progress** — replace 2-second polling with push.
11. **Regenerate button** on assistant turns — enables DPO v2 pipeline.
12. **Per-conversation context override** — let users adjust context per chat.
13. **Memory-type chips** — visual taxonomy indicator on memory renders.

---

## 11. Cross-references to prior audits

| Prior finding | Current status |
|---|---|
| Audit #5 Clipboard-paste external fetch (memorysmith.js:813-832) | Still open — untouched by this branch |
| Audit #5 ChatReferenceLinkPolicy event handler bypass | Still open |
| Audit #5 Mermaid innerHTML XSS | Still open |
| Audit #5 OllamaContextWindowTokens display-only bug | **CLOSED** — fixed by BuildOllamaRequestOptions() |
| Audit #5 Zero server-side chat logging | **CLOSED** — ChatTranscriptWriter implemented |
| Audit #5 Zero feedback mechanism | **CLOSED** — ChatFeedbackStore implemented |
| Audit #5 23 configurability gaps | 3 addressed by TrainingOptions toggles; 20 still open |
| Audit #4 Code search findings | Partially addressed — batch embedding benchmarks, cache invalidation fix, embedding failure hardening, ONNX execution providers |

---

## 12. Assumptions and confidence

| Section | Confidence | Notes |
|---|---|---|
| TRAIN-001 (template mismatch) | 0.98 | Verified by reading `to_training_text()` and Modelfile TEMPLATE |
| TRAIN-002 (trust_remote_code) | 0.95 | Verified from `from_pretrained()` calls in harness.py |
| TRAIN-003 (VRAM math) | 0.90 | Based on published bf16 sizes; actual may vary ±15% |
| TRAIN-006 (shard path) | 0.90 | Need to verify MergeShardAsync implementation |
| TRAIN-020 (HF token leak) | 0.85 | Need to inspect exact fields persisted in status |
| Overall branch health | Medium-high | The training subsystem works end-to-end but has 2 critical bugs blocking production use |

---

## 13. Recommended priority

### P0 — Block release
1. **TRAIN-001** — Fix `to_training_text()` to produce ChatML. Every training run until this is fixed makes the model worse.
2. **TRAIN-002** — Remove `trust_remote_code=True`. One-line fix, removes RCE surface.

### P1 — Fix before first real training run
3. **TRAIN-003** — Add QLoRA / 4-bit loading. Current code OOMs on target hardware.
4. **TRAIN-004** — Add `requirements-training.txt`.
5. **TRAIN-006** — Add path containment to merge_shard.

### P2 — Fix before beta
6. **TRAIN-005** — SecurityProfile gating.
7. **TRAIN-010** — Raise eval gate minimum.
8. **TRAIN-012** — Add `num_ctx` to Modelfile.
9. **TRAIN-011** — Remove double repetition penalty.

### P3 — Polish
10. **TRAIN-008** — Add cancellation support.
11. **TRAIN-013** — Expand redaction patterns.
12. UX recommendations from § 10.