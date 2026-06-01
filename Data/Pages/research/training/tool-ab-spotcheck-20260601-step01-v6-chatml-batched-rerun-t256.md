# Tool A/B Spot Check - 20260601-step01-v6-chatml-batched-rerun-t256 (Base vs Tuned)

Generated: 2026-06-01 16:26:26Z

## Scope

- Manual spot-check battery: 5 prompts per tool across 12 Athena tools (60 total A/B pairs)
- Base model: `Qwen/Qwen3.5-4B`
- Tuned adapter: `D:\temp\memorysmith-training\runs\20260601-085001\adapter`
- Raw results JSON: `Data/Pages/research/training/tool-ab-spotcheck-20260601-step01-v6-chatml-batched-rerun-t256.data.json`

## Headline Metrics

| Metric | Base | Tuned | Delta |
| --- | ---: | ---: | ---: |
| Envelope valid | 0/60 (0.0%) | 17/60 (28.3%) | +17 |
| Expected tool match | 0/60 (0.0%) | 0/60 (0.0%) | +0 |

## Per-Tool Results

| Tool | Cases | Base envelope | Base tool match | Tuned envelope | Tuned tool match | Delta envelope | Delta tool match |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `memorysmith_code_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 3/5 (60.0%) | 0/5 (0.0%) | +3 | +0 |
| `memorysmith_code_search_status` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | +0 | +0 |
| `memorysmith_context_pack` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | +0 | +0 |
| `memorysmith_get` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 3/5 (60.0%) | 0/5 (0.0%) | +3 | +0 |
| `memorysmith_hybrid_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | +0 | +0 |
| `memorysmith_page_get` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 3/5 (60.0%) | 0/5 (0.0%) | +3 | +0 |
| `memorysmith_page_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 1/5 (20.0%) | 0/5 (0.0%) | +1 | +0 |
| `memorysmith_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 1/5 (20.0%) | 0/5 (0.0%) | +1 | +0 |
| `memorysmith_semantic_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | +0 | +0 |
| `memorysmith_task_get` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 1/5 (20.0%) | 0/5 (0.0%) | +1 | +0 |
| `memorysmith_task_list` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 2/5 (40.0%) | 0/5 (0.0%) | +2 | +0 |
| `memorysmith_unified_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 3/5 (60.0%) | 0/5 (0.0%) | +3 | +0 |

## Notable Improvements

- `memorysmith_unified_search-1` (memorysmith_unified_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=search_wiki); prompt="search the wiki for kv cache options"
- `memorysmith_unified_search-3` (memorysmith_unified_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=search); prompt="lookup wiki notes about chat template"
- `memorysmith_unified_search-4` (memorysmith_unified_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=search); prompt="search for model profile defaults"
- `memorysmith_search-5` (memorysmith_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=search); prompt="search for exact key tsk-0228"
- `memorysmith_get-2` (memorysmith_get): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=open_memory); prompt="open memory mem_training_001"
- `memorysmith_get-3` (memorysmith_get): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=get_memory); prompt="get memory mem_ops_009"
- `memorysmith_get-4` (memorysmith_get): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=fetch_memory); prompt="fetch memory mem_onnx_001"
- `memorysmith_page_search-4` (memorysmith_page_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=search); prompt="search pages for request guard middleware"
- `memorysmith_page_get-1` (memorysmith_page_get): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=open_page); prompt="open page memory-taxonomy"
- `memorysmith_page_get-2` (memorysmith_page_get): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=get_page); prompt="get page codebase-vector-search-whitepaper"
- `memorysmith_page_get-4` (memorysmith_page_get): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=search); prompt="fetch page semantic-search"
- `memorysmith_task_list-2` (memorysmith_task_list): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=getTasks); prompt="list all tasks in progress"

## Notable Regressions

- None

## Persistent Failures (Both Models)

- `memorysmith_unified_search-1` (memorysmith_unified_search): base pred=None, tuned pred=search_wiki, baseErr=No JSON object found, tunedErr=None
- `memorysmith_unified_search-2` (memorysmith_unified_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=JSON parse error: Expecting property name enclosed in double quotes: line 1 column 42 (char 41)
- `memorysmith_unified_search-3` (memorysmith_unified_search): base pred=None, tuned pred=search, baseErr=No JSON object found, tunedErr=None
- `memorysmith_unified_search-4` (memorysmith_unified_search): base pred=None, tuned pred=search, baseErr=No JSON object found, tunedErr=None
- `memorysmith_unified_search-5` (memorysmith_unified_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=JSON parse error: Expecting property name enclosed in double quotes: line 1 column 42 (char 41)
- `memorysmith_hybrid_search-1` (memorysmith_hybrid_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=JSON parse error: Expecting property name enclosed in double quotes: line 1 column 42 (char 41)
- `memorysmith_hybrid_search-2` (memorysmith_hybrid_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=JSON parse error: Expecting property name enclosed in double quotes: line 1 column 42 (char 41)
- `memorysmith_hybrid_search-3` (memorysmith_hybrid_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found
- `memorysmith_hybrid_search-4` (memorysmith_hybrid_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=JSON parse error: Expecting property name enclosed in double quotes: line 1 column 42 (char 41)
- `memorysmith_hybrid_search-5` (memorysmith_hybrid_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=JSON parse error: Expecting property name enclosed in double quotes: line 1 column 42 (char 41)
- `memorysmith_semantic_search-1` (memorysmith_semantic_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=JSON parse error: Expecting property name enclosed in double quotes: line 1 column 42 (char 41)
- `memorysmith_semantic_search-2` (memorysmith_semantic_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=JSON parse error: Expecting property name enclosed in double quotes: line 1 column 42 (char 41)
- `memorysmith_semantic_search-3` (memorysmith_semantic_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=JSON parse error: Expecting property name enclosed in double quotes: line 1 column 42 (char 41)
- `memorysmith_semantic_search-4` (memorysmith_semantic_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found
- `memorysmith_semantic_search-5` (memorysmith_semantic_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=JSON parse error: Expecting property name enclosed in double quotes: line 1 column 42 (char 41)

## Representative Output Snippets

### memorysmith_unified_search-1

- Prompt: search the wiki for kv cache options
- Base: [think] Okay, the user is asking me to search the wiki for information about kv cache options. I need to remember that I'm Athena, MemorySmith's local wiki assistant. When a search or retrieval action is requested, I should respond with ...
- Tuned: [think] The user is asking me to search the wiki for information about kv cache options. I need to respond with a JSON object containing a tool call for a search/retrieval action. Let me format this correctly as a JSON object with the to...

### memorysmith_unified_search-2

- Prompt: find docs for training harness
- Base: assistant [think] Okay, the user is asking for documentation on the training harness. I need to figure out how to respond as Athena, the local wiki assistant. First, I should check if there's a specific tool I need to use for retrieving ...
- Tuned: assistant [think] Thinking Process: 1. **Analyze the Request:** * User: "find docs for training harness" * Role: Athena, MemorySmith's local wiki assistant. * Constraint: When a search/retrieval action is requested, respond with exactly ...

### memorysmith_unified_search-3

- Prompt: lookup wiki notes about chat template
- Base: assistant [think] Okay, the user is asking me to look up wiki notes about chat templates. I need to check if there's any information in the MemorySmith's wiki about chat templates. Since I'm Athena, the local wiki assistant, I should use...
- Tuned: assistant [think] Okay, the user is asking me to look up wiki notes about chat templates. I need to check if there's a tool available for retrieving wiki information. Since I'm MemorySmith's local wiki assistant, I should use the search ...

### memorysmith_unified_search-4

- Prompt: search for model profile defaults
- Base: assistant [think] Okay, the user is asking me to search for model profile defaults. I need to figure out what they mean by that. Model profiles could refer to different things depending on the context. Maybe they're talking about machine...
- Tuned: assistant [think] Okay, the user is asking me to search for model profile defaults. I need to figure out how to handle this request. Since I'm Athena, the local wiki assistant, I should check if there's a tool available for searching. Th...

### memorysmith_unified_search-5

- Prompt: find references about code search eta
- Base: assistant [think] Okay, the user is asking for references about code search ETA. First, I need to figure out what they mean by "code search eta". ETA usually stands for Estimated Time of Arrival, but in the context of code search, that d...
- Tuned: assistant [think] Thinking Process: 1. **Analyze the Request:** * User: "find references about code search eta" * Intent: The user is asking for references related to "code search eta". * Context: I am Athena, MemorySmith's local wiki as...

### memorysmith_hybrid_search-1

- Prompt: run a hybrid search for request guard middleware
- Base: [think] Okay, the user is asking me to run a hybrid search for "request guard middleware". I need to figure out how to handle this. First, I remember that as Athena, I'm supposed to respond with a JSON object when a search/retrieval acti...
- Tuned: [think] The user is asking me to run a hybrid search for "request guard middleware". This appears to be a request for me to perform a search operation, likely on a knowledge base or documentation system. However, I need to be careful her...

### memorysmith_hybrid_search-2

- Prompt: hybrid search for source bundle auth behavior
- Base: [think] Okay, the user is asking about hybrid search for source bundle auth behavior. Let me start by recalling what I know about hybrid search. Hybrid search usually combines different search methods, like keyword and vector search, to ...
- Tuned: [think] The user is asking about "hybrid search for source bundle auth behavior" - this appears to be a technical question about authentication behavior in a source bundle context, possibly related to some kind of search or retrieval sys...

### memorysmith_hybrid_search-3

- Prompt: use hybrid search to find onnx semantic search notes
- Base: Okay, the user is asking me to use hybrid search to find ONNX semantic search notes. First, I need to understand what they're looking for. Hybrid search typically combines different search methods, like keyword and semantic search, to re...
- Tuned: Okay, the user is asking me to use hybrid search to find ONNX semantic search notes. Let me start by understanding what they need. First, hybrid search usually combines different search methods, like keyword and vector-based search. Sinc...
