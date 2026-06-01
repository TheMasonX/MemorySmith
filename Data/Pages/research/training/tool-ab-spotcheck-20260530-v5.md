# Tool A/B Spot Check - Base vs Tuned (2026-05-30)

Generated: 2026-05-30 23:49:18Z

## Scope

- Manual spot-check battery: 5 prompts per tool across 12 Athena tools (60 total A/B pairs)
- Base model: `Qwen/Qwen3.5-4B`
- Tuned adapter: `D:\temp\memorysmith-training\runs\distilled-all-cat-20260530-v6-chatml\adapter`
- Raw results JSON: `Data/Pages/research/training/tool-ab-spotcheck-20260530-v5.data.json`

## Headline Metrics

| Metric | Base | Tuned | Delta |
| --- | ---: | ---: | ---: |
| Envelope valid | 0/60 (0.0%) | 60/60 (100.0%) | +60 |
| Expected tool match | 0/60 (0.0%) | 42/60 (70.0%) | +42 |

## Per-Tool Results

| Tool | Cases | Base envelope | Base tool match | Tuned envelope | Tuned tool match | Delta envelope | Delta tool match |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `memorysmith_code_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 5/5 (100.0%) | 4/5 (80.0%) | +5 | +4 |
| `memorysmith_code_search_status` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 5/5 (100.0%) | 2/5 (40.0%) | +5 | +2 |
| `memorysmith_context_pack` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 5/5 (100.0%) | 5/5 (100.0%) | +5 | +5 |
| `memorysmith_get` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 5/5 (100.0%) | 3/5 (60.0%) | +5 | +3 |
| `memorysmith_hybrid_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 5/5 (100.0%) | 5/5 (100.0%) | +5 | +5 |
| `memorysmith_page_get` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 5/5 (100.0%) | 2/5 (40.0%) | +5 | +2 |
| `memorysmith_page_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 5/5 (100.0%) | 4/5 (80.0%) | +5 | +4 |
| `memorysmith_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 5/5 (100.0%) | 4/5 (80.0%) | +5 | +4 |
| `memorysmith_semantic_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 5/5 (100.0%) | 5/5 (100.0%) | +5 | +5 |
| `memorysmith_task_get` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 5/5 (100.0%) | 3/5 (60.0%) | +5 | +3 |
| `memorysmith_task_list` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 5/5 (100.0%) | 5/5 (100.0%) | +5 | +5 |
| `memorysmith_unified_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 5/5 (100.0%) | 0/5 (0.0%) | +5 | +0 |

## Notable Improvements

- `memorysmith_unified_search-1` (memorysmith_unified_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memorysmith_search); prompt="search the wiki for kv cache options"
- `memorysmith_unified_search-2` (memorysmith_unified_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memorysmith_code_search); prompt="find docs for training harness"
- `memorysmith_unified_search-3` (memorysmith_unified_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memorysmith_code_search); prompt="lookup wiki notes about chat template"
- `memorysmith_unified_search-4` (memorysmith_unified_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memorysmith_code_search); prompt="search for model profile defaults"
- `memorysmith_unified_search-5` (memorysmith_unified_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memorysmith_code_search); prompt="find references about code search eta"
- `memorysmith_hybrid_search-1` (memorysmith_hybrid_search): base(match=False, env=False, pred=None) -> tuned(match=True, env=True, pred=memorysmith_hybrid_search); prompt="run a hybrid search for request guard middleware"
- `memorysmith_hybrid_search-2` (memorysmith_hybrid_search): base(match=False, env=False, pred=None) -> tuned(match=True, env=True, pred=memorysmith_hybrid_search); prompt="hybrid search for source bundle auth behavior"
- `memorysmith_hybrid_search-3` (memorysmith_hybrid_search): base(match=False, env=False, pred=None) -> tuned(match=True, env=True, pred=memorysmith_hybrid_search); prompt="use hybrid search to find onnx semantic search notes"
- `memorysmith_hybrid_search-4` (memorysmith_hybrid_search): base(match=False, env=False, pred=None) -> tuned(match=True, env=True, pred=memorysmith_hybrid_search); prompt="hybrid search for maintenance proposal workflow"
- `memorysmith_hybrid_search-5` (memorysmith_hybrid_search): base(match=False, env=False, pred=None) -> tuned(match=True, env=True, pred=memorysmith_hybrid_search); prompt="run hybrid search for task assignee update invariant"
- `memorysmith_semantic_search-1` (memorysmith_semantic_search): base(match=False, env=False, pred=None) -> tuned(match=True, env=True, pred=memorysmith_semantic_search); prompt="semantic search for retrieval warning propagation"
- `memorysmith_semantic_search-2` (memorysmith_semantic_search): base(match=False, env=False, pred=None) -> tuned(match=True, env=True, pred=memorysmith_semantic_search); prompt="semantic search for pages lock navigation regression"

## Notable Regressions

- None

## Persistent Failures (Both Models)

- `memorysmith_unified_search-1` (memorysmith_unified_search): base pred=None, tuned pred=memorysmith_search, baseErr=No JSON object found, tunedErr=None
- `memorysmith_unified_search-2` (memorysmith_unified_search): base pred=None, tuned pred=memorysmith_code_search, baseErr=No JSON object found, tunedErr=None
- `memorysmith_unified_search-3` (memorysmith_unified_search): base pred=None, tuned pred=memorysmith_code_search, baseErr=No JSON object found, tunedErr=None
- `memorysmith_unified_search-4` (memorysmith_unified_search): base pred=None, tuned pred=memorysmith_code_search, baseErr=No JSON object found, tunedErr=None
- `memorysmith_unified_search-5` (memorysmith_unified_search): base pred=None, tuned pred=memorysmith_code_search, baseErr=No JSON object found, tunedErr=None
- `memorysmith_search-3` (memorysmith_search): base pred=None, tuned pred=memorysmith_text_search, baseErr=No JSON object found, tunedErr=None
- `memorysmith_get-2` (memorysmith_get): base pred=None, tuned pred=memorysmith_open, baseErr=No JSON object found, tunedErr=None
- `memorysmith_get-5` (memorysmith_get): base pred=None, tuned pred=memorysmith_load, baseErr=No JSON object found, tunedErr=None
- `memorysmith_page_search-3` (memorysmith_page_search): base pred=None, tuned pred=memorysmith_page_notes, baseErr=No JSON object found, tunedErr=None
- `memorysmith_page_get-1` (memorysmith_page_get): base pred=None, tuned pred=memorysmith_page_open, baseErr=No JSON object found, tunedErr=None
- `memorysmith_page_get-3` (memorysmith_page_get): base pred=None, tuned pred=memorysmith_page, baseErr=No JSON object found, tunedErr=None
- `memorysmith_page_get-5` (memorysmith_page_get): base pred=None, tuned pred=memorysmith_page_open, baseErr=No JSON object found, tunedErr=None
- `memorysmith_task_get-2` (memorysmith_task_get): base pred=None, tuned pred=memorysmith_task_open, baseErr=No JSON object found, tunedErr=None
- `memorysmith_task_get-5` (memorysmith_task_get): base pred=None, tuned pred=memorysmith_task_load, baseErr=No JSON object found, tunedErr=None
- `memorysmith_code_search-4` (memorysmith_code_search): base pred=None, tuned pred=memorysmith_code, baseErr=No JSON object found, tunedErr=None

## Representative Output Snippets

### memorysmith_unified_search-1

- Prompt: search the wiki for kv cache options
- Base: [think] Okay, the user is asking me to search the wiki for information about kv cache options. I need to remember that I'm Athena, MemorySmith's local wiki assistant. When a search or retrieval action is requested, I should respond with ...
- Tuned: [think] {"toolCalls":[{"name":"memorysmith_search","arguments":{"query":"kv cache options"}}]} assistant [think] {"toolCalls":[{"name":"memorysmith_search","arguments":{"query":"kv cache options"}}]} assistant [think] {"toolCalls":[{"nam...

### memorysmith_unified_search-2

- Prompt: find docs for training harness
- Base: assistant [think] Okay, the user is asking for documentation on the training harness. I need to figure out how to respond as Athena, the local wiki assistant. First, I should check if there's a specific tool I need to use for retrieving ...
- Tuned: assistant [think] {"toolCalls":[{"name":"memorysmith_code_search","arguments":{"query":"training harness","limit":5}}]}

### memorysmith_unified_search-3

- Prompt: lookup wiki notes about chat template
- Base: assistant [think] Okay, the user is asking me to look up wiki notes about chat templates. I need to check if there's any information in the MemorySmith's wiki about chat templates. Since I'm Athena, the local wiki assistant, I should use...
- Tuned: assistant [think] {"toolCalls":[{"name":"memorysmith_code_search","arguments":{"query":"chat template","limit":5}}]} assistant [think] {"toolCalls":[{"name":"memorysmith_code_search","arguments":{"query":"chat template","limit":5}}]} ass...

### memorysmith_unified_search-4

- Prompt: search for model profile defaults
- Base: assistant [think] Okay, the user is asking me to search for model profile defaults. I need to figure out what they mean by that. Model profiles could refer to different things depending on the context. Maybe they're talking about machine...
- Tuned: assistant [think] {"toolCalls":[{"name":"memorysmith_code_search","arguments":{"query":"model profile defaults","limit":10}}]}

### memorysmith_unified_search-5

- Prompt: find references about code search eta
- Base: assistant [think] Okay, the user is asking for references about code search ETA. First, I need to figure out what they mean by "code search eta". ETA usually stands for Estimated Time of Arrival, but in the context of code search, that d...
- Tuned: assistant [think] {"toolCalls":[{"name":"memorysmith_code_search","arguments":{"query":"eta","limit":10}}]}

### memorysmith_hybrid_search-1

- Prompt: run a hybrid search for request guard middleware
- Base: [think] Okay, the user is asking me to run a hybrid search for "request guard middleware". I need to figure out how to handle this. First, I remember that as Athena, I'm supposed to respond with a JSON object when a search/retrieval acti...
- Tuned: [think] {"toolCalls":[{"name":"memorysmith_hybrid_search","arguments":{"query":"request guard middleware","memoryLimit":6}}]} assistant [think] {"toolCalls":[{"name":"memorysmith_hybrid_search","arguments":{"query":"request guard middlew...

### memorysmith_hybrid_search-2

- Prompt: hybrid search for source bundle auth behavior
- Base: [think] Okay, the user is asking about hybrid search for source bundle auth behavior. Let me start by recalling what I know about hybrid search. Hybrid search usually combines different search methods, like keyword and vector search, to ...
- Tuned: [think] {"toolCalls":[{"name":"memorysmith_hybrid_search","arguments":{"query":"source bundle auth behavior","limit":6}}]} assistant [think] {"toolCalls":[{"name":"memorysmith_hybrid_search","arguments":{"query":"source bundle auth behav...

### memorysmith_hybrid_search-3

- Prompt: use hybrid search to find onnx semantic search notes
- Base: Okay, the user is asking me to use hybrid search to find ONNX semantic search notes. First, I need to understand what they're looking for. Hybrid search typically combines different search methods, like keyword and semantic search, to re...
- Tuned: {"toolCalls":[{"name":"memorysmith_hybrid_search","arguments":{"query":"onnx","memoryLimit":10}}]} assistant [think] {"toolCalls":[{"name":"memorysmith_hybrid_search","arguments":{"query":"onnx","memoryLimit":10}}]} assistant
