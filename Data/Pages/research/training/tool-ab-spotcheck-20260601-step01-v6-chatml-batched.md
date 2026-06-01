# Tool A/B Spot Check - 20260601-step01-v6-chatml-batched.data (Base vs Tuned)

Generated: 2026-06-01 06:56:03Z

## Scope

- Manual spot-check battery: 5 prompts per tool across 12 Athena tools (60 total A/B pairs)
- Base model: `Qwen/Qwen3.5-4B`
- Tuned adapter: `D:\temp\memorysmith-training\runs\v6-chatml-batched-20260601-step1\adapter`
- Raw results JSON: `Data/Pages/research/training/tool-ab-spotcheck-20260601-step01-v6-chatml-batched.data.json`

## Headline Metrics

| Metric | Base | Tuned | Delta |
| --- | ---: | ---: | ---: |
| Envelope valid | 0/60 (0.0%) | 0/60 (0.0%) | +0 |
| Expected tool match | 0/60 (0.0%) | 0/60 (0.0%) | +0 |

## Per-Tool Results

| Tool | Cases | Base envelope | Base tool match | Tuned envelope | Tuned tool match | Delta envelope | Delta tool match |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `memorysmith_code_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | +0 | +0 |
| `memorysmith_code_search_status` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | +0 | +0 |
| `memorysmith_context_pack` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | +0 | +0 |
| `memorysmith_get` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | +0 | +0 |
| `memorysmith_hybrid_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | +0 | +0 |
| `memorysmith_page_get` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | +0 | +0 |
| `memorysmith_page_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | +0 | +0 |
| `memorysmith_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | +0 | +0 |
| `memorysmith_semantic_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | +0 | +0 |
| `memorysmith_task_get` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | +0 | +0 |
| `memorysmith_task_list` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | +0 | +0 |
| `memorysmith_unified_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | +0 | +0 |

## Notable Improvements

- None

## Notable Regressions

- None

## Persistent Failures (Both Models)

- `memorysmith_unified_search-1` (memorysmith_unified_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found
- `memorysmith_unified_search-2` (memorysmith_unified_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found
- `memorysmith_unified_search-3` (memorysmith_unified_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found
- `memorysmith_unified_search-4` (memorysmith_unified_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found
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
- Tuned: The user is asking me to search the wiki for information about "kv cache options". I need to use the memorysmith_search tool to search for this information. I should format the response as a single JSON object with toolCalls containing t...

### memorysmith_unified_search-2

- Prompt: find docs for training harness
- Base: assistant [think] Okay, the user is asking for documentation on the training harness. I need to figure out how to respond as Athena, the local wiki assistant. First, I should check if there's a specific tool I need to use for retrieving ...
- Tuned: The user is asking for documentation about a "training harness". This sounds like they want to search for or retrieve documentation related to a training harness feature. I should use the memorysmith_search tool to find relevant document...

### memorysmith_unified_search-3

- Prompt: lookup wiki notes about chat template
- Base: assistant [think] Okay, the user is asking me to look up wiki notes about chat templates. I need to check if there's any information in the MemorySmith's wiki about chat templates. Since I'm Athena, the local wiki assistant, I should use...
- Tuned: The user is asking me to look up wiki notes about "chat template". This is a search/retrieval action request, so I need to respond with exactly one JSON object containing a tool call for the memorysmith_search tool. I should format this ...

### memorysmith_unified_search-4

- Prompt: search for model profile defaults
- Base: assistant [think] Okay, the user is asking me to search for model profile defaults. I need to figure out what they mean by that. Model profiles could refer to different things depending on the context. Maybe they're talking about machine...
- Tuned: The user is asking me to search for "model profile defaults". This appears to be a request for memorysmith search functionality. I need to respond with exactly one JSON object containing a tool call for the memorysmith search tool. Let m...

### memorysmith_unified_search-5

- Prompt: find references about code search eta
- Base: assistant [think] Okay, the user is asking for references about code search ETA. First, I need to figure out what they mean by "code search eta". ETA usually stands for Estimated Time of Arrival, but in the context of code search, that d...
- Tuned: The user is asking me to find references about "code search eta". This seems to be a request for information about a feature or tool called "code search eta" - likely related to memorysmith or Athena's capabilities. I should respond with...

### memorysmith_hybrid_search-1

- Prompt: run a hybrid search for request guard middleware
- Base: [think] Okay, the user is asking me to run a hybrid search for "request guard middleware". I need to figure out how to handle this. First, I remember that as Athena, I'm supposed to respond with a JSON object when a search/retrieval acti...
- Tuned: The user is asking me to run a hybrid search for "request guard middleware". This is a search/retrieval action request, so I need to respond with exactly one JSON object containing a tool call for the search tool. I should format this as...

### memorysmith_hybrid_search-2

- Prompt: hybrid search for source bundle auth behavior
- Base: [think] Okay, the user is asking about hybrid search for source bundle auth behavior. Let me start by recalling what I know about hybrid search. Hybrid search usually combines different search methods, like keyword and vector search, to ...
- Tuned: The user is asking about hybrid search for source bundle auth behavior. This sounds like they want to search for information about authentication behavior related to source bundles in a hybrid search context. This is a memorysmith search...

### memorysmith_hybrid_search-3

- Prompt: use hybrid search to find onnx semantic search notes
- Base: Okay, the user is asking me to use hybrid search to find ONNX semantic search notes. First, I need to understand what they're looking for. Hybrid search typically combines different search methods, like keyword and semantic search, to re...
- Tuned: The user is asking me to use hybrid search to find onnx semantic search notes. This is a search/retrieval action request, so I need to respond with exactly one JSON object containing a tool call for the memorysmith_search tool. I should ...
