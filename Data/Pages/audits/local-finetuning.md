# MemorySmith Test & Fine-Tuning Harness — Technical Design

**Status:** Design + partial implementation baseline. Core transcript/feedback data plane, harness bridge script, and first benchmarked run artifact are now in-repo.
**Date:** 2026-05-28
**Scope:** Production-grade fine-tuning harness for MemorySmith's local LLM. Pivot target: `qwen3.5:4b` (Ollama, Apache 2.0, 4.66B params, Q4_K_M, 256K native context) running on RTX 5060 8 GB VRAM with a deployed context window of 16–24K tokens.
**Codebase reference:** master @ `c4d7a28ade1a2878d270f1479bfb255f5058482b`, audit branch `feature/code-search-high-roi-batch8` @ `61af8491`.
**Audit family:** This document is the forward-looking complement to Audits #1–5. Cross-references to Audit #5 are inlined where relevant.

---

## Implementation Delta (2026-05-28 Evening)

- Runnable harness bridge shipped:
  - `Scripts/Run-FinetuneHarness.ps1`
  - `MemorySmith.Training/harness.py`
- First run evidence recorded:
  - `runs/sprint3-ft-20260528/status.json`
  - `runs/sprint3-ft-20260528/benchmark.json`
  - `Data/Training/exports/sprint3-ft-20260528.sft.jsonl`
- Operator runbook: [guides/local-finetune-harness-runbook](../guides/local-finetune-harness-runbook.md)

Current limitation: the local `.venv` does not yet include full LoRA/TRL dependencies, so the current harness run executes export/eval/benchmark with simulated train steps. The bridge/status contract is already aligned for swapping to full training once the pinned training environment is provisioned.

---

## 0. Document map

1. Goals, scope, non-goals
2. Hardware envelope and context window math
3. Target model dossier (verified `qwen3.5:4b` facts)
4. Codebase grounding — what exists today, file by file
5. Data layer redesign — chat logging upgrade and thumbs feedback
6. Preference data export format decision (DPO vs ORPO vs filtered SFT)
7. The four-stage training pipeline
8. The three tuning objectives and evaluation framework
9. C# ↔ Python bridge contract
10. Architecture diagram (ASCII)
11. Training time and electricity cost estimates for the RTX 5060
12. Promotion and rollback workflow
13. Cross-references to Audit #5 findings
14. Open questions, assumptions, confidence values
15. Appendix A — full tool roster (the fine-tuning target surface)
16. Appendix B — JSON envelope grammar and rejection cases
17. Appendix C — chat template drift hazard

---

## 1. Goals, scope, non-goals

### 1.1 Primary goal

Make a locally-deployed 4B model behave like a 9B model on the **three workloads MemorySmith actually cares about**:

1. Producing structurally-perfect tool-call JSON in the exact envelope the C# orchestrator parses.
2. Producing Blazor-consumable Markdown (no raw HTML, well-formed Mermaid fences, citation patterns matching the `memory:<id>` / `page:<slug>` contract).
3. Internalizing the MemorySmith memory-status taxonomy (`Unconsolidated` / `Working` / `Core` / `Deprecated`) and the (proposed) memory-type taxonomy (`Episodic` / `Semantic` / `SystemConfig`) so it tags new records correctly without being re-instructed every turn.

A general-purpose 4B model misses each of these often enough that a 9B is needed at inference time today. The hypothesis: 200–2000 well-shaped instruction pairs and a small preference dataset will close that gap, freeing roughly 3.2 GB of VRAM that the 9B currently consumes and unlocking the 16–24K context window we want.

### 1.2 Secondary goals

- **Reproducibility.** Any maintainer should be able to re-run the pipeline against a new base model (Qwen 3.6 4B, Llama 4 5B, anything Unsloth supports) with a config change and no code edits.
- **Honesty surface.** Eval results live next to the model artifact. No model goes to `active` without passing the gate.
- **Local-first.** No cloud calls during training by default. All credentials, logs, and weights stay on the machine.
- **Survive context compaction.** The harness writes its own training-relevant signals (preferences, eval results) so a future agent can pick up where this run left off.

### 1.3 Non-goals (explicit)

- Multi-GPU distributed training. The 5060 is a single card; designing around DeepSpeed Zero-3 is overkill.
- RLHF in the classical sense (reward model + PPO). DPO / ORPO subsume the reward signal directly from preference data.
- Vision fine-tuning. `qwen3.5:4b` is multimodal but MemorySmith's current chat path passes images as model-native payloads only when the provider supports it; the fine-tune targets the text and tool-call paths.
- Replacing GitHub Copilot. The `GitHubCopilotChatProvider` continues to exist for users who want cloud quality on demand; the local fine-tune is the default-on path.
- A new MCP SDK adoption. The hand-rolled `McpController.cs` stays. The harness fine-tunes against MemorySmith's actual current tool surface, not a hypothetical future one.

### 1.4 Definition of done

The harness is "done" when a one-line invocation from the .NET app (or a Scripts/ entry-point) produces:

1. A Modelfile-registered Ollama tag `memorysmith-athena:vN` ready to swap into `ChatOptions.OllamaModel`.
2. An eval report (JSON + Markdown) covering all three objectives with pass/fail per criterion.
3. A `model-card.md` that captures provenance: base model SHA, training dataset hash, hyperparameters, eval scores, FP16 master-weight checkpoint location, and a rollback target.

---

## 2. Hardware envelope and context window math

### 2.1 Verified hardware target

- **GPU:** NVIDIA RTX 5060 (8 GB GDDR7 VRAM, ~448 GB/s memory bandwidth, Blackwell architecture, FP16 ~25 TFLOPS, INT8/FP8 ~50 TOPS — Blackwell adds native FP4 too).
- **System RAM assumption:** 32 GB minimum recommended for training; activations spill to CPU memory under Unsloth's gradient checkpointing.
- **Storage assumption:** ≥80 GB free on the SSD where Unsloth stages model weights, intermediate checkpoints, and GGUF exports.

### 2.2 Inference VRAM budget (16K and 24K context)

`qwen3.5:4b` ships pre-quantized at Q4_K_M, **3.4 GB on disk** (verified from the Ollama tag page, blob SHA `2a654d98e6fb`). At runtime, VRAM is dominated by three buckets:

| Bucket | Estimate at 16K ctx | Estimate at 24K ctx | Notes |
|---|---|---|---|
| Model weights (Q4_K_M) | 3.4 GB | 3.4 GB | Fixed |
| KV cache (FP16) | ~9.7 GB | ~14.5 GB | **Will not fit.** Quantize KV cache. |
| KV cache (Q8_0) | ~4.9 GB | ~7.3 GB | 16K just fits; 24K does not. |
| KV cache (Q4_0) | ~2.4 GB | ~3.6 GB | Both fit. Some quality loss. |
| Activations / overhead | 0.4–0.8 GB | 0.4–0.8 GB | Llama.cpp inference path |

The KV cache estimates assume a dense transformer with **~36 hidden layers × 32 attention heads × 128 head dim × 2 bytes (FP16)** = ~589 KB/token. Confidence: **medium**. The exact architecture of `qwen3.5:4b` is not published in the Ollama page beyond `architecture: qwen35` and `parameters: 4.66B`. The pipeline must call `ollama show qwen3.5:4b --modelfile` and inspect the GGUF metadata blob during install to verify; if the architecture diverges from the assumption (e.g., grouped-query attention with fewer KV heads), the KV cache footprint drops substantially.

**Recommendation:** Default to **16K context with KV cache at Q8_0** for the deployed Athena model. Offer a `Chat:OllamaKvCacheType` option (new) gated by the SecurityProfile, defaulting to `q8_0` on `secure-local` and `q4_0` on `local-dev`. Fix the existing `OllamaContextWindowTokens` bug at the same time so `num_ctx` is actually sent to Ollama (see § 4.4 below).

**Why 16K and not 24K as the default:** the marginal user value of 8K more context on a chat-with-wiki workload is modest, while the VRAM headroom matters when the OS, browser, IDE, and Blazor app are also pulling on the GPU. 24K should be a `secure-local` opt-in for users with a clean GPU.

### 2.3 Training VRAM budget

Unsloth's published savings on a 4B model are **~60% less VRAM** versus baseline HuggingFace TRL. Reasonable working numbers:

| Configuration | Sequence length | VRAM | Fits 5060 (8 GB)? |
|---|---|---|---|
| LoRA (16-bit base) | 4096 | ~7.5 GB | Tight |
| LoRA (16-bit base) | 8192 | ~10–11 GB | **No** |
| QLoRA (4-bit base, 16-bit adapters) | 4096 | ~5.2 GB | Yes |
| QLoRA | 8192 | ~6.8 GB | Yes |
| QLoRA | 16384 | ~10 GB | **No** — train shorter, validate longer |
| QLoRA + gradient checkpointing + offload | 8192 | ~5.5 GB | Yes (slower) |

Confidence on these numbers: **medium-high**. Unsloth's notebooks ship with documented memory footprints, but the exact 5060 number will only be known when the harness runs.

**Recommendation:** Train at sequence length 4096 with QLoRA. Evaluate at 16K context using the trained adapters merged into the FP16 master weights, then re-quantize to Q4_K_M for deployment. This way training compute stays cheap and inference quality at the deployed context is what gets measured.

### 2.4 Power / thermal budget

The 5060 nominal TBP is 145 W. Sustained training pegs the card. Assume **150 W avg under load + ~50 W CPU/RAM/disk + ~30 W rest of system ≈ 230 W**. We use this number again in § 11 when estimating cost.

---

## 3. Target model dossier — `qwen3.5:4b`

Every claim in this section is verified against the live Ollama tag page (fetched 2026-05-28).

| Field | Value | Source |
|---|---|---|
| Tag | `qwen3.5:4b` | ollama.com/library/qwen3.5:4b |
| Blob SHA | `2a654d98e6fb` | Ollama "Details" panel |
| Architecture | `qwen35` | GGUF metadata, surfaced by Ollama |
| Parameters | 4.66 B | Ollama "Details" panel |
| Quantization (default tag) | Q4_K_M | Ollama "Details" panel |
| File size on disk | 3.4 GB | Ollama "Details" panel |
| Native context window | 256 K | Ollama listing column |
| License | Apache 2.0 | Ollama "Details" panel |
| Tag updated | ~2 months before 2026-05-28 | Ollama "Updated" badge |
| Capability flags | vision, tools, thinking, cloud | Ollama category badges |
| Default sampling params | `presence_penalty 1.5`, `temperature 1.0`, `top_k 20`, `top_p 0.95` | Ollama params blob |
| Sibling sizes | 0.8B, 2B, 4B, 9B, 27B, 35B (35-A3B MoE), 122B (112B-A10B MoE), 397B cloud-only | Library page |

### 3.1 Family architecture notes

From the Ollama README on the library page:

- "**Unified Vision-Language Foundation** — Early fusion training on multimodal tokens achieves cross-generational parity with Qwen3."
- "**Efficient Hybrid Architecture** — Gated Delta Networks combined with sparse Mixture-of-Experts deliver high-throughput inference."
- "Expanded support to 201 languages and dialects."
- The 4B specifically is **dense** (not MoE — only the 35-A3B, 112B-A10B, and 397B-A17B tags are flagged with MoE active-parameter counts). The "Gated Delta Networks" architectural claim applies to the family broadly; whether the dense 4B uses sliding-window attention, grouped-query attention, or sticks with standard MHA is **not specified** on the Ollama page. **Action:** the install script must dump GGUF metadata to confirm, and the design doc's KV cache math should be revised if it diverges.

### 3.2 Unsloth support — verified

From the Unsloth main-branch README (fetched 2026-05-28):

> Qwen3.5 - 0.8B, 2B, 4B, 9B, 27B, 35-A3B, 112B-A10B are now supported. Guide + notebooks at unsloth.ai/docs/models/qwen3.5/fine-tune.

The notebooks confirm:

- `Qwen3_5_(4B)_Vision.ipynb` — **1.5× faster, 60% less VRAM** than baseline.
- `Qwen3_5_(4B)_Vision_GRPO.ipynb` — **2× faster, 70% less VRAM** for the GSPO variant.

Confidence on Unsloth-Qwen3.5-4B compatibility: **high**. The notebooks are first-party.

### 3.3 LoRA target modules

For the `qwen35` architecture in Unsloth, the canonical target module set is the same family used by Qwen3:

```python
target_modules = [
    "q_proj", "k_proj", "v_proj", "o_proj",
    "gate_proj", "up_proj", "down_proj",
]
```

Confidence: **medium-high**. The exact module names need to be confirmed against the loaded model's `state_dict` keys at training time. The design recommends a defensive `assert len(missing_targets) == 0` after `FastLanguageModel.get_peft_model(...)` so any drift in module naming fails loud rather than producing a "trained" adapter that touched nothing.

### 3.4 Default sampling at inference

The Ollama tag ships defaults that are **calibrated for the family's pretraining, not for MemorySmith's tool-call discipline**:

```json
{ "presence_penalty": 1.5, "temperature": 1.0, "top_k": 20, "top_p": 0.95 }
```

A `temperature: 1.0` and `presence_penalty: 1.5` will inject creative variation into what should be deterministic JSON tool calls. The existing `wiki-chat-agent.modelfile` already overrides these — it sets `temperature 0.4`, `top_p 0.9`, `top_k 40`, `repeat_penalty 1.25`, `presence_penalty 0.6`, `frequency_penalty 0.4`. **The harness's generated Modelfile must continue to override these.** The risk: a future maintainer regenerating the Modelfile from the upstream defaults will silently degrade tool-call reliability.

---

## 4. Codebase grounding — what exists today

This section is the load-bearing one: the design is grounded in real file paths, not a hypothetical architecture. All paths are relative to the repository root.

### 4.1 Solution layout

| Project | Role |
|---|---|
| `MemorySmith.App/` | Blazor Server + ASP.NET Core host. Chat UI, REST API, MCP endpoint, DI root. |
| `MemorySmith.Core/` | Shared domain models (`MemoryRecord`, `MemoryStatus`, security models). ONNX indexing. |
| `MemorySmith.Storage/` | File and SQLite persistence (`FileMemoryStore`, `FileEventStore`, `SqliteMemorySmithDatabase`). |
| `MemorySmith.Benchmarks/` | BenchmarkDotNet suite. |
| `MemorySmith.Tests/` | NUnit. |
| `e2e/` | End-to-end tests. |

### 4.2 Ollama integration

- **Class:** `OllamaChatProvider` (sealed partial) in `MemorySmith.App/Services/ChatServices.cs` (~line 439).
- **Endpoint:** assembled at call time from `Chat:OllamaEndpoint` (default `http://localhost:11434`). The provider hits `api/chat` for chat and `api/tags` for model enumeration.
- **Model name:** read from `Chat:OllamaModel`. **Default in code:** `"gemma4:e4b"` (`ChatOptions` at `MemorySmith.App/Services/MemorySmithOptions.cs:278`, confirmed in `appsettings.json`).
- **Modelfile shipped in repo:** `MemorySmith.Core/Docs/Prompts/wiki-chat-agent.modelfile` — begins with `FROM qwen3.5`. This is the "Athena" model that real deployments build locally and tag. The `gemma4:e4b` default is a fallback for users who skip the modelfile build step.
- **Capability declaration:**
  - `SupportsNativeToolCalls: false` — uses MemorySmith's own JSON-text tool protocol.
  - `SupportsStreaming: true`, `SupportsImageInput: true`, `ReportsContextWindowUsage: true`.

### 4.3 Chat pipeline

- **Entry points:**
  1. `MemorySmith.App/Components/Pages/Chat.razor` (~135 KB, ~2920 lines) calls `IChatAgent.StreamAsync` directly.
  2. `MemorySmith.App/Controllers/ChatController.cs` (REST, thin wrapper).
- **Orchestrator:** `MemoryChatAgent` (sealed partial) in `MemorySmith.App/Services/ChatServices.cs:~1225`.
- **Turn flow (streaming):**
  1. `ResolveProvider(request.Provider)` picks `OllamaChatProvider` or `GitHubCopilotChatProvider`.
  2. `BuildContextPlan(request)` — intent-aware planner (`ChatContextPlanner`).
  3. `BuildContextAsync(...)` — hybrid memory + page preload bounded by `MaxPreloadedContextRecords` / `MaxPreloadedContextPages`.
  4. `RunIntentInterceptAsync(...)` — `ChatIntentInterceptor` fires a deterministic tool call for obvious phrases ("search the wiki for…", "open page…").
  5. `BuildMessages(...)` assembles the message list.
  6. Stream loop: provider streams content, buffer is checked for a tool-call prefix via `IsPotentialToolCallPrefix(content)`. On match, `ReadToolCalls(content)` parses `{"toolCalls":[{"name":"...","arguments":{...}}]}`. Tool result is appended as `system` message. Loop bounded by `MaxToolIterations` (default **2**) and `MaxToolCallsPerTurn`.
  7. `BuildResponseAsync(...)` constructs `MemoryChatResponse`.

### 4.4 The `OllamaContextWindowTokens` bug (and why the harness fixes it)

The field exists in `ChatOptions` (nullable int, default null) and is read in `ResolveUsageMetadata` for the **display overlay**. The chat payload sent to `api/chat` is:

```csharp
new { model, stream = false /* or true */, messages = BuildOllamaMessages(request) }
```

There is **no `options.num_ctx` field**. Ollama uses the model's default. For `qwen3.5:4b` that default is 256K, which on an 8 GB card will simply OOM the moment a long conversation tries to allocate KV cache.

**Fix proposed as part of this harness:** extend the payload to:

```csharp
new {
    model,
    stream,
    messages = BuildOllamaMessages(request),
    options = new {
        num_ctx = chatOptions.OllamaContextWindowTokens ?? 16384,
        // optional advanced:
        num_keep = 4,
        repeat_penalty = chatOptions.OllamaRepeatPenalty ?? 1.25,
    }
}
```

This is a single-commit fix that pays off the harness's first VRAM dividend immediately. It is **not** strictly part of the fine-tune, but it lives in the same change set because the trained model's deployed context is meaningless if the C# host doesn't pass `num_ctx`.

### 4.5 MCP tools exposed

**Source of truth:** `MemorySmith.App/Services/ChatToolCatalog.cs` (~68 KB), used by both `/mcp` and chat intercept. **Transport:** plain ASP.NET MVC `[ApiController]` at `[Route("mcp")]` in `MemorySmith.App/Controllers/McpController.cs`. Protocol version: `"2025-06-18"`. Server name: `"MemorySmithWiki"`. No `ModelContextProtocol` C# SDK, no `Microsoft.Extensions.AI` package.

Full tool roster — see **Appendix A**. The fine-tune trains against these exact names.

### 4.6 Logging surface (the gap)

**What exists:**

- **Serilog** structured logs at `logs/memorysmith-structured-.jsonl` (14-day retention), console, and Windows Event Log (warnings+). Config: `MemorySmith:Logging` → `LoggingOptions` in `MemorySmithOptions.cs`.
- **Audit log with HMAC chain:** `AuditLogService` in `SecurityServices.cs`. Entries have `BeforeHash`, `AfterHash`, `PreviousAuditHash`, `AuditHash`. Backed by SQLite + JSONL at `Data/Events/audit-{yyyy}-W{week}.jsonl`. Toggled by `Audit:HashChainEnabled`. **Covers security events only** — login, role changes, page writes. Does **not** cover chat turns.
- **Maintenance agent transcript:** `Data/Events/maintenance-agent-transcript.jsonl`, retention 200, redaction enabled. Captures maintenance-agent LLM turns. Does **not** cover user chat.
- **OpenTelemetry:** `MemorySmithTelemetry` (`ActivitySource` + `Meter`). OTLP exporter optional. **No chat-turn span is created.**

**What does NOT exist — and this is the design's critical input:**

- **Zero `ILogger<>` is injected into `MemoryChatAgent`.** The constructor takes no logger.
- **No SQLite table for chat sessions or turns.** `SqliteMemorySmithDatabase`'s schema contains no `ChatLog`, `Conversation`, `ChatTurn`, or `Feedback` table.
- **No JSONL transcript for user-facing chat.**
- **Chat history is browser localStorage only.** Key: `memorysmith.chat.preferences.v1`. Once the user closes the tab or clears storage, history is gone. There is **zero server-side record of any chat turn that has ever occurred.**

This is the gap the harness must fill before any "mine the logs for training data" stage 1 design can function.

### 4.7 Existing feedback / rating mechanism

**None.** Exhaustive search confirmed.

- "thumb" appears only in `chat-attachment-thumb` (a CSS class for image thumbnails).
- "feedback", "rating", "vote", "prefer", "like", "dislike", "good", "bad" — zero signal-bearing hits in the chat UI.
- No `ChatFeedback` model anywhere.
- No SQLite feedback table.

The harness will introduce this from scratch.

### 4.8 Memory tiering taxonomy

Today the closest thing is `MemoryStatus` (`MemorySmith.Core/Models/MemoryStatus.cs`):

```csharp
public enum MemoryStatus { Unconsolidated, Working, Core, Deprecated }
```

This is a **lifecycle** enum, not a **type** enum. The Gemini prompt's "Episodic / Semantic / SystemConfig" taxonomy does not exist in the codebase. `MemoryRecord` has `Tags` (free-form `List<string>`) and `Confidence` (0.0–1.0), but no `Type`, `Tier`, or `Category` property.

**Design decision required (open question OQ-3 in § 14):** either introduce a new `MemoryType` enum on `MemoryRecord` or commit to a reserved-tag convention (`type:episodic`, `type:semantic`, `type:system-config`). The fine-tuning target labels depend on this.

### 4.9 Config surface

Root: `MemorySmithOptions` in `MemorySmith.App/Services/MemorySmithOptions.cs`. Sub-options bound at `MemorySmith:*`:

- `Chat`, `Logging`, `Telemetry`, `Audit`, `Mcp`, `MaintenanceAgent`.
- `SecurityProfile` ∈ {`local-dev`, `secure-local`, `remote-hardened`}. Default: `secure-local`.

**Where new training options slot in:**

```csharp
public class MemorySmithOptions {
    // ...existing...
    public TrainingOptions Training { get; set; } = new();
}

public class TrainingOptions {
    public bool FeedbackEnabled { get; set; } = false;      // default-off: opt in
    public string FeedbackStoragePath { get; set; } = "Data/Events/chat-feedback.jsonl";
    public string TranscriptPath { get; set; } = "Data/Events/chat-transcripts/";
    public bool ChatTranscriptEnabled { get; set; } = false;
    public string ActiveModelTag { get; set; } = "memorysmith-athena:latest";
    public string FallbackModelTag { get; set; } = "qwen3.5:4b";
    public string TrainingDataExportPath { get; set; } = "Data/Training/exports/";
    public PreferenceExportFormat PreferenceFormat { get; set; } = PreferenceExportFormat.FilteredSft;
    // SecurityProfile-driven defaults applied by the same loader that handles MemorySmithSecurityProfiles.
}

public enum PreferenceExportFormat { FilteredSft, Dpo, Orpo }
```

Default-off respects Audit #5 finding **"Configurability gaps (23 specific)"**: anything that touches user content or sends data to disk needs an explicit toggle.

### 4.10 Chat UI component

`MemorySmith.App/Components/Pages/Chat.razor`. Message rendering loop (~lines 110–155):

```razor
<article class="chat-message @turn.Role">
  <div class="chat-message-topline">
    <span class="chat-message-role">@turn.Role</span>
    <span class="chat-message-meta">
      <span class="chat-message-model">@FormatTurnModel(turn)</span>
    </span>
  </div>
  <div class="chat-message-body chat-message-markdown">...</div>
</article>
```

MudBlazor is already in use elsewhere in the same file. The thumbs-up / thumbs-down buttons slot inside `chat-message-topline` on `turn.Role == "assistant"` messages only (the user's own turns don't get rated).

### 4.11 JS interop surface

`MemorySmith.App/wwwroot/memorysmith.js` (~35 KB). Contains `window.memorySmith.chat` namespace. No existing feedback function. Audit #5's HIGH-severity findings on this file (clipboard-paste external fetch, Mermaid `innerHTML`) must be respected — the thumbs feedback path should **not** add a new JS interop call. A pure Blazor `EventCallback` is sufficient.

---

## 5. Data layer redesign — chat logging upgrade and thumbs feedback

### 5.1 Two storage surfaces

The harness needs two new write paths. Both are governed by `TrainingOptions` toggles (§ 4.9) and both default to off.

#### Surface 1: Chat transcripts

**Purpose:** capture the literal turn-by-turn record needed to reconstruct training prompts and assistant responses.

**Backend:** JSONL files at `Data/Events/chat-transcripts/{yyyy-MM-dd}.jsonl`. Append-only. One line per assistant turn (the user message and any tool calls are inline in the record). 90-day default retention configurable.

**Schema (`ChatTurnRecord`):**

```json
{
  "id": "01J9XK5RT3...ULID",
  "timestamp": "2026-05-28T19:14:02.137Z",
  "sessionId": "01J9XK5R...",
  "user": { "principalId": "local:tmason", "displayName": "TheMasonX" },
  "model": { "tag": "memorysmith-athena:v3", "provider": "ollama" },
  "templateVersion": "wiki-chat-agent-v1",
  "modeIntent": "Chat",
  "systemPromptHash": "sha256:7a2b...",
  "request": {
    "message": "search the wiki for KV cache options",
    "historyTurnCount": 4,
    "preloadedMemoryIds": ["mem_abc", "mem_def"],
    "preloadedPageSlugs": [],
    "attachmentTypes": []
  },
  "execution": {
    "toolCalls": [
      { "name": "memorysmith_unified_search", "argumentsJson": "{\"query\":\"...\"}", "latencyMs": 41 }
    ],
    "iterationsUsed": 1,
    "promptTokens": 2104,
    "completionTokens": 311,
    "totalTokens": 2415,
    "firstTokenMs": 287,
    "totalMs": 4123
  },
  "response": {
    "finishReason": "stop",
    "contentSha256": "sha256:c1d8...",
    "contentBytes": 1842
  },
  "redactedContent": false,
  "redactionRule": null
}
```

**Privacy:** the literal request and response text are **not** in the transcript record by default. They live in a sibling file `chat-transcripts/{yyyy-MM-dd}.content.jsonl` keyed by `id`, gated by a separate `TrainingOptions.StoreChatContent` toggle. This is a deliberate Audit #5–style configurability split: a user can opt in to logging metadata (for evals) without opting in to logging literal content (for training).

**Why content is split off:** at training time the export job reads both files and joins. At any other time the metadata file is sufficient for instrumentation, drift detection, and "did the system actually fire a tool?" debugging.

#### Surface 2: Thumbs feedback

**Purpose:** capture the user's preference signal on the assistant's response.

**Backend:** SQLite table in `SqliteMemorySmithDatabase`. Why SQLite, not JSONL: feedback needs random updates (user changes mind), needs joins to transcripts, and is small in volume.

**Schema:**

```sql
CREATE TABLE chat_feedback (
    id              TEXT PRIMARY KEY,                  -- ULID
    turn_id         TEXT NOT NULL REFERENCES ...,      -- matches ChatTurnRecord.id
    session_id      TEXT NOT NULL,
    principal_id    TEXT NOT NULL,
    rating          INTEGER NOT NULL CHECK (rating IN (-1, 0, 1)),  -- thumbs down / cleared / thumbs up
    note            TEXT,                              -- optional freeform "why?"
    created_at      TEXT NOT NULL,
    updated_at      TEXT NOT NULL
);
CREATE INDEX idx_chat_feedback_turn ON chat_feedback (turn_id);
CREATE INDEX idx_chat_feedback_rating ON chat_feedback (rating);
```

A `0` rating means the user hit thumbs and then changed their mind; the row persists for the audit trail. `note` enables capturing the most valuable signal in the simplest possible form — when a user hits thumbs-down, popping a one-line "what went wrong?" textbox is gold for training because it tells you **what kind of failure mode** the model hit.

### 5.2 Blazor UI: thumbs slot

Inside the existing `chat-message-topline` on assistant turns:

```razor
@if (turn.Role == "assistant" && Options.Training.FeedbackEnabled)
{
    <div class="chat-message-feedback">
        <MudIconButton
            Icon="@Icons.Material.Outlined.ThumbUp"
            Color="@(turn.Feedback?.Rating == 1 ? Color.Success : Color.Default)"
            Size="Size.Small"
            OnClick="() => SubmitFeedback(turn, 1)"
            aria-label="Mark helpful" />
        <MudIconButton
            Icon="@Icons.Material.Outlined.ThumbDown"
            Color="@(turn.Feedback?.Rating == -1 ? Color.Error : Color.Default)"
            Size="Size.Small"
            OnClick="() => SubmitFeedback(turn, -1)"
            aria-label="Mark unhelpful" />
    </div>
}
```

`SubmitFeedback` is a server-side Blazor handler — no new JS interop, no fetch, no risk of touching the `memorysmith.js` clipboard-paste finding from Audit #5. A thumbs-down click optionally surfaces a single-line note input via the existing `MudTextField` already present in the page.

**Accessibility:** the `aria-label`s are required. The icons are stand-alone clickable controls; without labels a screen reader user has no way to rate.

**No JS interop.** The audit's clipboard-paste finding and Mermaid `innerHTML` finding are both reasons to keep the new feature off the JS surface. Pure Blazor `EventCallback<>`.

### 5.3 The "metadata-only by default" stance

The harness defaults are tuned for someone who has not yet decided whether they want training data captured. Three concentric opt-ins:

1. `ChatTranscriptEnabled = true` → metadata transcripts only. Nothing the user typed is on disk.
2. `StoreChatContent = true` → literal request/response text logged. Suitable for someone running training locally on their own data.
3. `FeedbackEnabled = true` → thumbs UI rendered. Independent of the two above; can be enabled solo for telemetry-only "are responses good" instrumentation.

This is the right shape for a local-first single-actor threat model (per the calibrated threat model in Audit #5). Anyone running on `remote-hardened` profile gets all three off by default.

### 5.4 Export pipeline

A new project: `MemorySmith.Training` (.NET 9 class library, or a CLI tool under `Scripts/`). Two entry points:

- `dotnet run --project MemorySmith.Training -- export-sft --since 2026-04-01 --out Data/Training/exports/sft-2026-04.jsonl`
- `dotnet run --project MemorySmith.Training -- export-preferences --since 2026-04-01 --format dpo --out Data/Training/exports/dpo-2026-04.jsonl`

The exporter:

1. Joins transcript metadata to content (if `StoreChatContent` was on) to assemble {system, user, tool_calls, assistant} sequences.
2. Joins to `chat_feedback` for rating annotation.
3. Drops any turn with `redactedContent = true`.
4. Drops any turn whose `principalId` is on a per-export deny list.
5. Validates each emitted line against the target schema (DPO triple or SFT messages array).
6. Writes a `manifest.json` with row count, source date range, schema version, and a content hash.

The manifest is the contract the training script reads. If schemas drift, the training script fails fast on the manifest, never silently on data.

---

## 6. Preference data export format — DPO vs ORPO vs filtered SFT

### 6.1 The three options laid flat

| Format | Input shape | What it learns | Volume needed | Unsloth support |
|---|---|---|---|---|
| **Filtered SFT** | `{messages: [...]}` per line (chat ML) | Imitate positives; ignore negatives | Any volume | First-class |
| **DPO** | `{prompt, chosen, rejected}` per line | Prefer chosen over rejected for same prompt | ~500+ pairs minimum, ideally 2K+ | First-class |
| **ORPO** | `{prompt, chosen, rejected}` per line | SFT + preference in one pass; no separate SFT step | ~500+ pairs | First-class, newer |

### 6.2 The volume reality for a solo-developer MemorySmith user

A single developer using the chat feature daily generates maybe **5–30 turns/day**. If 20% get rated and 10% of rated turns are thumbs-down, that's **0.1–0.6 thumbs-down/day**. Even at 365 days that's well under 500 pairs — the floor where DPO starts to behave well.

DPO's other constraint is that it needs **paired** examples — for the same prompt, one chosen and one rejected response. Thumbs feedback as designed in § 5 does **not** naturally produce pairs. It produces single-sample ratings. To get pairs, the harness needs one of:

- **Re-roll button.** When a user thumbs-down, surface a "regenerate" affordance. The new response becomes the candidate "chosen" if the user thumbs it up. This is the cheapest path and worth wiring at the same time as feedback.
- **Sibling generation at log time.** For every turn, generate a "B" response in the background (using a stronger model — temporarily Copilot, or a higher-temperature version of the same model) and store both. Users rate only one, but pairs exist for export. Expensive in compute, cleaner in data quality.
- **Synthetic pairing.** Use Copilot or a stronger external model to generate a "chosen" for any thumbed-down turn, post-hoc. Cheap, but the "chosen" wasn't actually produced by the candidate model, which biases training.

### 6.3 Recommendation

**V1 (ship now): Filtered SFT.**

- The export drops thumbs-down turns and keeps the rest as positive training examples (weighted by rating, optionally).
- Works with any volume. A maintainer can ship a fine-tune off **50 high-quality positive turns** and see real behavior change.
- The `notes` field on thumbs-down rows is captured and surfaced in a "failure modes" report. This is genuinely useful even without using it for preference training.

**V2 (after ~3 months of operation): DPO with re-roll pairs.**

- Add the "regenerate" affordance to the chat UI (an `MudIconButton` next to the thumbs).
- Both responses are persisted; the rating delta produces a `{prompt, chosen, rejected}` triple naturally.
- When the pair count crosses a threshold (default 500 pairs), switch `TrainingOptions.PreferenceFormat = Dpo` and re-export.

**V3 (only if v2 plateaus): ORPO.**

- ORPO collapses the SFT+DPO two-pass loop into one and is the right call **only if** v2 reveals the SFT pass is eating headroom we'd rather spend on preferences.

### 6.4 Concrete export schemas

**Filtered SFT (`*.sft.jsonl`):**

```json
{"messages":[
  {"role":"system","content":"# Athena — MemorySmith ..."},
  {"role":"user","content":"search the wiki for KV cache options"},
  {"role":"assistant","content":"{\"toolCalls\":[{\"name\":\"memorysmith_unified_search\",\"arguments\":{\"query\":\"KV cache options\",\"memoryLimit\":5,\"pageLimit\":5}}]}"},
  {"role":"tool","content":"{\"results\":[...]}"},
  {"role":"assistant","content":"Athena's grounded answer..."}
],"weight":1.0,"sourceTurnId":"01J9XK5RT3..."}
```

**DPO (`*.dpo.jsonl`):**

```json
{
  "prompt":"...full prompt context as a chat-template-formatted string...",
  "chosen":"...assistant response from the thumbs-up sibling...",
  "rejected":"...assistant response from the thumbs-down sibling...",
  "sourceTurnIds":{"chosen":"01J9...","rejected":"01J9..."}
}
```

**ORPO:** same as DPO with optional `chosen_weight` and `rejected_weight`.

### 6.5 The hidden trap: chat template drift in exports

The DPO/ORPO `prompt` field is a **rendered chat-template string**, not a list of messages. Whatever chat template the export uses must **match exactly** the template the Modelfile registers with Ollama and the template Unsloth uses during training. If those three diverge, the model learns one boundary and gets queried at another and falls off a cliff.

The harness enforces this with a **single template artifact** at `MemorySmith.Core/Docs/Prompts/chat-template.jinja2` that all three consumers (Modelfile generation, training script, export script) read from. See § 7.1 below.

---

## 7. The four-stage training pipeline

### 7.1 Stage 1 — Data synthesis and chat template enforcement

**Inputs:**

- `Data/Training/exports/*.sft.jsonl` (or `.dpo.jsonl`) from the C# exporter.
- `MemorySmith.Core/Docs/Prompts/wiki-chat-agent.md` — the live system prompt.
- `MemorySmith.Core/Docs/Prompts/chat-template.jinja2` — the canonical chat template (new file the harness introduces).
- A synthetic-data pack at `MemorySmith.Training/synthetic/` — see below.

**Synthetic-data pack contents (~500–2000 examples, hand-curated + Copilot-assisted):**

1. **Tool-call discipline pairs.** "search the wiki for X" → exact JSON envelope. Coverage: every tool in Appendix A's read-only set, with permutations of arguments. ~30 examples per tool × 12 tools = 360 examples.
2. **Markdown formatting examples.** Substantive answers in the four-shape contract (direct answer / evidence / inference / sources), with `memory:<id>` and `page:<slug>` citations correctly formatted. ~200 examples.
3. **Mode discipline.** Chat-mode refusals when asked to write (must produce the canonical "writes require Agent mode" explanation). Agent-mode strict JSON outputs. ~100 examples.
4. **Memory-type labeling.** Given a sample of new information, propose the correct `Episodic` / `Semantic` / `SystemConfig` tag along with the `Working` / `Core` status. ~150 examples once the taxonomy is decided (open question OQ-3).
5. **Question-card protocol.** Decision-point examples where the model should return `{"questionCard": {...}}`. ~50 examples.
6. **Mermaid diagram correctness.** Examples that produce valid Mermaid only when a diagram genuinely clarifies. ~40 examples.
7. **Citation hygiene negatives** (for DPO): a "rejected" example where citations link to `(id: Title)` (non-resolvable) and a "chosen" example with `memory:<id>` formatting. ~100 pairs.

**Chat template enforcement:**

The custom template in the current `wiki-chat-agent.modelfile` is:

```
<|system|>
{{ .System }}
<|user|>
{{ .Prompt }}
<|assistant|>
```

This is **NOT** Qwen3.5's native ChatML template (`<|im_start|>role\ncontent<|im_end|>`). Two paths:

**Path A (recommended): keep the custom template and train against it.**
- Pros: minimal change to inference path.
- Cons: the base model's native instruction-following is partially wasted; need ~30% more SFT data to compensate.

**Path B: switch the modelfile to ChatML and retrain against ChatML.**
- Pros: leverages the base model's native instruction-following more fully.
- Cons: every existing wiki-chat-agent.modelfile-built deployment needs a rebuild.

The design **recommends Path B**. The cost (rebuild Modelfile, regenerate Ollama tag) is trivial; the upside (better instruction-following at lower data cost) is real. The single-template-artifact rule (§ 6.5) means the change is one-file.

**Path B template (`chat-template.jinja2`):**

```jinja
{%- if messages[0]['role'] == 'system' -%}
<|im_start|>system
{{ messages[0]['content'] }}<|im_end|>
{%- set offset = 1 -%}
{%- else -%}
{%- set offset = 0 -%}
{%- endif -%}
{%- for message in messages[offset:] -%}
<|im_start|>{{ message['role'] }}
{{ message['content'] }}<|im_end|>
{%- endfor -%}
{%- if add_generation_prompt -%}
<|im_start|>assistant
{%- endif -%}
```

Both the Unsloth `tokenizer.chat_template` and the Ollama `TEMPLATE` directive in the generated Modelfile derive from this single artifact.

### 7.2 Stage 2 — Unsloth LoRA / QLoRA training

**Skeleton (Python, no external implementation in this turn — design intent only):**

```python
from unsloth import FastLanguageModel
import torch

BASE_MODEL = "unsloth/Qwen3.5-4B-Instruct-bnb-4bit"   # confirm exact HF path before run
SEQ_LEN = 4096

model, tokenizer = FastLanguageModel.from_pretrained(
    model_name=BASE_MODEL,
    max_seq_length=SEQ_LEN,
    dtype=None,            # auto — bf16 on Blackwell
    load_in_4bit=True,     # QLoRA
)

model = FastLanguageModel.get_peft_model(
    model,
    r=16,
    target_modules=["q_proj","k_proj","v_proj","o_proj",
                    "gate_proj","up_proj","down_proj"],
    lora_alpha=16,
    lora_dropout=0,
    bias="none",
    use_gradient_checkpointing="unsloth",
    random_state=42,
    use_rslora=False,
    loftq_config=None,
)

# Tokenizer chat template loaded from single artifact
with open("MemorySmith.Core/Docs/Prompts/chat-template.jinja2") as f:
    tokenizer.chat_template = f.read()

# Train: standard TRL SFTTrainer for SFT, DPOTrainer for DPO, ORPOTrainer for ORPO.
# Unsloth wraps these with kernel optimizations.
```

**Hyperparameters (starting point, log-and-revise):**

- Batch size: 1 effective, 16 grad accumulation steps (= effective 16).
- Learning rate: 2e-4 (LoRA) or 5e-5 (full FP16 base+LoRA).
- Epochs: 3 for SFT v1; 1 for DPO v2 after SFT.
- Warmup: 5 steps.
- Optimizer: `adamw_8bit` (Unsloth-supplied).
- Weight decay: 0.0.
- LR scheduler: linear with warmup.
- Save every 100 steps; keep last 3.
- Eval every 100 steps against the held-out eval split (10% of data, deterministic split by `sourceTurnId` hash mod 10).

**Master weight tracking:**

Unsloth retains FP16 weights internally during training; the `model.save_pretrained_merged(...)` call writes them out. The harness saves **two** artifacts per run:

1. `runs/<run_id>/adapter/` — the LoRA adapter (small, ~30 MB).
2. `runs/<run_id>/merged-fp16/` — full FP16 merged weights (~9 GB). **Kept locally only**, never auto-uploaded.

The FP16 merged weights are the source of truth for re-quantization. If we later want a Q5_K_M variant or a Q8_0 variant for a different hardware target, we re-quantize from the FP16 master, not from the Q4_K_M.

### 7.3 Stage 3 — GGUF export and quantization

**One call:**

```python
model.save_pretrained_gguf(
    "runs/<run_id>/gguf",
    tokenizer,
    quantization_method="q4_k_m",
)
```

This calls llama.cpp's `quantize` under the hood. Output: `runs/<run_id>/gguf/<base-name>-q4_k_m.gguf`.

**Verification step (the harness adds this):**

```bash
llama-cli -m runs/<run_id>/gguf/...q4_k_m.gguf \
  -p "$(cat eval/smoke-prompt.txt)" \
  -n 200 --temp 0
```

If the smoke output doesn't include valid `{"toolCalls":...}` JSON when expected, **the harness halts and surfaces the failure**. We do not register a broken model with Ollama.

### 7.4 Stage 4 — Ollama Modelfile generation and registration

**Generated Modelfile (`runs/<run_id>/Modelfile`):**

```
FROM ./gguf/qwen3.5-4b-athena-q4_k_m.gguf

# Forced runtime context — must match training validation
PARAMETER num_ctx 16384
PARAMETER num_predict 2048

# Anti-looping / anti-hallucination, carried over from Athena's existing modelfile
PARAMETER temperature 0.4
PARAMETER top_p 0.9
PARAMETER top_k 40
PARAMETER repeat_penalty 1.25
PARAMETER presence_penalty 0.6
PARAMETER frequency_penalty 0.4

# Chat template — generated from chat-template.jinja2 by the harness
TEMPLATE """
{{- if .System }}<|im_start|>system
{{ .System }}<|im_end|>
{{ end }}<|im_start|>user
{{ .Prompt }}<|im_end|>
<|im_start|>assistant
"""

# System prompt — embedded from MemorySmith.Core/Docs/Prompts/wiki-chat-agent.md
SYSTEM """
# Athena — MemorySmith Wiki Chat Agent Prompt
...full content of wiki-chat-agent.md...
"""
```

**Registration commands (Python `subprocess` from the harness):**

```python
subprocess.run([
    "ollama", "create",
    f"memorysmith-athena:v{run_id}",
    "-f", f"runs/<run_id>/Modelfile"
], check=True)
```

The version tag is **monotonically increasing**, never overwritten. The "active" model is selected by updating `Chat:OllamaModel` (or the new `Training.ActiveModelTag`) in `appsettings.json` and restarting the host. **The harness does not auto-swap** — promotion is a deliberate, human-in-the-loop step (§ 12).

---

## 8. The three tuning objectives and evaluation framework

### 8.1 Objective 1 — Strict MCP tool calling

**Definition of success:** when the user's message obviously needs a tool, the model emits **exactly one** JSON object matching the envelope grammar (Appendix B), with no prose, no Markdown fence, no leading whitespace beyond a single optional newline.

**Eval dataset (~120 cases):**

- 60 happy-path cases — one expected tool call each, across all 12 read-only tools.
- 30 negative cases — message that should NOT trigger a tool (e.g. "thanks!", "summarize what you just said").
- 20 edge cases — ambiguous wording, mid-conversation tool calls, multi-tool sequences.
- 10 adversarial — prompt-injection attempts inside retrieved content (per the system prompt's "Untrusted Retrieved Data" rule).

**Scoring (per case):**

- **+1.0** — emitted JSON parses, names a valid tool, has all required args, no leading prose.
- **+0.5** — JSON parses but uses wrong tool name.
- **+0.3** — JSON parses but is wrapped in a Markdown fence.
- **+0.0** — no JSON, or JSON has Markdown surrounding it, or model leaks reasoning before the JSON.

**Pass threshold for promotion:** ≥0.85 weighted average across the 120 cases, with **zero adversarial failures**.

**Implementation:** the eval harness runs each case against the candidate model via the local Ollama API and parses the output with the same `ReadToolCalls` logic the live C# host uses (we expose it as a small library or reimplement the regex in Python — the C# parsing is mirrored 1:1 in the eval script to catch any drift).

### 8.2 Objective 2 — Deterministic Blazor-friendly markdown

**Definition of success:** answers parse cleanly through Markdig (the C# Markdown library MemorySmith uses) without warnings, contain no raw HTML, have valid Mermaid fences when present, and follow the four-shape contract (direct answer / evidence / inference / sources) when substantive.

**Eval dataset (~80 cases):**

- 40 substantive answers — questions whose ideal response invokes the four-shape structure.
- 20 short answers — questions whose ideal response is a single paragraph or sentence (no overuse of headings/lists).
- 10 Mermaid cases — questions where a diagram clarifies.
- 10 citation cases — questions whose answer requires `memory:<id>` and `page:<slug>` citations.

**Scoring (per case):**

- Programmatic checks:
  - Markdig parses without warnings: **+0.3**
  - No raw `<script>`, `<style>`, `<iframe>`, `<object>` tags: **+0.2** (security gate from Audit #5 — fail = -1.0)
  - Mermaid fences are valid syntax: **+0.2**
  - Citations use canonical patterns: **+0.2**
- LLM-judge checks (using Copilot as judge):
  - Answer follows the four-shape contract when substantive: **+0.5**
  - Answer is not overlong / preachy: **+0.3**

**Pass threshold:** ≥0.80 weighted average, with **zero raw-HTML or invalid-Mermaid failures** (these are security and UX hard fails).

### 8.3 Objective 3 — Memory tiering / classification

Depends on OQ-3 resolution. Two variants of the eval framework, pick one at design freeze:

**Variant A (new `MemoryType` enum):**

- Eval dataset: 100 (raw text → expected `{type: Episodic|Semantic|SystemConfig, status: Working|Core}`) pairs.
- Scoring: exact match per axis.
- Pass: ≥0.85 on `type`, ≥0.75 on `status`.

**Variant B (reserved-tag convention):**

- Eval dataset: 100 (raw text → expected `tags` list including one of `type:episodic` / `type:semantic` / `type:system-config`) pairs.
- Scoring: tag presence + status correctness.
- Pass: same as Variant A.

### 8.4 Eval harness mechanics

**Tool:** standalone Python script under `MemorySmith.Training/eval/`. Runs against any Ollama tag. Outputs:

- `runs/<run_id>/eval/report.json` — machine-readable scores per case + aggregates.
- `runs/<run_id>/eval/report.md` — human-readable summary with worst-N cases inlined.
- `runs/<run_id>/eval/diff.md` — diff vs the currently-active model on every case where they disagree.

**Critical: the eval runs against the SAME modelfile + parameters that production will use.** If the eval uses `temperature 0.0` and prod uses `0.4`, the eval doesn't measure what's actually shipped. The harness pins `temperature 0.0` in eval **and** a second pass at `temperature 0.4` for stability.

**Pass gate at promotion:**

```
promotion_allowed =
    objective1.score >= 0.85 AND
    objective1.adversarial_failures == 0 AND
    objective2.score >= 0.80 AND
    objective2.raw_html_failures == 0 AND
    objective3.type_score >= 0.85 AND
    objective3.status_score >= 0.75 AND
    diff_against_active.regressions <= 5  -- soft gate
```

If `regressions > 5` the harness produces a `regressions.md` listing them; promotion requires explicit override (`--force` flag on the promote command, logged in the audit chain).

---

## 9. C# ↔ Python bridge contract

### 9.1 Why a bridge at all

Two reasons:

1. **Single launch surface.** Users invoke training from the .NET app (or its CLI), not by activating a venv and remembering five commands.
2. **State coherence.** The .NET host owns the canonical `appsettings.json`, the SQLite database, the audit log. The Python harness reads from these and writes back through a known contract — no out-of-band assumptions.

### 9.2 Bridge shape — three layers

**Layer 1: Invocation wrapper (.NET).** A new project `MemorySmith.Training` (or extension to `MemorySmith.App`) exposes:

```csharp
public interface ITrainingHarness {
    Task<TrainingRunResult> StartRunAsync(TrainingRunRequest req, CancellationToken ct);
    Task<TrainingRunStatus> GetStatusAsync(string runId, CancellationToken ct);
    IAsyncEnumerable<TrainingLogLine> StreamLogsAsync(string runId, CancellationToken ct);
    Task<bool> CancelRunAsync(string runId, CancellationToken ct);
}
```

`TrainingRunRequest` includes: `BaseModelTag`, `ExportPath`, `Format` (SFT/DPO/ORPO), `Hyperparameters`, `EvalOnly` (bool), `DryRun` (bool).

**Layer 2: Process management.** The .NET layer's implementation `PythonHarnessProcess`:

- Spawns `python harness.py --run-id <ULID> --request <path-to-json> --workdir <path>` from a known venv path. The venv path is `MemorySmithOptions.Training.PythonVenvPath` with a default of `Scripts/.venv-training/`.
- The python harness reads `<request>.json`, writes status to `<workdir>/status.json` atomically (write-rename), and streams stdout/stderr line-by-line through .NET's `Process.StandardOutput`.
- The .NET wrapper parses each stdout line for a JSON envelope `{"event":"...", "data":{...}}` — anything else is logged verbatim.
- On `CancelRunAsync`, the wrapper sends `SIGTERM` then `SIGKILL` after a grace period (configurable, default 10s).

**Layer 3: Status file contract.** The Python harness writes a single JSON file `<workdir>/status.json` atomically every 5 seconds. Schema:

```json
{
  "runId": "01J9...",
  "phase": "data|train|export|eval|register|done|failed|cancelled",
  "startedAt": "...",
  "elapsedSeconds": 312,
  "lastEvent": "...",
  "metrics": {
    "step": 240, "totalSteps": 1875,
    "loss": 0.41, "evalLoss": 0.46,
    "vramGb": 5.8, "throughputTokensPerSec": 612
  },
  "gpu": { "name": "RTX 5060", "memUsedGb": 6.1, "memTotalGb": 8.0, "tempC": 71 },
  "warnings": [],
  "errors": []
}
```

The .NET wrapper polls this file (cheap), surfaces a live progress UI in Blazor, and persists summaries into the audit log on every phase transition.

### 9.3 Why a venv specifically, not a Docker container

- The user's hardware is single-tenant.
- Docker on Windows hosts adds WSL overhead and complicates GPU passthrough on consumer NVIDIA cards.
- A venv is one `python -m venv` and one `pip install -r requirements-training.txt` away.

The design **leaves the door open** for Docker by isolating the bridge contract behind `ITrainingHarness` — a future `DockerHarnessProcess` implementation can satisfy the same interface. But v1 ships venv.

### 9.4 Cancellation semantics

- `CancelRunAsync` is idempotent.
- A cancelled run leaves the `runs/<run_id>/` directory intact with `status.phase = "cancelled"`. Partial artifacts are kept (so a user can inspect what happened).
- The next run gets a new ULID. **No run is ever resumed automatically** — the harness fails loud rather than silently picking up where a previous run died. Users can opt into resume via an explicit `--resume <run_id>` flag.

### 9.5 Process lifecycle and logs

Every event line emitted to stdout takes the form `{"event": "...", "data": {...}, "ts": "..."}`. The .NET wrapper:

- Forwards each event to Serilog's structured sink at `Information` level.
- Inserts a row into a new `training_run_events` SQLite table for queryable history.
- Surfaces the latest 50 events in the Blazor progress panel.

Crashes: if the Python process exits with non-zero, the wrapper reads the last 50 stderr lines and writes them into a `crash.txt` next to `status.json`. The audit log gets one entry per crash.

### 9.6 Security posture

- The harness binary path is **not** configurable by web request. The Python venv path lives in `appsettings.json`, only.
- The harness writes only to `Data/Training/`, `runs/`, and the configured Ollama Modelfile output path. Any other path access is a bug — the design's unit tests assert this.
- The audit log (HMAC-chained) gets one entry per phase transition. Run start, eval pass/fail, model registration — all replayable from the chain.

---

## 10. Architecture diagram

```
+-----------------------------------------------------------------------------+
|                          MemorySmith.App (.NET 9, Blazor)                   |
|                                                                             |
|  +-------------------+   +-----------------------+   +-------------------+  |
|  |   Chat.razor      |-->|   MemoryChatAgent     |-->| OllamaChatProvider|  |
|  |   (thumbs UI)     |   |   (orchestrator)      |   |  HTTP -> Ollama   |  |
|  +---------+---------+   +-----------+-----------+   +---------+---------+  |
|            |                         |                         |            |
|            v                         v                         v            |
|  +-------------------+   +-----------------------+   +-------------------+  |
|  | ChatFeedback (DB) |   | ChatTurnRecord (JSONL)|   |   appsettings.json |  |
|  | thumbs ratings    |   | turn metadata + opt   |   |   ActiveModelTag   |  |
|  | + free-text notes |   | content companion file|   |   FallbackModelTag |  |
|  +---------+---------+   +-----------+-----------+   +---------+---------+  |
|            \________________________  ___________________________/          |
|                                     \/                                      |
|                          +----------+-----------+                           |
|                          |  TrainingExporter    |                           |
|                          |  (joins + redacts)   |                           |
|                          +----------+-----------+                           |
|                                     |                                       |
|                                     v                                       |
|                          +----------+-----------+                           |
|                          |  ITrainingHarness    |  <-- launched by user /   |
|                          |  PythonHarnessProcess|      scheduled job        |
|                          +----------+-----------+                           |
+-------------------------------------+---------------------------------------+
                                      |
                  spawn (Process.Start, stdout/stderr piped)
                                      |
                                      v
   +-----------------------------------------------------------------------+
   |                       Python venv (Scripts/.venv-training)            |
   |                                                                       |
   |   +----------------+   +----------------+   +-------------------+     |
   |   | harness.py     |-->|  Unsloth +     |-->| llama.cpp quantize|     |
   |   | reads request  |   |  TRL trainer   |   |   q4_k_m export   |     |
   |   | writes status  |   |  LoRA/DPO/ORPO |   +---------+---------+     |
   |   +-------+--------+   +----------------+             |               |
   |           |                                           v               |
   |           |                                +-------------------+      |
   |           |                                |  Modelfile gen    |      |
   |           |                                |  (Jinja from one  |      |
   |           |                                |   template file)  |      |
   |           |                                +---------+---------+      |
   |           |                                          |                |
   |           v                                          v                |
   |  +----------------+                       +-------------------+       |
   |  |  runs/<id>/    |                       |  ollama create    |       |
   |  |  status.json   |                       |  memorysmith-     |       |
   |  |  events.jsonl  |                       |  athena:vN        |       |
   |  |  eval/*.md     |                       +---------+---------+       |
   |  |  gguf/*.gguf   |                                 |                 |
   |  |  merged-fp16/  |                                 |                 |
   |  +----------------+                                 |                 |
   +-----------------------------------------------------|-----------------+
                                                         |
                                                         v
                                          +---------------------------+
                                          |   Ollama runtime          |
                                          |   localhost:11434         |
                                          |   serves all memorysmith- |
                                          |   athena:v* tags          |
                                          +---------------------------+
                                                         ^
                                                         |
                       Chat -> Ollama on the active tag, controlled by
                       Training.ActiveModelTag in appsettings.json
```

---

## 11. Training time and electricity cost estimates for the RTX 5060

### 11.1 Throughput assumptions

Unsloth on a 4B QLoRA at sequence length 4096 on Blackwell consumer cards delivers **~600–1000 tokens/sec** of training throughput. The 5060's memory bandwidth (~448 GB/s) is the most likely bottleneck for this size of model; the matmul TOPS are not the bottleneck.

For estimation: assume **800 tokens/sec** as a working midpoint. Confidence: **medium** (no first-party benchmark exists yet for Qwen3.5-4B on 5060; estimate is interpolated from 3090/4070 numbers in Unsloth's published runs and adjusted for the 5060's memory bandwidth).

### 11.2 Data volume scenarios

| Dataset size | Tokens per example (avg) | Total tokens | Epochs | Training tokens | Hours @ 800 tok/s |
|---|---|---|---|---|---|
| **Small (200 ex)** | 1800 | 360 K | 3 | 1.08 M | **0.4 h** (~22 min) |
| **Medium (800 ex)** | 1800 | 1.44 M | 3 | 4.32 M | **1.5 h** |
| **Large (2000 ex)** | 1800 | 3.6 M | 3 | 10.8 M | **3.8 h** |
| **DPO pass on 500 pairs** | 2400 | 1.2 M | 1 | 1.2 M | **0.4 h** |

Add ~30 minutes for data load, tokenization, Unsloth setup, eval, GGUF export, and Ollama registration. **Practical wall-clock totals:** 50 min (small) to ~4.3 hours (large + DPO).

### 11.3 Electricity cost

Per § 2.4 assumption of ~230 W sustained system draw:

| Wall-clock | kWh | US national avg ($0.17/kWh) | EU avg (~$0.35/kWh) | Off-peak ($0.10/kWh) |
|---|---|---|---|---|
| 1 h | 0.23 | $0.04 | $0.08 | $0.02 |
| 4 h | 0.92 | $0.16 | $0.32 | $0.09 |
| Weekly (4 h x 4) | 3.68 | $0.63 | $1.29 | $0.37 |

The electricity bill is negligible at solo-developer cadence. **The dominant cost is wall-clock time, not money.**

### 11.4 Cloud comparison

For perspective:

| Option | Hardware | Time for 4 h-equivalent run | Cost @ list price |
|---|---|---|---|
| **5060 (local)** | 8 GB Blackwell | 4 h | ~$0.16 electricity |
| A100 80 GB (Lambda) | 80 GB Ampere | ~45 min (faster mem bandwidth + bigger batch) | ~$1.35 @ $1.80/h |
| H100 80 GB (CoreWeave/Lambda) | 80 GB Hopper | ~25 min | ~$1.65 @ $3.95/h |
| RunPod A40 | 48 GB Ampere | ~1 h | ~$0.80 @ $0.79/h |

**Conclusion:** for runs under ~6 hours, the 5060 is the right answer — no cloud setup tax, no data egress, no privacy concerns. **Above 6 hours, consider renting an A40 or A100 hour.** The harness's bridge contract is hardware-agnostic; a future implementation can target a RunPod instance behind the same `ITrainingHarness` interface.

### 11.5 First-run estimate (recommended)

- Small SFT (~200 examples, 3 epochs, sequence 4096): **~30 minutes**.
- Including eval pass on the candidate model: **~45 minutes**.
- Including GGUF export + Ollama registration: **~50 minutes**.

A user opting in for the first time and saying "fine-tune now" sees a result within an hour. That's the right shape for a "lunch break" feedback loop.

---

## 12. Promotion and rollback workflow

### 12.1 Promotion gate

After a run finishes with all eval gates green, the harness writes:

```
runs/<run_id>/promotion-candidate.json
{
  "runId": "01J9...",
  "ollamaTag": "memorysmith-athena:v17",
  "evalScores": {...},
  "currentActive": "memorysmith-athena:v15",
  "diffSummary": {"regressions": 2, "improvements": 8, ...},
  "humanApprovalRequired": true
}
```

The Blazor admin page renders this with a side-by-side comparison and a "Promote" button. Clicking it:

1. Writes a new entry to the audit log (`AuditEventType: ModelPromoted`, chained).
2. Updates `Training.ActiveModelTag` in `appsettings.json` (with backup at `appsettings.json.bak`).
3. Triggers a soft restart of the chat path (the `IChatAgent` re-resolves options on next request — no full app restart needed if `IOptionsMonitor<>` is in use).
4. Writes a record into a new `model_history` SQLite table linking the runId to a wall-clock activation time.

### 12.2 Shadow eval (optional)

A `Training.ShadowEvalEnabled` toggle, off by default. When on:

- For every live chat turn, the candidate model is ALSO queried (asynchronously, results not surfaced to the user) and its response is logged with the same metadata as the active model's response.
- The eval harness aggregates shadow vs active over a window (default 100 turns) and reports drift.
- Useful for catching live-traffic regressions that the eval dataset missed.

VRAM cost: shadow eval loads the candidate model **alongside** the active model, doubling weight memory. On an 8 GB card this is impractical. Recommendation: **disable shadow eval on 8 GB cards. Use sequential A/B: alternate active model day-to-day for a week, collect feedback, compare.**

### 12.3 Rollback

One-button:

1. Admin page lists `model_history` in reverse chronological order.
2. Click "Roll back to memorysmith-athena:v15".
3. The harness writes the previous tag into `Training.ActiveModelTag`, records the rollback in the audit log.
4. The Ollama tag itself is not deleted — old tags persist on disk until the user explicitly prunes them via a new `Scripts/prune-old-tags.ps1` (which warns before deleting anything younger than 90 days).

### 12.4 Failure modes the workflow handles

- **Eval pass but live regression:** caught by user thumbs-down spike. The trend dashboard alerts (`Training.RollbackOnFeedbackSpike`, default off).
- **GGUF export silently corrupted:** caught by the smoke test in § 7.3.
- **Ollama tag collision:** new runs always use `v<runId>` (monotonic). Never overwrite.
- **Modelfile drift between train and deploy:** prevented by the single-template artifact (§ 6.5, § 7.1).

---

## 13. Cross-references to Audit #5 findings

The Audit #5 file `[[FILE_n3nctv7l]]` surfaces several issues that the harness either resolves, defers, or must respect. Tracking explicitly:

| Audit #5 finding | Severity | This design's interaction |
|---|---|---|
| Clipboard-paste silently fetches external image URLs (`memorysmith.js:813-832`) | HIGH | **Untouched** — thumbs UI is pure Blazor, no new JS. Recommend Sprint B continues independently. |
| `ChatReferenceLinkPolicy.FilterToAllowedTargets` only filters `href`, not event handlers | HIGH | **Untouched** — out of scope for fine-tuning. |
| Mermaid `innerHTML` XSS surface | HIGH | **Tangentially touched** — Objective 2 trains the model to produce only valid Mermaid; that doesn't fix the XSS surface, but it reduces the attack surface in practice. |
| BOM-prefixed JSON files trigger spurious reindex churn | HIGH | **Untouched** — code-search concern. |
| 23 configurability gaps | MEDIUM | **Respected** — the harness's three new toggles (`ChatTranscriptEnabled`, `StoreChatContent`, `FeedbackEnabled`) are gated by `SecurityProfile` defaults. Default-off on `secure-local` and `remote-hardened`. |
| Tier 1 killer features | INFO | **F2 (Inline citations with confidence)** is improved by Objective 2 training. **F1 (Spotlight palette)** is out of scope. |
| FileMemoryStore.Save status-change order | P0 | **Untouched** — Sprint A territory. The training data export reads from the consolidated stores, so a Sprint A fix improves training data integrity for free. |

### 13.1 What this design does NOT regress

- **Audit log HMAC chain** stays intact. New `ModelPromoted` events extend the chain.
- **SecurityProfile-driven defaults** are the model for the new `TrainingOptions` toggles.
- **No new JS interop** is added. The clipboard-fetch and Mermaid `innerHTML` findings are not aggravated.

### 13.2 What this design depends on (Sprint A pre-reqs)

- **SafeFileWriter** for atomic JSONL appends (per Audit #5 Sprint A). The training transcript writer would benefit but doesn't strictly require it — the design notes the dependency.
- **FileMemoryStore status-change fix.** Affects training data quality if Memory records flip status mid-export. Not blocking.

---

## 14. Open questions, assumptions, confidence values

Each open question (OQ) needs a decision before implementation. Each assumption (A) is what the design is built on; if any flips, the relevant section needs revisiting.

### Open questions

**OQ-1:** Is the user's stated "Qwen 3.5 4b" the Ollama-published `qwen3.5:4b` tag, or a custom local build derived from the upstream HF model? *Default assumption: the Ollama tag.* Decision impact: **low** — the Modelfile generation is the same either way; only the base model HF path in `harness.py` changes.

**OQ-2:** Path A (custom template, current) vs Path B (ChatML, recommended) for the chat template? *Recommendation: B.* Decision impact: **medium** — affects training data volume requirement and one-time migration effort.

**OQ-3:** New `MemoryType` enum vs reserved-tag convention for Episodic/Semantic/SystemConfig? *Recommendation: tag convention first, enum if it doesn't stick.* Decision impact: **medium** — affects SQLite schema, MemoryRecord model, training labels.

**OQ-4:** Should the harness ship with synthetic-data examples for tools the maintainer hasn't yet used in chat? *Recommendation: yes — a curated baseline for every tool in Appendix A ships in the repo.* Decision impact: **low** — work effort only.

**OQ-5:** Should `appsettings.json` writes on promotion happen via the running app or via an out-of-band edit (Scripts/promote.ps1)? *Recommendation: in-app via `IOptionsMonitor<>` with file-watch reload.* Decision impact: **low**.

**OQ-6:** What's the failure-mode taxonomy for the optional `note` on thumbs-down? *Recommendation: free-form text v1, classify post-hoc with the model itself.* Decision impact: **low**.

**OQ-7:** Does the user want a CI integration (run eval on every commit, gate merges on score)? *Default: no.* Decision impact: **medium** — would justify a separate sub-design.

### Assumptions

**A-1:** The `qwen3.5:4b` GGUF includes a tokenizer that matches the upstream HF tokenizer Unsloth pulls. *If false:* the Modelfile's `TEMPLATE` and the trained tokenizer chat_template will mismatch, producing token-level corruption. **Mitigation:** a `verify_tokenizer_parity.py` step in stage 1 hashes both tokenizer vocab lists and fails fast on drift.

**A-2:** The RTX 5060 supports the BF16 dtype Unsloth prefers on Blackwell. *Confidence: high* (Blackwell adds native FP4 too).

**A-3:** Ollama's `num_ctx` parameter is honored at runtime for `qwen35` architecture as it is for the rest of the Qwen family. *Confidence: high.*

**A-4:** The user's primary use case is text + tool calling, not image-input chat. The vision pathway exists but is not the fine-tune target. *Decision: confirm with user — out of scope for v1 if confirmed.*

**A-5:** The codebase will not aggressively refactor `MemoryChatAgent` or `ChatToolCatalog` in the near term. *If the surface changes, the synthetic dataset needs an update.*

**A-6:** The user has Python 3.11 or 3.12 available, NVIDIA driver ≥ 565 for Blackwell, CUDA 12.4+. *The harness's bootstrap script verifies and fails loud if not.*

### Confidence ratings on the design

| Section | Confidence | Notes |
|---|---|---|
| § 2 hardware envelope | **medium-high** | Numbers are estimates; harness runs verification |
| § 3 model dossier | **high** | Verified against live Ollama page |
| § 4 codebase grounding | **high** | Audited via GitHub MCP, file paths confirmed |
| § 5 logging + thumbs schema | **high** | Standard pattern, no surprises |
| § 6 export format recommendation | **medium-high** | Volume estimate could be off ±3x |
| § 7 training pipeline | **high** | Unsloth officially supports Qwen3.5-4B |
| § 8 eval framework | **medium** | Pass thresholds will need a first-run calibration |
| § 9 bridge contract | **high** | Plain `Process` + JSONL conventions, well-trodden |
| § 11 cost estimates | **medium** | 5060-on-Qwen3.5 throughput is interpolated |
| § 12 promotion workflow | **high** | Aligns with existing audit log primitives |

---

## 15. Appendix A — full tool roster (fine-tuning target)

The harness fine-tunes the model to produce the exact tool envelopes for these (source: `ChatToolCatalog.cs` and `wiki-chat-agent.md`, sorted by risk class):

**ReadOnly (in chat, in MCP, in agent):**

1. `memorysmith_unified_search` — natural-language wiki query
2. `memorysmith_hybrid_search` — balanced conceptual + literal
3. `memorysmith_semantic_search` — strongly conceptual
4. `memorysmith_search` — literal/exact terms
5. `memorysmith_context_pack` — root + references + conflict-aware
6. `memorysmith_get` — known memory id
7. `memorysmith_page_search` — Markdown page search
8. `memorysmith_page_get` — known slug
9. `memorysmith_task_list` — filter tasks
10. `memorysmith_task_get` — known task id
11. `memorysmith_code_search` — code/symbol/file search
12. `memorysmith_code_search_status` — index build status

**SensitiveRead (MCP only, NOT in chat):**

13. `memorysmith_source_bundle`
14. `memorysmith_find_by_source`

**Write (MCP + agent, NOT in chat):**

15. `memorysmith_task_create`
16. `memorysmith_task_update`
17. `memorysmith_task_set_status`
18. `memorysmith_task_add_comment`
19. `memorysmith_task_add_attachment`
20. `memorysmith_page_save`
21. `memorysmith_page_delete`

The fine-tune trains exclusively against tools 1–12 in chat-mode contexts. Tools 13–21 are trained in agent-mode contexts only. Cross-mode contamination (model emits a write call in chat mode) is treated as a hard fail in eval Objective 1.

---

## 16. Appendix B — JSON envelope grammar and rejection cases

### B.1 The canonical envelope

```json
{"toolCalls":[{"name":"<tool_name>","arguments":{<JSON object>}}]}
```

### B.2 Acceptance rules

- Outermost object MUST be `{}` (not array).
- MUST have exactly one top-level key: `toolCalls`.
- `toolCalls` MUST be an array.
- Each entry MUST have exactly two keys: `name` (string) and `arguments` (object).
- `name` MUST be in the read-only set in chat mode; in the write set in agent mode.
- `arguments` MUST conform to the tool's argument schema (validated by `ChatToolCatalog`).
- The whole JSON MUST be emitted as the **sole** content of the assistant message. No prose before. No prose after. No Markdown fence.

### B.3 Rejection cases (must fail in eval)

- Wrapped in `` ```json ... ``` `` fence.
- Preceded by reasoning text like "Sure, let me search for that. " before the JSON.
- Followed by "Let me know if that helps!" after the JSON.
- Multiple JSON objects concatenated.
- `toolCalls` field missing.
- `name` field is null or empty.
- `arguments` field is a string instead of an object.

### B.4 Question card envelope (alternate)

```json
{"questionCard":{"question":"<text>","detailsMarkdown":"<text>","options":["<o1>","<o2>"],"other":{"label":"Other","placeholder":"Type another answer"},"responsePrefix":"Answer to follow-up question"}}
```

Same prose-free isolation rules apply.

### B.5 Agent-mode envelope

```json
{"reply":"<markdown>","memoryWrites":[],"pageWrites":[]}
```

`memoryWrites` and `pageWrites` MUST be arrays even when empty. The outer object MUST NOT be wrapped in a Markdown fence.

---

## 17. Appendix C — chat template drift hazard

The single largest silent-failure risk in this pipeline is **chat template drift** between training and inference. The hazard:

- Unsloth defaults to the upstream model's `tokenizer.chat_template`.
- The Ollama Modelfile defaults to whatever `TEMPLATE` was written into it (today: custom minimalist).
- The C# `BuildMessages` in `MemoryChatAgent` constructs a list of `{role, content}` dicts and hands them to `api/chat`, which formats them using the Modelfile's template.

If these three diverge, the model trained on `<|im_start|>system\n...<|im_end|>` will, at inference, see something like `<|system|>\n...` and produce garbage on the first token.

### C.1 The single-source rule

`MemorySmith.Core/Docs/Prompts/chat-template.jinja2` is the canonical artifact. The harness:

1. Reads it during training and assigns to `tokenizer.chat_template`.
2. Reads it during Modelfile generation and substitutes Jinja → Go-template syntax (Ollama uses Go templates, not Jinja). The translation is mechanical.
3. The .NET host doesn't directly format — it sends structured messages to Ollama. **But the host MUST not invent its own templating.** Any C# string-concatenation that formats `<|...|>` tokens is a bug.

### C.2 Verification step

The first training run includes a `verify_chat_template.py` step that:

1. Tokenizes a synthetic 5-turn conversation under `tokenizer.chat_template`.
2. Calls Ollama's `api/show` to get the registered Modelfile template.
3. Tokenizes the same conversation under the Modelfile template (mocked through llama.cpp).
4. Diffs the two token sequences. Any difference fails the run.

### C.3 Migration plan if Path B is chosen

If we adopt ChatML (Path B in § 7.1):

1. Regenerate `wiki-chat-agent.modelfile` from the new template.
2. Rebuild any local Athena Ollama tags that were built off the old template.
3. The fine-tuned model uses ChatML from day one — no migration needed for it.
4. Users who never ran a local Athena build (default `gemma4:e4b` fallback) are unaffected.

---

## End of design.

**Next turn (deferred, on user approval):** scaffold the implementation in three commits:

1. The C# side — `TrainingOptions`, `ChatFeedback` SQLite migration, `ChatTurnRecord` JSONL writer, thumbs Blazor UI, `ITrainingHarness` interface + `PythonHarnessProcess`, `Scripts/promote.ps1`, `Scripts/rollback.ps1`.
2. The Python side — `MemorySmith.Training/harness.py`, the requirements file, the chat-template artifact, the eval harness skeleton, the synthetic-data starter pack.
3. The eval-and-promote glue — Blazor admin page for promotion candidates, eval report rendering, audit log integration.

A reasonable first run with the design above produces an `memorysmith-athena:v1` tag in under an hour from a green-pipeline start.
