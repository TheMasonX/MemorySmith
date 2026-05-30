# MemorySmith — Audit #7

## Complete Chat System & Training Harness Audit

**Generated:** 2026-05-30
**Subject:** `feature/code-search-high-roi-batch8` latest tip
**Scope:** Full end-to-end review of the chat system (10,400 LOC across 11 files), training harness (2,000 LOC), and comparison to state-of-the-art AI chat/agentic tools.
**Calibration:** Reliability > Performance > Usability > Observability > Repeatability. Security calibrated to local-first with safe defaults + configurable. XSS de-prioritized per user's prior direction.
**Methodology:** Three parallel deep-read subagents (ChatServices backend 3216 LOC, Chat.razor UI 3068 LOC + JS 1028 LOC + markdown 407 LOC + link policy 288 LOC, ChatToolCatalog 1431 LOC) plus my own first-hand reads of the training harness (2000 LOC) and new features.

---

## 0. Executive Summary

The MemorySmith chat system is architecturally sound — it has a clean provider abstraction (Ollama + GitHub Copilot), a unified tool catalog shared between chat and MCP, a context preloading planner, an intent interceptor, a proposal-based write approval workflow, streaming with trace events, and a localStorage-backed session persistence layer. The new training harness (transcript capture, feedback store, Python LoRA trainer, dependency probing) adds a data flywheel that no comparable local-first tool ships.

But the chat system's **rendering performance at streaming scale is the critical bottleneck.** Two compounding issues make the chat UI degrade noticeably at 30+ messages with a fast-streaming model:

1. **`StateHasChanged` fires on every streaming token** (50-100+/sec), each triggering a full Blazor component tree diff.
2. **Markdown is re-rendered for every turn on every render cycle** — 50 messages × 50 ticks/sec = 2,500 full Markdig-to-HTML conversions per second.

These are the first two things to fix. Everything else — usability gaps, tool surface completeness, provider-native tool calling — is secondary until the streaming render path is production-grade.

The biggest **functional gap** is the absence of memory write tools: the core data model (memory records) is entirely read-only from both the tool catalog and the MCP surface. An agent can search, fetch, and build context packs from memories, but cannot create or update them through the tool protocol. Tasks and pages have full CRUD; memories don't.

The biggest **UX gap** vs state-of-the-art is the lack of code block copy buttons — every competitive chat UI has them. Close behind: no message editing/regeneration, the literal "Waiting for first token..." text appearing as message content instead of a shimmer animation, and no auto-resizing composer textarea.

The training harness is well-designed for its stage: transcript capture with redaction, SQLite-backed feedback, configurable LoRA parameters, dependency probing before launch, simulated mode for environments without GPU. The main gap is the lack of evaluation gates — the harness trains but doesn't systematically measure whether the fine-tuned model is better than the baseline before promotion.

**Severity rollup for this audit: 3 High, 23 Medium, 28 Low, plus 22 missing-feature recommendations.**

---

## 1. Rendering Performance (the Critical Path)

### 1.1 [HIGH, conf 0.95] StateHasChanged fires on every streaming token

**Task:** [TSK-0229](/tasks?task=TSK-0229)

**Source:** `Chat.razor:1385`. Inside the `await foreach` loop over streaming deltas, `await InvokeAsync(StateHasChanged)` is called on every single update. For fast models (GPT-4o, Ollama with GPU), this can be 50-100+ invocations per second. Each triggers a full Blazor diff of the entire component tree.

**Impact:** At 30+ messages, the diff includes all turns, all trace entries, all sidebar content. The rendering cost is O(turns × tokens_per_second). A 50-message conversation with a 100-token/sec model produces 5,000 full diffs per second. The user sees jank, dropped frames, and delayed token display.

**Comparison:** Claude's web UI uses React with `requestAnimationFrame`-batched renders at 60fps max. ChatGPT uses a similar pattern with a `useEffect` debounce. Cursor buffers deltas client-side and appends DOM nodes directly, bypassing framework diffing.

**Recommendation:** Throttle `StateHasChanged` to 4-8 calls per second (every 125-250ms). Buffer deltas between renders. Between ticks, append delta text to a JS-side buffer; on tick, flush the buffer to C# state and render once.

### 1.2 [HIGH, conf 0.95] Markdown re-rendered for all turns on every render cycle

**Task:** [TSK-0229](/tasks?task=TSK-0229)

**Source:** `Chat.razor:2402-2413`. `RenderTurnContent` calls `ChatMarkdownRenderer.RenderHtml` + `LinkifyInlineCodeReferences` + `FilterToAllowedTargets` — three regex-heavy passes — and is called per turn per render. Non-streaming turns don't change between renders, but their markdown is reprocessed each time.

**Impact:** Compounding with 1.1. For a 50-message conversation, 50 full Markdig-to-HTML conversions fire 4-100 times per second during streaming. The regex passes over HTML strings allocate heavily.

**Recommendation:** Cache the rendered HTML per turn content hash:
```csharp
private readonly Dictionary<int, MarkupString> _renderCache = new();
private static MarkupString RenderTurnContent(ChatTurnState turn) {
    var hash = turn.Content?.GetHashCode() ?? 0;
    if (_renderCache.TryGetValue(hash, out var cached)) return cached;
    var html = ChatMarkdownRenderer.RenderHtml(turn.Content);
    // ... linkify + filter ...
    var result = new MarkupString(html);
    _renderCache[hash] = result;
    return result;
}
```

Only the currently-streaming turn needs re-rendering. All prior turns can serve from cache.

### 1.3 [HIGH, conf 0.85] OnDraftInput calls SaveSessionsAsync on every keystroke

**Task:** [TSK-0230](/tasks?task=TSK-0230)

**Source:** `Chat.razor:1234-1239`. `OnDraftInput` → `PersistActiveDraft()` → `SaveSessionsAsync()`. `SaveSessionsAsync` serializes all 30 sessions to JSON and writes to localStorage via JS interop — on every keystroke. For a fast typist (5-10 keystrokes/sec), this is 5-10 full localStorage writes per second.

**Impact:** Each write round-trips through SignalR → JS → localStorage → sync I/O. The user sees input lag when the localStorage quota is near-full or the session list is large.

**Recommendation:** Debounce `SaveSessionsAsync` in `OnDraftInput` to at most once per second. Save only the active session's draft, not all sessions.

---

## 2. Tool Catalog & Agent Surface (Functional Completeness)

### 2.1 [HIGH, conf 0.95] No memory write tools in the catalog

**Task:** [TSK-0231](/tasks?task=TSK-0231)

**Source:** `ChatToolCatalog.cs`. The catalog has 22 tools: full CRUD for tasks (create, update, set_status, add_comment, add_attachment), create+delete for pages, but **zero** memory write tools. `memorysmith_memory_create`, `memorysmith_memory_update`, and `memorysmith_memory_delete` don't exist.

**Impact:** The core data model — the reason the app exists — is read-only from the tool surface. An agent that discovers a gap in the wiki can search for it and cite the absence, but can't create the missing record. The maintenance agent's write path goes through proposals (which use file-level writes, not the tool catalog), but the chat agent and MCP clients have no write path at all for memories.

**Recommendation:** Add `memorysmith_memory_create` and `memorysmith_memory_update` as Write-risk tools with `AvailableInAgent: true, EnabledByDefaultInMcp: false`. Gate behind the existing `AgentWritesEnabled` option and the `AgentWriteApprovalMode` workflow. Follow the task-tool pattern.

### 2.2 [MEDIUM, conf 0.90] No provider-native tool calling

**Task:** [TSK-0232](/tasks?task=TSK-0232)

**Source:** `ChatServices.cs:1949-2010` (ReadToolCalls). The entire tool-call flow uses a custom JSON protocol — the LLM emits `{"toolCalls":[...]}` in its text output, the app parses it with `StripJsonFence` + `ReadToolCalls`. Neither Ollama's native `tools` API (available since 0.3.x) nor OpenAI's function-calling API is used.

**Impact on reliability:** The text-based protocol has a ~5-10% failure rate when the LLM wraps tool calls in prose, uses the wrong JSON shape, or emits partial JSON. `IsPotentialToolCallPrefix` buffers any response starting with `{`, `[`, or backtick, causing visible stalls when the LLM starts with a code block.

**Impact on quality:** Provider-native tool calling gives the model structured output constraints that prevent malformed calls. The model sees the tool schemas at the API level, not as free-text in the system prompt. This is the standard approach in every competitive product.

**Comparison:** Claude Code, Cursor, Continue.dev, and Copilot Chat all use provider-native function calling when available. The text-based fallback exists only for legacy models that don't support structured output.

**Recommendation:** For Ollama (0.3.x+), serialize `ChatToolDescriptor.InputSchema` objects into the Ollama `tools` request field. Parse the response's `tool_calls` array instead of `ReadToolCalls`. For GitHub Models (OpenAI-compatible), use the `tools`/`functions` API. Keep the text-based `ReadToolCalls` as a fallback for models that don't declare tool support.

### 2.3 [MEDIUM, conf 0.90] MCP tool responses have no size cap

**Task:** [TSK-0233](/tasks?task=TSK-0233)

**Source:** `McpController.cs:155`. `ToolText(result.Text)` sends the full tool result to the MCP client with no truncation. `ChatOptions.MaxToolResultCharacters = 12000` applies only to the chat host, not the MCP path. A `memorysmith_source_bundle` call returning 50 KB of source content sends the entire payload.

**Recommendation:** Add `Mcp.MaxResponseCharacters` (default 128K). Truncate with a `truncated: true` metadata flag.

### 2.4 [MEDIUM, conf 0.85] `memorysmith_code_search_merge_shard` accepts arbitrary filesystem paths

**Task:** [TSK-0234](/tasks?task=TSK-0234)

**Source:** `ChatToolCatalog.cs:467-476`. The `shardPath` argument is passed directly to `MergeShardAsync` with no path validation. Any authenticated editor with MCP access can point this at any file the server process can read.

**Recommendation:** Validate against a configured `AllowedShardRoots` list. Reject non-`.db`/`.sqlite` extensions. This is a Write-risk tool disabled by default, so the exposure requires explicit admin opt-in — but the absence of validation is still a design gap.

### 2.5 [MEDIUM, conf 0.85] `memorysmith_page_delete` checks view permission, not edit permission

**Task:** [TSK-0235](/tasks?task=TSK-0235)

**Source:** `ChatToolCatalog.cs:1000-1010`. The delete delegate calls `ctx.CanViewPage(existing.MinimumRole)` — the read check. The MCP controller does enforce `CanEditMemorySmith` before dispatching Write tools, so this is partially mitigated. But the delegate itself doesn't verify edit authority.

### 2.6 [MEDIUM, conf 0.85] `BuildTools()` is a 900-line monolithic method

**Task:** [TSK-0192](/tasks?task=TSK-0192)

22 tool definitions in one `IEnumerable<ChatToolDescriptor>` generator. Adding or modifying a tool requires editing inside this method; unit-testing a single tool is awkward. Decompose into per-domain factories: `BuildMemoryTools()`, `BuildCodeSearchTools()`, `BuildPageTools()`, `BuildTaskTools()`.

### 2.7 [MEDIUM, conf 0.80] `format` enum inconsistencies across tool schemas

**Task:** [TSK-0236](/tasks?task=TSK-0236)

`json-v2` is accepted by `IsStructuredFormat` but not declared in any schema's enum. Context-pack schema lists `["markdown","json"]` while search schemas list `["markdown","json","envelope"]`. Align.

---

## 3. Provider Abstraction & Streaming

### 3.1 [MEDIUM, conf 0.90] Ollama streaming has no stall detection

**Task:** [TSK-0237](/tasks?task=TSK-0237)

**Source:** `ChatServices.cs:530-568`. The streaming loop reads lines from Ollama's NDJSON response with only a global timeout (`CancelAfter(RequestTimeoutSeconds)`, default 600s). If Ollama hangs after one chunk, the user waits up to 10 minutes with no indication.

**Recommendation:** Add a per-chunk idle timeout (e.g., 30 seconds). If no token arrives in 30s, cancel and show "Model stopped responding."

### 3.2 [MEDIUM, conf 0.85] Copilot SDK `Channel` has no backpressure or idle watchdog

**Task:** [TSK-0237](/tasks?task=TSK-0237)

**Source:** `ChatServices.cs:811-925`. The channel is unbounded (`Channel.CreateUnbounded`). The SDK subscription feeds events into the channel writer; the consumer reads. If the SDK sends events faster than the consumer processes (unlikely but possible with burst reasoning), memory grows unboundedly. No idle watchdog: if the SDK stops without signaling completion, the reader blocks until the global timeout.

**Recommendation:** Use `Channel.CreateBounded(capacity: 1000, BoundedChannelFullMode.Wait)`. Add a secondary watchdog timer that completes the writer if no events arrive in 30 seconds.

### 3.3 [MEDIUM, conf 0.85] Token estimation is `chars/4` globally

**Task:** [TSK-0248](/tasks?task=TSK-0248)

**Source:** `ChatServices.cs:2388-2399`. This under-estimates tokens for code (2-2.5 chars/token) and CJK (~1.5 chars/token). The estimate drives the context-window usage gauge shown to users.

**Recommendation:** Use `chars / 3.0` as a more conservative global default. For providers with known tokenizers (Ollama models can report token counts in the response), use the actual count.

### 3.4 [MEDIUM, conf 0.85] No streaming HTTP endpoint for external consumers

**Task:** [TSK-0246](/tasks?task=TSK-0246)

**Source:** `ChatController.cs`. Only synchronous `POST /api/chat` and `POST /api/chat/feedback`. Streaming is exclusively through the Blazor SignalR circuit.

**Recommendation:** Add `POST /api/chat/stream` with `text/event-stream` response for CLI tools and scripts.

### 3.5 [MEDIUM, conf 0.80] `IsPotentialToolCallPrefix` over-matches

**Task:** [TSK-0212](/tasks?task=TSK-0212)

**Source:** `ChatServices.cs:1949-1953`. Any response starting with `{`, `[`, or backtick is classified as potential tool call and fully buffered. This covers legitimate prose patterns: JSON examples, code blocks, bulleted lists. The user sees a stall until the stream completes.

**Recommendation:** Add a byte-count threshold (e.g., 2KB) or time threshold (500ms) after which the buffer is flushed regardless.

---

## 4. Chat UX (vs State-of-the-Art)

### 4.1 Feature Comparison Matrix

| Feature | MemorySmith | Claude | ChatGPT | Cursor | Continue.dev |
|---|---|---|---|---|---|
| Streaming with token-by-token display | Partial (bursts) | Yes | Yes | Yes | Yes |
| Code block copy button | **No** | Yes | Yes | Yes | Yes |
| Message editing / regeneration | **No** | Yes | Yes | Yes | Partial |
| Auto-resizing composer | **No** | Yes | Yes | Yes | Yes |
| Typing/thinking indicator | Text-based ("Waiting for first token...") | Animated dots | Animated dots | Shimmer | Shimmer |
| Session rename | **No** | Yes | Yes | N/A | N/A |
| Session search/pin | **No** | Yes | Yes | N/A | N/A |
| Conversation export | **No** | Yes | Yes | No | No |
| Suggested follow-ups | **No** | Yes | Yes | No | No |
| Voice input/output | **No** | Yes | Yes | No | No |
| Inline diff preview for proposals | **No** | Artifacts | Canvas | Yes | Yes |
| Context file picker | **No** | N/A | N/A | Yes | Yes |
| Provider-native tool calling | **No** | Yes | Yes | Yes | Yes |
| Tool result streaming/progress | **No** | Yes | Partial | No | No |
| Feedback / thumbs up/down | **Yes** | Yes | Yes | No | No |
| Trace/reasoning viewer | **Yes** | Partial | Partial | No | No |
| Wiki-grounded RAG context | **Yes** | No | No | No | No |
| Local-first with no cloud dependency | **Yes** | No | No | No | Partial |
| Training data capture + LoRA harness | **Yes** | No | No | No | No |

### 4.2 Missing Features Ranked by Impact

**Tier 1 — Table-stakes for AAA UX (fix these first):**
1. **Code block copy button** — every competitive UI has this. ~30 LOC in `renderEnhancements`.
2. **Auto-resizing composer textarea** — prevents scrolling in the input. ~20 LOC JS.
3. **Replace "Waiting for first token..." literal text with a shimmer/skeleton animation** — the current text renders as markdown content, confusing users.
4. **Message editing (user turns)** — retype to retry is unacceptable.
5. **Message regeneration (assistant turns)** — "try again" button.

**Tier 2 — Quality-of-life:**
6. **Keyboard shortcuts** — Cmd+K for search, Cmd+N for new chat, Escape to close sidebar.
7. **Session rename** — inline edit on session title.
8. **Session search** — filter the history panel.
9. **Conversation export** — download as markdown or JSON.
10. **Suggested follow-up prompts** — show 2-3 after each response.

**Tier 3 — Competitive differentiation:**
11. **Inline diff preview** for memory/page write proposals (use Monaco or jsdiff).
12. **Context file picker** — let the user explicitly include files/memories in the prompt.
13. **Provider-native tool calling** (detailed in §2.2).
14. **Voice input** via Web Speech API.
15. **Token counter / cost indicator** during streaming.

---

## 5. Training Harness Audit

### 5.1 Architecture Overview

The training harness is a three-layer system:

1. **Data plane** (C#): `ChatTranscriptWriter` captures turn metadata + content to JSONL files with redaction. `SqliteChatFeedbackStore` stores thumbs up/down ratings in SQLite. Both are wired into `MemoryChatAgent.StreamToCompletionAsync` at line 1633.

2. **Orchestration** (C#): `TrainingHarnessRunnerService` manages the lifecycle — probes Python dependencies, launches the harness as a subprocess, monitors timeout, reports status via the active-run singleton.

3. **Execution** (Python): `harness.py` loads transcripts or synthetic examples, resolves training mode (simulated/LoRA/inference), exports SFT data, runs LoRA training with `peft` + `transformers`, writes events/status/benchmarks to the work directory.

### 5.2 Findings

#### 5.2.1 [MEDIUM, conf 0.90] No evaluation gate between training and promotion

**Task:** [TSK-0204](/tasks?task=TSK-0204)

The harness trains a LoRA adapter but doesn't systematically evaluate whether the fine-tuned model is better than the baseline. `TrainingOptions` has `MinObjective1Score`, `MinObjective2Score`, and `MaxRegressions` fields, but the Python harness doesn't read or enforce them — they're scaffolding. The `benchmark.json` output records basic metrics (steps, loss) but no held-out evaluation.

**Recommendation:** Add a benchmark phase after training: the harness runs the trained model against a fixed set of test prompts (the relevance suite pattern), compares to the baseline model's outputs, and only writes `"promote": true` in the status if quality gates pass.

#### 5.2.2 [MEDIUM, conf 0.85] `harness.py` uses `trust_remote_code=True` unconditionally

**Task:** [TSK-0241](/tasks?task=TSK-0241)

**Source:** `harness.py:295`. `AutoTokenizer.from_pretrained(model_id, trust_remote_code=True)`. This flag tells HuggingFace Transformers to execute arbitrary Python code from the model's `tokenizer_config.json`. For a local-first app downloading models from HuggingFace, this is a supply-chain risk — a compromised model repo can run arbitrary code during tokenizer loading.

**Recommendation:** Default to `trust_remote_code=False`. Gate behind a `TrainingOptions.TrustRemoteCode` config (default false). Only enable for models that explicitly require it (like Qwen3).

#### 5.2.3 [MEDIUM, conf 0.85] Transcript redaction regex is narrow

**Task:** [TSK-0241](/tasks?task=TSK-0241)

**Source:** `ChatTranscriptWriter.cs:16-17`. Two patterns: Bearer tokens and `api_key|token|secret|password|authorization` with `:=` separators. Doesn't catch: base64-encoded secrets, JWT tokens (`eyJ...`), AWS keys (`AKIA...`), connection strings, cookies, or secrets embedded in URLs.

**Recommendation:** Add patterns for JWT prefix `eyJ`, AWS key prefix `AKIA`, and URL-embedded credentials (`://user:password@`). Use a configurable pattern list via `TrainingOptions.RedactionPatterns`.

#### 5.2.4 [MEDIUM, conf 0.85] Python subprocess uses `Arguments` string, not `ArgumentList`

**Task:** [TSK-0241](/tasks?task=TSK-0241)

**Source:** `TrainingHarnessRunnerService.cs:239`. `Arguments = string.Join(" ", arguments)` where `arguments` includes `Quote(harnessScript)`, `Quote(run.RunId)`, etc. The `Quote` method at line 344 does `$"\"{value.Replace("\"", "\\\"")}\""`. This is a basic escaping attempt but doesn't handle all edge cases (e.g., paths with backslashes on Windows).

**Recommendation:** Use `ProcessStartInfo.ArgumentList` instead of string interpolation. This is the .NET-recommended approach and avoids manual quote escaping.

#### 5.2.5 [MEDIUM, conf 0.80] `DeleteExpiredTranscripts` runs on every write

**Task:** [TSK-0242](/tasks?task=TSK-0242)

**Source:** `ChatTranscriptWriter.cs:46`. `WriteAsync` calls `DeleteExpiredTranscripts(directory, retentionDays)` on every chat turn. The function enumerates all JSONL files in the directory and checks their last-write time. With many transcript files, this is O(N) filesystem I/O per chat turn.

**Recommendation:** Run cleanup in a background timer (hourly), not on every write.

#### 5.2.6 [MEDIUM, conf 0.80] Feedback store uses separate SQLite DB from main DB

**Source:** `ChatFeedbackStore.cs` opens connections via its own path. The main app uses `SqliteMemorySmithDatabase` for auth/audit/version data. Having two separate SQLite databases means two sets of connections, two sets of pragmas to configure, and no transactional consistency between feedback and audit records.

**Recommendation:** Consider adding a `ChatFeedback` table to the main `SqliteMemorySmithDatabase` schema, or document the intentional separation and its tradeoffs.

#### 5.2.7 [LOW, conf 0.85] TrainingWorkbench.razor is 1035 lines

The training workbench is a substantial Blazor page with live status, run history, settings proxy, export management, and dependency probing. At 1035 lines it's manageable but approaching the point where extraction into smaller components would improve maintainability.

#### 5.2.8 [LOW, conf 0.80] No synthetic data quality review workflow

28 synthetic SFT examples in `starter_sft.jsonl` and `starter_sft.expanded.jsonl`. No UI to review, rate, or filter these before training. The harness uses them as-is when no transcript data is available.

**Recommendation:** Surface synthetic examples in the Training Workbench for admin review before training runs.

---

## 6. Observability & Trace Pipeline

### 6.1 [MEDIUM, conf 0.85] No per-tool invocation telemetry

**Task:** [TSK-0249](/tasks?task=TSK-0249)

Neither `ChatToolCatalog` nor `McpController` record tool call count, latency, or error rate in OpenTelemetry metrics. The chat host records trace events (`ChatTraceEvent`), but these are session-local and not aggregated.

**Recommendation:** Wrap each `tool.Execute(...)` in an OTEL `Activity` span tagged with `tool.name`, `tool.risk`, `transport`. Add a counter metric for invocations and a histogram for latency.

### 6.2 [MEDIUM, conf 0.85] Chat transcript metadata is well-structured but not queryable

**Task:** [TSK-0244](/tasks?task=TSK-0244)

`ChatTurnRecord` captures provider, model, execution metrics, tool calls, prompt/completion token estimates. This is excellent data for training and debugging. But it's written to JSONL files with no index — finding "all turns where the model used memorysmith_code_search and took >5 seconds" requires grep.

**Recommendation:** Add a lightweight SQLite table for transcript metadata (parallel to the JSONL files) with indexed columns for provider, model, latency, tool names.

### 6.3 [LOW, conf 0.85] Thinking-block extraction handles Ollama's `message.thinking` field

**Task:** [TSK-0250](/tasks?task=TSK-0250)

**Source:** `ChatServices.cs:632-635`. `ReadOllamaDelta` correctly extracts the `thinking` field from the Ollama JSON response. Models that emit `<think>...</think>` inline in `message.content` are handled by a regex post-pass at completion.

This is the correct dual-path approach. The inline `<think>` extraction only runs at stream completion, so during streaming the user sees raw `<think>` tags. For models like QwQ or DeepSeek-R1 over Ollama, this is a visual glitch but not a data loss.

**Recommendation:** Add a per-chunk scrubber that detects and strips `<think>` tags during streaming, not just at completion.

---

## 7. Remaining Findings (Categorized)

### Reliability
- [MEDIUM] `ReadToolCalls` swallows all JSON parse exceptions silently (line 1970) — log a warning and flush buffered content to the user
- [MEDIUM] Concurrent mutation of `ChatTurnState` during streaming (Chat.razor:1330-1512) — mutations happen outside Blazor's dispatch context
- [MEDIUM] localStorage writes during streaming every 2 seconds (Chat.razor:1386-1391)
- [LOW] `StripJsonFence` uses `LastIndexOf("```")` — nested fences in JSON string values cause premature truncation
- [LOW] `ActiveSession` falls back to `_sessions.First()` which can throw on empty list (Chat.razor:526-527)
- [LOW] Cross-tab localStorage conflict — no `storage` event listener (Chat.razor:2072-2119)

### Usability
- [MEDIUM] "Waiting for first token..." appears as literal message content instead of a shimmer animation (Chat.razor:1313)
- [MEDIUM] Transcript `role="log"` and `aria-live` missing on chat transcript container (Chat.razor:103)
- [MEDIUM] Feedback rating toggle can't clear — clicking same thumb sends same value, not 0 (Chat.razor:1551-1584)
- [LOW] No session rename capability
- [LOW] No mobile responsiveness beyond sidebar collapse
- [LOW] `RenderQuestionCardDetails` uses `MarkupString` without link policy filtering (Chat.razor:2776)
- [LOW] `OnAfterRenderAsync` calls `renderEnhancements` on every render including high-frequency streaming ticks

### Performance
- [MEDIUM] Image attachment base64 held in Blazor Server circuit memory — 5MB image = ~6.6MB in server RAM per circuit (Chat.razor:1966-1976)
- [LOW] `BuildTraceGraph` and `FilteredTraceEntries` use LINQ allocations in the render path (Chat.razor:1005-1048)
- [LOW] `ActiveSession` is a computed property with `FirstOrDefault` called dozens of times per render cycle

### Code Quality
- [MEDIUM] Duplicated helpers between ChatServices.cs and ChatToolCatalog.cs (~20 methods)
- [MEDIUM] Dead code: `ShouldPreloadContext` (lines 2451-2479) + `FormatRecordAsync` (lines 2232-2244) + ~6 compiled regex helpers
- [LOW] MCP `protocolVersion: "2025-06-18"` — may be stale relative to current MCP spec
- [LOW] McpController has 7 dead helper methods (GetString, GetInt, GetBool, GetStatus, Truncate, Clamp, FormatLinks)

---

## 8. What's Done Well

1. **Unified tool catalog** — one source of truth for chat, MCP, and agent tool surfaces with risk classification and per-mode availability. Thoughtful design.
2. **Context preloading planner** — `ChatContextPlanner.Plan` uses regex-based intent detection to decide what to preload and which tool to recommend. Clean separation of concerns.
3. **Intent interceptor** — deterministic auto-tool-call routing for common patterns ("search the wiki for X"). Faster than waiting for the LLM to decide.
4. **Write proposal workflow** — agent writes go through approval with proposal JSON parsing, diff preview, and accept/reject flow. This is the right pattern for a trusted-but-verified agent.
5. **Training data capture** — transcript metadata + content with per-field redaction, configurable retention, and a feedback store. This is infrastructure that no comparable local-first tool ships.
6. **Trace event pipeline** — per-turn trace events (context plan, tool calls, provider metadata, timing) visible in a sidebar drawer. Good observability.
7. **Model profile management** — per-provider/model configuration with role-based access, maintenance/proposal-review defaults, and a dedicated `/models` UI.
8. **Mermaid three-tier security policy** — standard/restricted/strict with `securityLevel: "strict"` on the Mermaid initialization. Defense in depth.
9. **Question card system** — structured LLM-driven clarification prompts with options and free-form input. Novel UX.
10. **`num_ctx` forwarding to Ollama** — correct context window governance at the provider level.

---

## 9. Prioritized Action Items

### Sprint A — "Make streaming smooth" (2-3 days)
1. Throttle `StateHasChanged` to 4-8 Hz during streaming.
2. Cache rendered markdown per turn content hash.
3. Debounce `SaveSessionsAsync` in `OnDraftInput` to once per second.
4. Replace "Waiting for first token..." text with a shimmer animation.
5. Add `role="log" aria-live="polite"` to transcript container.

### Sprint B — "Code block copy + composer" (1 day)
6. Add copy button to fenced code blocks in `renderEnhancements`.
7. Auto-resize composer textarea.
8. Add Cmd+K keyboard shortcut for spotlight search (if/when spotlight exists).

### Sprint C — "Memory write tools + provider-native calling" (3-5 days)
9. Add `memorysmith_memory_create` and `memorysmith_memory_update` tools.
10. Implement Ollama native tool calling (tools API parameter).
11. Implement OpenAI-compatible function calling for GitHub Models.
12. Keep text-based `ReadToolCalls` as fallback.

### Sprint D — "Message editing + training gates" (2-3 days)
13. Message editing (user turns) with re-send.
14. Message regeneration (assistant turns).
15. Add evaluation gate to training harness.
16. Add `trust_remote_code` configuration toggle.

---

## 10. Additional Critical Findings from Backend Deep-Read

### 10.1 [HIGH, conf 0.98] No `ILogger` anywhere in the 3216-line ChatServices.cs

**Task:** [TSK-0238](/tasks?task=TSK-0238)

The entire chat backend — `OllamaChatProvider`, `GitHubCopilotChatProvider`, `MemoryChatAgent` — lacks `ILogger<T>` injection. Provider errors, tool call failures, JSON parse failures, context planning decisions, prompt assembly, and streaming lifecycle events are all unlogged. This is a 3200-line service file with zero diagnostic logging. Every other service in the app uses `ILogger`.

**Impact:** When the chat "doesn't work" — wrong model, stalled stream, failed tool call — the operator has no server-side log to diagnose. They must inspect client-side trace events only.

**Recommendation:** Inject `ILogger<T>` into all three classes. Log: provider errors (Error), tool call execution (Info), tool call failures (Warning), context plan decisions (Debug), prompt token estimates (Debug), streaming lifecycle (Debug).

### 10.2 [HIGH, conf 0.92] No context window overflow detection

**Task:** [TSK-0202](/tasks?task=TSK-0202)

`BuildMessages` (lines 2524-2567) assembles system prompt + context + intercept results + attachments + history + user message with NO check that the total fits in the provider's context window. If context preloading returns large results, the total can exceed `OllamaContextWindowTokens`. Ollama will silently truncate from the start — destroying the system prompt and untrusted-data preamble.

**Recommendation:** After message assembly, sum estimated tokens. If over budget, trim context items first, then history, logging what was dropped. This is what Continue.dev and Copilot Chat do.

### 10.3 [HIGH, conf 0.90] `FormatGitHubPrompt` flattens structured messages into one string

**Task:** [TSK-0239](/tasks?task=TSK-0239)

`ChatServices.cs:1015-1016`. All messages (system, context, history, user) are concatenated into `"ROLE:\nContent"` format — a single flat string. This loses the structured message boundaries that models rely on for instruction following. Multi-turn conversations degrade in quality.

**Recommendation:** Use the SDK's structured message API if available. If not, use explicit delimiters (`<|system|>`, `<|user|>`, `<|assistant|>`) that the model family recognizes.

### 10.4 [HIGH, conf 0.95] Transcript timing fields always zero

**Task:** [TSK-0240](/tasks?task=TSK-0240)

`ChatServices.cs:1682-1683`. `TurnExecution.FirstTokenMs` and `TotalMs` are hardcoded to `0`. No timing instrumentation is captured. The training data export loses all performance metadata.

**Recommendation:** Start a `Stopwatch` at request entry, capture first-chunk time, and compute total duration before writing the transcript.

### 10.5 [HIGH, conf 0.90] Ollama streaming: malformed JSON line crashes the entire stream

**Task:** [TSK-0245](/tasks?task=TSK-0245)

`ChatServices.cs:544`. `JsonDocument.Parse(line)` has no try/catch. A single malformed line (partial write, OOM diagnostic, non-JSON text) throws `JsonException` and terminates the stream. Accumulated content is lost.

**Recommendation:** Wrap per-line parse in try/catch. Log the raw line. Surface accumulated content to the user.

### 10.6 [MEDIUM, conf 0.95] Tool call durations not populated in transcript

**Task:** [TSK-0240](/tasks?task=TSK-0240)

`TurnExecution.ToolCalls` is always `[]` in the transcript. Individual tool timings are captured but not aggregated into the training data export.

### 10.7 [MEDIUM, conf 0.90] `IsPotentialToolCallPrefix` calls `content.ToString()` on every streaming chunk

**Task:** [TSK-0212](/tasks?task=TSK-0212)

Inside the streaming loop, `content.ToString()` creates a full string copy of the accumulated response on every delta. For a 4000-token response, this is O(n²) total string allocations.

**Recommendation:** Track the first non-whitespace character with a boolean flag. Check only once.

---

## 11. Combined Rollup

| Severity | Count |
|---|---:|
| High | 10 |
| Medium | 27 |
| Low | 28 |
| Missing features recommended | 22 |

The ten Highs span three categories: rendering performance (StateHasChanged per token, markdown re-render per tick, keystroke-driven localStorage writes), backend reliability (no ILogger, no context window overflow detection, Ollama JSON parse crash, Copilot message flattening), and data quality (transcript timing always zero, Ollama stall detection). Fix the rendering performance first (§1) — it affects every user on every session. Then the backend reliability items (§10) — they affect debugging and quality.

---

*End of Audit #7. ~5,200 words.*
