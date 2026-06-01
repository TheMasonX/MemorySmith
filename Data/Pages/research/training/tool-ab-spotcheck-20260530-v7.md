# Tool A/B Spot Check - Base vs Tuned (2026-05-30)

Generated: 2026-05-31 05:26:57Z

## Scope

- Manual spot-check battery: 5 prompts per tool across 12 Athena tools (60 total A/B pairs)
- Base model: `Qwen/Qwen3.5-4B`
- Tuned adapter: `D:\temp\memorysmith-training\runs\distilled-all-cat-20260530-v7-clean-rerun\adapter`
- Raw results JSON: `Data/Pages/research/training/tool-ab-spotcheck-20260530-v7.data.json`

## Headline Metrics

| Metric | Base | Tuned | Delta |
| --- | ---: | ---: | ---: |
| Envelope valid | 0/60 (0.0%) | 35/60 (58.3%) | +35 |
| Expected tool match | 0/60 (0.0%) | 0/60 (0.0%) | +0 |

## Per-Tool Results

| Tool | Cases | Base envelope | Base tool match | Tuned envelope | Tuned tool match | Delta envelope | Delta tool match |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `memorysmith_code_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 4/5 (80.0%) | 0/5 (0.0%) | +4 | +0 |
| `memorysmith_code_search_status` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 3/5 (60.0%) | 0/5 (0.0%) | +3 | +0 |
| `memorysmith_context_pack` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 2/5 (40.0%) | 0/5 (0.0%) | +2 | +0 |
| `memorysmith_get` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 2/5 (40.0%) | 0/5 (0.0%) | +2 | +0 |
| `memorysmith_hybrid_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | +0 | +0 |
| `memorysmith_page_get` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 5/5 (100.0%) | 0/5 (0.0%) | +5 | +0 |
| `memorysmith_page_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 5/5 (100.0%) | 0/5 (0.0%) | +5 | +0 |
| `memorysmith_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 3/5 (60.0%) | 0/5 (0.0%) | +3 | +0 |
| `memorysmith_semantic_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | +0 | +0 |
| `memorysmith_task_get` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 4/5 (80.0%) | 0/5 (0.0%) | +4 | +0 |
| `memorysmith_task_list` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 3/5 (60.0%) | 0/5 (0.0%) | +3 | +0 |
| `memorysmith_unified_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 4/5 (80.0%) | 0/5 (0.0%) | +4 | +0 |

## Notable Improvements

- `memorysmith_unified_search-1` (memorysmith_unified_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memory_search); prompt="search the wiki for kv cache options"
- `memorysmith_unified_search-2` (memorysmith_unified_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memorysmith_training_harness); prompt="find docs for training harness"
- `memorysmith_unified_search-3` (memorysmith_unified_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memory_search); prompt="lookup wiki notes about chat template"
- `memorysmith_unified_search-4` (memorysmith_unified_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memory_search); prompt="search for model profile defaults"
- `memorysmith_search-1` (memorysmith_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memory_search); prompt="search memories for exact term TRAIN-001"
- `memorysmith_search-3` (memorysmith_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memory_search); prompt="find literal text RequestGuard"
- `memorysmith_search-4` (memorysmith_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memory_search); prompt="search by tag governance"
- `memorysmith_context_pack-4` (memorysmith_context_pack): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memory_retrieval); prompt="create context pack from ids mem_a,mem_b"
- `memorysmith_context_pack-5` (memorysmith_context_pack): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=search); prompt="context pack for query chatml template"
- `memorysmith_get-3` (memorysmith_get): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memory_get); prompt="get memory mem_ops_009"
- `memorysmith_get-5` (memorysmith_get): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memory_load); prompt="load memory mem_task_001"
- `memorysmith_page_search-1` (memorysmith_page_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memory_search); prompt="find pages about markdown rendering"

## Notable Regressions

- None

## Persistent Failures (Both Models)

- `memorysmith_unified_search-1` (memorysmith_unified_search): base pred=None, tuned pred=memory_search, baseErr=No JSON object found, tunedErr=None
- `memorysmith_unified_search-2` (memorysmith_unified_search): base pred=None, tuned pred=memorysmith_training_harness, baseErr=No JSON object found, tunedErr=None
- `memorysmith_unified_search-3` (memorysmith_unified_search): base pred=None, tuned pred=memory_search, baseErr=No JSON object found, tunedErr=None
- `memorysmith_unified_search-4` (memorysmith_unified_search): base pred=None, tuned pred=memory_search, baseErr=No JSON object found, tunedErr=None
- `memorysmith_unified_search-5` (memorysmith_unified_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found
- `memorysmith_hybrid_search-1` (memorysmith_hybrid_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found
- `memorysmith_hybrid_search-2` (memorysmith_hybrid_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found
- `memorysmith_hybrid_search-3` (memorysmith_hybrid_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found
- `memorysmith_hybrid_search-4` (memorysmith_hybrid_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found
- `memorysmith_hybrid_search-5` (memorysmith_hybrid_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found
- `memorysmith_semantic_search-1` (memorysmith_semantic_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found
- `memorysmith_semantic_search-2` (memorysmith_semantic_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found
- `memorysmith_semantic_search-3` (memorysmith_semantic_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found
- `memorysmith_semantic_search-4` (memorysmith_semantic_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found
- `memorysmith_semantic_search-5` (memorysmith_semantic_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found

## Representative Output Snippets

### memorysmith_unified_search-1

- Prompt: search the wiki for kv cache options
- Base: [think] Okay, the user is asking me to search the wiki for information about kv cache options. I need to remember that I'm Athena, MemorySmith's local wiki assistant. When a search or retrieval action is requested, I should respond with ...
- Tuned: The user wants to search the wiki for information about kv cache options. I should use the memory search tool to find relevant documentation about kv cache configuration options. [/think] {"toolCalls":[{"name":"memory_search","arguments"...

### memorysmith_unified_search-2

- Prompt: find docs for training harness
- Base: assistant [think] Okay, the user is asking for documentation on the training harness. I need to figure out how to respond as Athena, the local wiki assistant. First, I should check if there's a specific tool I need to use for retrieving ...
- Tuned: The user is asking for documentation about the training harness. This is a specific feature in the MemorySmith system, so I should search for relevant documentation pages. [/think] {"toolCalls":[{"name":"memorysmith_training_harness","ar...

### memorysmith_unified_search-3

- Prompt: lookup wiki notes about chat template
- Base: assistant [think] Okay, the user is asking me to look up wiki notes about chat templates. I need to check if there's any information in the MemorySmith's wiki about chat templates. Since I'm Athena, the local wiki assistant, I should use...
- Tuned: The user is asking for wiki notes about chat templates. I should use the memory search tool to find relevant documentation about chat templates. [/think] {"toolCalls":[{"name":"memory_search","arguments":{"query":"chat template","maxResu...

### memorysmith_unified_search-4

- Prompt: search for model profile defaults
- Base: assistant [think] Okay, the user is asking me to search for model profile defaults. I need to figure out what they mean by that. Model profiles could refer to different things depending on the context. Maybe they're talking about machine...
- Tuned: The user is asking about model profile defaults. This is a specific configuration topic that should be in the wiki. I need to search for this information using the memory search tool. [/think] {"toolCalls":[{"name":"memory_search","argum...

### memorysmith_unified_search-5

- Prompt: find references about code search eta
- Base: assistant [think] Okay, the user is asking for references about code search ETA. First, I need to figure out what they mean by "code search eta". ETA usually stands for Estimated Time of Arrival, but in the context of code search, that d...
- Tuned: The user is asking about code search ETA (Estimated Time of Arrival). This is a specific feature in the MemorySmith code search system. I should search for documentation about code search and ETA estimates. [/think] {"toolCalls":[{"name"...

### memorysmith_hybrid_search-1

- Prompt: run a hybrid search for request guard middleware
- Base: [think] Okay, the user is asking me to run a hybrid search for "request guard middleware". I need to figure out how to handle this. First, I remember that as Athena, I'm supposed to respond with a JSON object when a search/retrieval acti...
- Tuned: The user wants to run a hybrid search for "request guard middleware". This is a specific search request that should use both semantic search and keyword search. I need to construct a hybrid search query that combines the topic with speci...

### memorysmith_hybrid_search-2

- Prompt: hybrid search for source bundle auth behavior
- Base: [think] Okay, the user is asking about hybrid search for source bundle auth behavior. Let me start by recalling what I know about hybrid search. Hybrid search usually combines different search methods, like keyword and vector search, to ...
- Tuned: The user is asking about hybrid search and source bundle authentication behavior. This is a specific technical question about the memory system's search functionality. I should use the memory search tool to find relevant information abou...

### memorysmith_hybrid_search-3

- Prompt: use hybrid search to find onnx semantic search notes
- Base: Okay, the user is asking me to use hybrid search to find ONNX semantic search notes. First, I need to understand what they're looking for. Hybrid search typically combines different search methods, like keyword and semantic search, to re...
- Tuned: The user is asking for semantic search notes about onnx. They want me to use hybrid search, which combines semantic search with keyword search. I should use the semantic search tool with appropriate parameters for onnx. [/think] {"toolCa...
