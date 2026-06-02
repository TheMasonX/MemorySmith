#!/usr/bin/env python3
"""Generate targeted augmentations for weak tools and create an improved corpus."""
import json

# New examples targeting weak tools with distinctive keywords
# Format: (tool_name, user_prompt, system_prompt_suffix)
new_examples = [
    # memorysmith_unified_search (weakest: 0/5 — confused with memorysmith_search)
    # Key: Use "unified" in the prompt, or "broadly", "across the wiki"
    ("memorysmith_unified_search",
     "Use unified search to find anything about the new training harness across all sources."),
    ("memorysmith_unified_search",
     "I need a unified search for project-wiki-mcp-config and related codebase notes."),
    ("memorysmith_unified_search",
     "Search the wiki broadly with unified search for cache management strategies."),
    ("memorysmith_unified_search",
     "Do a unified search to find all references to OpenTelemetry configuration."),
    ("memorysmith_unified_search",
     "Unified search: look up page_visibility and code_search together in the wiki."),

    # memorysmith_search (already 4/5 good, but some confusion with "memory_search")
    # Reinforce: "search memories for exact terms"
    ("memorysmith_search",
     "Search memories for the exact term 'RequestGuardMiddleware' and nothing else."),
    ("memorysmith_search",
     "Find exact literal text memorysmith_now and show me the raw content."),
    ("memorysmith_search",
     "Search for the exact phrase 'training_harness_v2' in my knowledge base."),

    # memorysmith_task_list (not tested in spotcheck, but keep balanced)
    ("memorysmith_task_list",
     "List all tasks tagged with 'security' that are in progress."),
    ("memorysmith_task_list",
     "Show me tasks assigned to smith with status blocked."),
    ("memorysmith_task_list",
     "List tasks created in the last week tagged eval."),

    # memorysmith_task_get (not tested, but low example count)
    ("memorysmith_task_get",
     "Open task TSK-0209 and show me its full description."),
    ("memorysmith_task_get",
     "Get details for task tsk-0221 from the task store."),

    # memorysmith_code_search (7 examples, but important for app)
    ("memorysmith_code_search",
     "Run code search for the class TrainingHarnessRunnerService."),
    ("memorysmith_code_search",
     "Find where 'resolve_hyperparameters' is defined in the codebase."),

    # memorysmith_code_search_status (4 examples, rarely tested)
    ("memorysmith_code_search_status",
     "What is the current status of the code search index?"),
    ("memorysmith_code_search_status",
     "Check whether code search indexing has completed yet."),
]

system_prompts = {
    "memorysmith_unified_search": "You are Athena, MemorySmith's local wiki assistant. Use available MemorySmith MCP tools. When the user asks for a broad or unified search across multiple sources, use memorysmith_unified_search.",
    "memorysmith_search": "You are Athena, MemorySmith's local wiki assistant. Use available MemorySmith MCP tools. When the user asks for an exact keyword or literal term search in memories, use memorysmith_search.",
    "memorysmith_task_list": "You are Athena, MemorySmith's local wiki assistant. Use available MemorySmith MCP tools. When the user asks to list or show tasks with filters, use memorysmith_task_list.",
    "memorysmith_task_get": "You are Athena, MemorySmith's local wiki assistant. Use available MemorySmith MCP tools. When the user refers to a specific task ID, use memorysmith_task_get.",
    "memorysmith_code_search": "You are Athena, MemorySmith's local wiki assistant. Use available MemorySmith MCP tools. When the user asks to find code, classes, or file definitions, use memorysmith_code_search.",
    "memorysmith_code_search_status": "You are Athena, MemorySmith's local wiki assistant. Use available MemorySmith MCP tools. When the user asks about indexing status or code search health, use memorysmith_code_search_status.",
}

# Load existing corpus
existing_path = "Data/Training/exports/v6-toolselection-20260602.sft.jsonl"
with open(existing_path) as f:
    existing = [json.loads(l) for l in f]

print(f"Existing corpus: {len(existing)} records")

# Generate new records using patterns from the augment files
new_records = []
for tool, prompt in new_examples:
    system = system_prompts.get(tool, "You are Athena, MemorySmith's local wiki assistant. Use available MemorySmith MCP tools.")
    # Build the response based on tool
    if tool == "memorysmith_unified_search":
        args = '{"query": "' + prompt.split(" for ")[-1].split(" ")[0] + ' ' + prompt.split(" for ")[-1] if " for " in prompt else prompt[:30] + '", "maxResults": 5}'
    elif tool == "memorysmith_search":
        args = '{"query": "' + prompt.split("'")[1] if "'" in prompt else prompt.replace("Search for ", "").replace("Search memories for ", "").replace("Find exact ", "").rstrip('.') + '", "maxResults": 5}'
    elif tool == "memorysmith_task_list":
        args = '{"tags": ["security"], "status": "in-progress"}'
        if "eval" in prompt:
            args = '{"tags": ["eval"]}'
        elif "blocked" in prompt:
            args = '{"status": "blocked"}'
        elif "assigned" in prompt:
            args = '{"assignee": "smith"}'
    elif tool == "memorysmith_task_get":
        args = '{"id": "TSK-0209"}'
        if "tsk-0221" in prompt:
            args = '{"id": "tsk-0221"}'
    elif tool == "memorysmith_code_search":
        if "TrainingHarnessRunnerService" in prompt:
            args = '{"query": "TrainingHarnessRunnerService", "maxResults": 5}'
        else:
            args = '{"query": "resolve_hyperparameters", "maxResults": 5}'
    elif tool == "memorysmith_code_search_status":
        args = '{}'
    
    # Fix arg quoting
    if tool in ("memorysmith_unified_search",):
        # Build a proper argument from the prompt
        query_term = prompt.replace("Use unified search to find ", "").replace("I need a unified search for ", "").replace("Search the wiki broadly with unified search for ", "").replace("Do a unified search to find all references to ", "").replace("Unified search: look up ", "").replace(" and ", " ")[:60]
        args = '{"query": "' + query_term.rstrip('.') + '", "maxResults": 5}'
    elif tool == "memorysmith_search":
        if "'" in prompt:
            query_term = prompt.split("'")[1]
        elif "exact term" in prompt:
            query_term = prompt.split("'")[1]
        else:
            query_term = prompt.replace("Search memories for the exact term ", "").replace("Find exact literal text ", "").replace("Search for the exact phrase ", "").rstrip('.')
        args = '{"query": "' + query_term + '", "maxResults": 5}'
    
    record = {
        "messages": [
            {"role": "system", "content": system},
            {"role": "user", "content": prompt},
            {"role": "assistant", "content": '{"toolCalls":[{"name":"' + tool + '","arguments":' + args + '}]}'}
        ]
    }
    new_records.append(record)

# Write augmented corpus
all_records = existing + new_records
out_path = "Data/Training/exports/v6-toolselection-v2-20260602.sft.jsonl"
with open(out_path, "w") as f:
    for rec in all_records:
        f.write(json.dumps(rec, ensure_ascii=True) + "\n")

print(f"Augmented corpus: {len(all_records)} records ({len(new_records)} new)")
print(f"Written to: {out_path}")
