# Training Data Distillation Request

**Type:** Agent Task Request  
**Date:** 2026-05-29  
**Status:** Open  
**Audience:** AI agent (Athena or equivalent) with access to MemorySmith MCP tools

## Supplemental Delivery Note (2026-05-30)

Hyperagent delivered **55 validated JSONL examples** across all 9 target categories with 29 unique real memory/page IDs from the live knowledge base. Tool call envelope verification passed for the required shape:

`{"toolCalls":[{"name":"...","arguments":{...}}]}`

Category coverage summary:

| Category | Count | Notes |
|---|---:|---|
| Single-tool retrieval | 15 | unified(3), hybrid(2), semantic(2), lexical(2), page_search(2), code_search(4) |
| Context pack + multi-reference | 8 | context_pack with backlinks, grounded multi-source answers |
| Direct get by ID/slug | 5 | memorysmith_get(3), page_get(2) |
| Task browsing | 5 | task_list(4), task_get(1) with filters |
| Code search | 5 | realistic codebase questions |
| Multi-turn | 6 | 4+ messages each |
| Agent-mode writes | 4 | strict JSON with reply/memoryWrites/pageWrites |
| Graceful failure | 4 | acknowledges gaps, suggests resources |
| Citation-focused | 3 | explicit `- Source: memory:` footers |

Validation claim from Hyperagent: **55/55 valid JSON**, **0 confabulated IDs**, correct tool-call envelope on all 53 tool-call examples, and all `memory:` / `page:` references came from verified inventory.

Important training-quality note: Hyperagent reports it initially produced an incorrect tool envelope (`{"tool":"..."}`) before correction, matching audit concern TRAIN-001. The delivered dataset now uses the exact envelope expected by the C# `ReadToolCalls` parser.

---

## Context — Why This Exists

MemorySmith fine-tunes a local language model — currently **Qwen/Qwen3.5-4B** — using parameter-efficient LoRA adapters (rank 8, alpha 16, target modules: `q_proj k_proj v_proj o_proj`). The goal is to teach the model to behave like **Athena**: a grounded wiki assistant that queries the MemorySmith knowledge base before answering, cites sources, requests tool calls in a precise JSON protocol, and distinguishes evidence from inference.

Training is driven by `MemorySmith.Training/harness.py` via the Training Workbench at `/training-workbench`. The harness reads `.sft.jsonl` export files from `Data/Training/exports/`, converts them to causal language model training text using `to_training_text()`, and runs the PEFT LoRA training loop directly on the GPU (RTX 5060, CUDA).

Currently there are **~30 synthetic export files** in `Data/Training/exports/`, most of which cover narrow tool-call scenarios produced during sprint development. The dataset is thin on:

- Multi-turn conversations with real retrieval depth
- Accurate source citation patterns (`memory:<id>`, `page:<slug>`)
- Agent-mode write examples (`memoryWrites`, `pageWrites`, task mutation tools)
- Graceful refusal/clarification when context is insufficient
- Code search integration (questions about the codebase resolved via `memorysmith_code_search`)

This request asks an agent to **distill new, high-quality SFT examples** by querying the live MemorySmith knowledge base and producing realistic Athena-style conversations that would help the model generalise those behaviours.

---

## Training Data Format

Each output file must be a **line-delimited JSON** file with extension `.sft.jsonl`, placed under `Data/Training/exports/`.

Each line is one independent training example in the **FilteredSft / ChatML** format:

```jsonc
{"messages": [
  {"role": "system", "content": "<system prompt>"},
  {"role": "user",  "content": "<user turn>"},
  {"role": "assistant", "content": "<ideal response>"},
  // optional additional turns for multi-turn examples
]}
```

### System Prompt Variants

Use realistic system prompt variants. The canonical form is:

```
You are Athena, MemorySmith's local wiki assistant. Use the supplied memories, pages, and attachments as local context. Distinguish clearly between evidence from the knowledge base and your own inference.
```

For citation-emphasis examples add:

```
You are Athena. Cite sources as memory:<id> or page:<slug>.
```

For agent-mode write examples add:

```
You are Athena in Agent mode with auto_accept approval. Mutation tools are available.
```

### Tool-Call Response Shape

When the ideal response is a tool call, the `content` must be exactly this JSON — **no prose, no Markdown fence**:

```json
{"toolCalls":[{"name":"<tool>","arguments":{<args>}}]}
```

For `memoryWrites` (agent mode):

```json
{"memoryWrites":[{"id":"<id>","title":"<t>","content":"<c>","tags":["..."]}]}
```

For `pageWrites` (agent mode):

```json
{"pageWrites":[{"slug":"<slug>","title":"<t>","body":"<markdown>"}]}
```

---

## Available Tools

The following tools are accessible through the intercepted MCP-compatible protocol at runtime and can be called as `toolCalls` in assistant turns. Use them to discover real content for example answers.

### Search & Retrieval

| Tool | Purpose | Key Arguments |
|------|---------|---------------|
| `memorysmith_unified_search` | Broad search — memories + pages + tasks | `query`, `memoryLimit`, `pageLimit` |
| `memorysmith_hybrid_search` | Balanced conceptual/lexical memory search | `query`, `limit` |
| `memorysmith_semantic_search` | Dense vector search for conceptual recall | `query`, `limit` |
| `memorysmith_search` | Exact-term, tag, ID, or literal-word search | `query`, `limit` |
| `memorysmith_context_pack` | Root record + references + backlinks | `id` or `query`, `includeBacklinks`, `referenceDepth` |
| `memorysmith_get` | Fetch a single memory by ID | `id` |

### Pages

| Tool | Purpose | Key Arguments |
|------|---------|---------------|
| `memorysmith_page_search` | Find wiki pages by natural-language query | `query` |
| `memorysmith_page_get` | Fetch a page by its slug | `slug` |

### Tasks

| Tool | Purpose | Key Arguments |
|------|---------|---------------|
| `memorysmith_task_list` | List/filter task records | `status`, `tag`, `assignee`, `query` |
| `memorysmith_task_get` | Fetch a task by ID or key | `id` |

### Agent-Mode Write Tools *(only valid in Agent-mode examples)*

| Tool | Purpose |
|------|---------|
| `memorysmith_task_create` | Create a new task record |
| `memorysmith_task_update` | Update task fields |
| `memorysmith_task_set_status` | Transition task status |
| `memorysmith_task_add_comment` | Append a comment |
| `memorysmith_task_add_attachment` | Attach a file reference |

### Code Search *(read-only)*

| Tool | Purpose | Key Arguments |
|------|---------|---------------|
| `memorysmith_code_search` | Full-text search over indexed source | `query`, `limit` |
| `memorysmith_code_search_status` | Current index build status | *(none)* |

---

## What to Generate

Produce **at least 50 high-quality examples** across the following categories. Aim for variety in phrasing, turn count, and content. Ground answers in **real IDs, slugs, and facts** pulled from the live knowledge base using the tools above.

### Category Breakdown

| Category | Count | Notes |
|----------|-------|-------|
| Single-tool retrieval (search → grounded answer) | 15 | Covers `unified_search`, `hybrid_search`, `semantic_search`, `search` |
| Context pack + multi-reference answer | 8 | Use `context_pack` with `includeBacklinks: true` |
| Direct get by ID or slug | 5 | Covers `memorysmith_get`, `memorysmith_page_get` |
| Task browsing | 5 | `task_list` with filters + `task_get` |
| Code search | 5 | Realistic codebase questions answered with source snippets |
| Multi-turn: search then follow-up | 6 | Second user turn asks for more detail, related records, or a correction |
| Agent-mode write | 4 | Create or update task, write a new memory draft |
| Graceful failure / clarification | 4 | Model has insufficient context, asks for clarification or says "I don't know" |
| Citation-focused | 3 | Include explicit `- Source: memory:<id>` footers |

### Quality Criteria

Each example must:

- Have a plausible user question grounded in MemorySmith's actual domain (wiki, tasks, code, training, chat, admin, ONNX, semantic search, etc.)
- Have an assistant response that either (a) requests exactly one focused tool call, or (b) provides a grounded prose answer with at least one cited ID/slug, or (c) is a clean graceful refusal when evidence is absent
- **Not** confabulate memory IDs or page slugs — only use IDs retrieved through the tools during distillation
- Follow the tool-call JSON contract exactly (no prose wrapping)
- Keep system prompts realistic; do not add tool call instructions into user turns

### Example Skeleton

```jsonc
// Tool-call example
{"messages":[
  {"role":"system","content":"You are Athena, MemorySmith's local wiki assistant."},
  {"role":"user","content":"What does MemorySmith use for semantic search?"},
  {"role":"assistant","content":"{\"toolCalls\":[{\"name\":\"memorysmith_unified_search\",\"arguments\":{\"query\":\"semantic search ONNX embedding\",\"memoryLimit\":5,\"pageLimit\":3}}]}"}
]}

// Grounded prose example (after tool results would have been returned)
{"messages":[
  {"role":"system","content":"You are Athena. Cite sources as memory:<id> or page:<slug>."},
  {"role":"user","content":"How does the ONNX semantic search pipeline work?"},
  {"role":"assistant","content":"MemorySmith's semantic search uses a MiniLM-L6-v2 ONNX model to produce 384-dimensional embeddings stored per-memory. At query time the same model embeds the query and cosine similarity ranks results.\n\n- Source: memory:mem_onnx_001 - ONNX Semantic Search Pipeline"}
]}
```

---

## Required Output

1. **One or more `.sft.jsonl` files** in `Data/Training/exports/`, named with the prefix `distilled-` and a date suffix, e.g.:
   - `distilled-retrieval-20260529.sft.jsonl`
   - `distilled-agent-writes-20260529.sft.jsonl`
   - `distilled-code-search-20260529.sft.jsonl`

2. A **summary comment** (can be a Task comment on this task, or a page update here) describing:
   - How many examples were generated per category
   - Which memory IDs and page slugs were verified live
   - Any gaps discovered (topics with no coverage in the wiki)
   - Confidence level on factual accuracy of grounded answers

---

## Acceptance Criteria

- [ ] ≥ 50 valid JSONL lines across all output files
- [ ] Each line parses as valid JSON with a `messages` array
- [ ] No confabulated IDs — all `memory:<id>` and `page:<slug>` references were retrieved via tools
- [ ] Tool-call lines contain only the JSON object (no surrounding text)
- [ ] At least 4 multi-turn examples (3+ turns each)
- [ ] At least 4 agent-mode examples
- [ ] Files placed in `Data/Training/exports/`
- [ ] Summary comment or update provided

---

## Notes for the Agent

- Start with `memorysmith_unified_search` or `memorysmith_context_pack` to orient yourself to the current state of the knowledge base before generating any examples.
- The live data is in `Data/Memories/Core/` and `Data/Pages/`. Use it as your ground truth.
- Do **not** generate examples about capabilities that don't exist (e.g., external web browsing, image generation, SQL queries) — Athena is a local wiki assistant only.
- Prefer realistic terse user questions over verbose or lecture-style prompts.
- For the "graceful failure" category, the assistant should acknowledge the gap and suggest what the user could provide or check, not confabulate an answer.
