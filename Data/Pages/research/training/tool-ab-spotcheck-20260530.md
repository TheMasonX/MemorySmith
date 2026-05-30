# Tool A/B Spot Check - Base vs Tuned (2026-05-30)

Generated: 2026-05-30 17:56:51Z

## Scope

- Manual spot-check battery: 5 prompts per tool across 12 Athena tools (60 total A/B pairs)
- Base model: `Qwen/Qwen3.5-4B`
- Tuned adapter: `D:\temp\memorysmith-training\runs\distilled-all-cat-20260530-121744\adapter`
- Raw results JSON: `artifacts/training-ab/tool-ab-spotcheck-20260530-124457.json`

## Headline Metrics

| Metric | Base | Tuned | Delta |
|---|---:|---:|---:|
| Envelope valid | 0/60 (0.0%) | 22/60 (36.7%) | +22 |
| Expected tool match | 0/60 (0.0%) | 6/60 (10.0%) | +6 |

## Per-Tool Results

| Tool | Cases | Base envelope | Base tool match | Tuned envelope | Tuned tool match | Delta envelope | Delta tool match |
|---|---:|---:|---:|---:|---:|---:|---:|
| `memorysmith_code_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 3/5 (60.0%) | 3/5 (60.0%) | +3 | +3 |
| `memorysmith_code_search_status` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | +0 | +0 |
| `memorysmith_context_pack` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 1/5 (20.0%) | 1/5 (20.0%) | +1 | +1 |
| `memorysmith_get` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 2/5 (40.0%) | 0/5 (0.0%) | +2 | +0 |
| `memorysmith_hybrid_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 3/5 (60.0%) | 0/5 (0.0%) | +3 | +0 |
| `memorysmith_page_get` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 3/5 (60.0%) | 0/5 (0.0%) | +3 | +0 |
| `memorysmith_page_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 3/5 (60.0%) | 0/5 (0.0%) | +3 | +0 |
| `memorysmith_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | +0 | +0 |
| `memorysmith_semantic_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 1/5 (20.0%) | 1/5 (20.0%) | +1 | +1 |
| `memorysmith_task_get` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | 0/5 (0.0%) | +0 | +0 |
| `memorysmith_task_list` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 2/5 (40.0%) | 1/5 (20.0%) | +2 | +1 |
| `memorysmith_unified_search` | 5 | 0/5 (0.0%) | 0/5 (0.0%) | 4/5 (80.0%) | 0/5 (0.0%) | +4 | +0 |

## Notable Improvements

- `memorysmith_unified_search-2` (memorysmith_unified_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memorysmith_search); prompt="find docs for training harness"
- `memorysmith_unified_search-3` (memorysmith_unified_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memorysmith_search); prompt="lookup wiki notes about chat template"
- `memorysmith_unified_search-4` (memorysmith_unified_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memorysmith_search); prompt="search for model profile defaults"
- `memorysmith_unified_search-5` (memorysmith_unified_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memorysmith_search); prompt="find references about code search eta"
- `memorysmith_hybrid_search-1` (memorysmith_hybrid_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memorysmith_search); prompt="run a hybrid search for request guard middleware"
- `memorysmith_hybrid_search-2` (memorysmith_hybrid_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memorysmith_search); prompt="hybrid search for source bundle auth behavior"
- `memorysmith_hybrid_search-4` (memorysmith_hybrid_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memorysmith_search); prompt="hybrid search for maintenance proposal workflow"
- `memorysmith_semantic_search-4` (memorysmith_semantic_search): base(match=False, env=False, pred=None) -> tuned(match=True, env=True, pred=memorysmith_semantic_search); prompt="semantic search for source-link default app open"
- `memorysmith_context_pack-5` (memorysmith_context_pack): base(match=False, env=False, pred=None) -> tuned(match=True, env=True, pred=memorysmith_context_pack); prompt="context pack for query chatml template"
- `memorysmith_get-2` (memorysmith_get): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memorysmith_open); prompt="open memory mem_training_001"
- `memorysmith_get-3` (memorysmith_get): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memorysmith_search); prompt="get memory mem_ops_009"
- `memorysmith_page_search-2` (memorysmith_page_search): base(match=False, env=False, pred=None) -> tuned(match=False, env=True, pred=memorysmith_search); prompt="search wiki pages for training harness"

## Notable Regressions

- None

## Persistent Failures (Both Models)

- `memorysmith_unified_search-1` (memorysmith_unified_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=toolCalls missing or empty
- `memorysmith_unified_search-2` (memorysmith_unified_search): base pred=None, tuned pred=memorysmith_search, baseErr=No JSON object found, tunedErr=None
- `memorysmith_unified_search-3` (memorysmith_unified_search): base pred=None, tuned pred=memorysmith_search, baseErr=No JSON object found, tunedErr=None
- `memorysmith_unified_search-4` (memorysmith_unified_search): base pred=None, tuned pred=memorysmith_search, baseErr=No JSON object found, tunedErr=None
- `memorysmith_unified_search-5` (memorysmith_unified_search): base pred=None, tuned pred=memorysmith_search, baseErr=No JSON object found, tunedErr=None
- `memorysmith_hybrid_search-1` (memorysmith_hybrid_search): base pred=None, tuned pred=memorysmith_search, baseErr=No JSON object found, tunedErr=None
- `memorysmith_hybrid_search-2` (memorysmith_hybrid_search): base pred=None, tuned pred=memorysmith_search, baseErr=No JSON object found, tunedErr=None
- `memorysmith_hybrid_search-3` (memorysmith_hybrid_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found
- `memorysmith_hybrid_search-4` (memorysmith_hybrid_search): base pred=None, tuned pred=memorysmith_search, baseErr=No JSON object found, tunedErr=None
- `memorysmith_hybrid_search-5` (memorysmith_hybrid_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found
- `memorysmith_semantic_search-1` (memorysmith_semantic_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found
- `memorysmith_semantic_search-2` (memorysmith_semantic_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found
- `memorysmith_semantic_search-3` (memorysmith_semantic_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found
- `memorysmith_semantic_search-5` (memorysmith_semantic_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found
- `memorysmith_search-1` (memorysmith_search): base pred=None, tuned pred=None, baseErr=No JSON object found, tunedErr=No JSON object found

## Representative Output Snippets

### memorysmith_unified_search-1

- Prompt: search the wiki for kv cache options
- Base: 5.0 </think> Hello! I'm **Athena**, your local wiki assistant. I can help you search for information about **KV Cache (Key-Value Cache)** options, but I need to clarify a few things: 1. **What specific context are you asking about?** -
- Tuned: WIKI_SEARCH{"query":"kv cache options","limit":10} </think>

### memorysmith_unified_search-2

- Prompt: find docs for training harness
- Base: </think> I can't find any documentation for a "training harness" in the MemorySmith project. This might be: 1. A different project name 2. A feature not yet documented 3. A misunderstanding of the terminology Could you clarify: - What sp...
- Tuned: I need to search for documentation about "training harness" in the Memorysmith knowledge base. Let me construct a query to find relevant docs. </think> {"toolCalls":[{"name":"memorysmith_search","arguments":{"query":"training harness","l...

### memorysmith_unified_search-3

- Prompt: lookup wiki notes about chat template
- Base: 1. **Identify the user's intent:** The user is asking for "wiki notes about chat template". This implies they want information, likely from a specific context (like a game, a software project, or a specific wiki platform) regarding a "ch...
- Tuned: I need to search for wiki notes about "chat template" in Memorysmith. Let me construct a proper memorysmith query. </think> {"toolCalls":[{"name":"memorysmith_search","arguments":{"query":"chat template","limit":5}}]}

### memorysmith_unified_search-4

- Prompt: search for model profile defaults
- Base: </think> I can't search for or access external model profile defaults, as I don't have direct access to external databases or real-time search capabilities. However, I can help you understand what model profile defaults typically include...
- Tuned: I need to search for information about "model profile defaults" in the MemorySmith knowledge base. Let me construct a memorysmith query for this. </think> {"toolCalls":[{"name":"memorysmith_search","arguments":{"query":"model profile def...

### memorysmith_unified_search-5

- Prompt: find references about code search eta
- Base: user find references about code search eta assistant <think> </think> Hello! I'm **Athena**, your local wiki assistant. I can help you find references about **Code Search ETA** (Estimated Time of Arrival) or similar concepts, but I need ...
- Tuned: I need to search memorysmith for references about "code search eta". Let me construct a memorysmith query for this. </think> {"toolCalls":[{"name":"memorysmith_search","arguments":{"query":"code search eta","limit":10}}]}

### memorysmith_hybrid_search-1

- Prompt: run a hybrid search for request guard middleware
- Base: user I need to run a hybrid search for request guard middleware. assistant <think> Okay, the user is asking me to run a hybrid search for request guard middleware. Let me think about how to approach this. First, I need to understand what...
- Tuned: </think> {"toolCalls":[{"name":"memorysmith_search","arguments":{"query":"request guard middleware","limit":5,"type":"hybrid"}}]}{"smithMemorysmithsmithsmithsmithsmithsmithsmithsmithsmithsmithsmithsmithsmithsmithsmithsmithsmithsmithsmith...

### memorysmith_hybrid_search-2

- Prompt: hybrid search for source bundle auth behavior
- Base: Hypothetical User Query: "hybrid search for source bundle auth behavior" My Role: I am Athena, MemorySmith's local wiki assistant. I need to provide accurate, helpful information about hybrid search and source bundle authentication behav...
- Tuned: </think> {"toolCalls":[{"name":"memorysmith_search","arguments":{"query":"source bundle auth behavior","limit":5}}]}{"memorysmithsmithsmithsmithsmithsmithsmithsmithsmithsmithsmithsmithsmithsmithsmithsmithsmithsmithsmithsmithsmithsmithsmi...

### memorysmith_hybrid_search-3

- Prompt: use hybrid search to find onnx semantic search notes
- Base: Okay, the user is asking me to use hybrid search to find ONNX semantic search notes. Let me start by understanding what they need. First, hybrid search usually combines different search methods, like keyword search and semantic search. O...
- Tuned: The user wants me to use hybrid search to find notes about "onnx semantic search". This is a memorysmith query. I need to construct a memorysmith query with the appropriate parameters for hybrid search. Memorysmith uses hybrid search whi...
