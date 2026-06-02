"""Regenerate training corpus and spotcheck eval for TSK-0271."""
import json
from collections import Counter

# ── 1. Filter corpus ──────────────────────────────────────────────────
with open("Data/Training/exports/v6-toolselection-v2-20260602.sft.jsonl") as f:
    records = [json.loads(line) for line in f]

deprecated = ["memorysmith_semantic_search", "memorysmith_unified_search"]
kept = [r for r in records if not any(d in json.dumps(r) for d in deprecated)]
print(f"Corpus: {len(records)} -> {len(kept)} records after removing deprecated tools")

# Tool distribution
tools = Counter()
for r in kept:
    text = json.dumps(r)
    for t in ["memorysmith_search", "memorysmith_hybrid_search", "memorysmith_context_pack",
              "memorysmith_get", "memorysmith_page_search", "memorysmith_page_get",
              "memorysmith_task_list", "memorysmith_task_get",
              "memorysmith_code_search", "memorysmith_code_search_status"]:
        if t in text:
            tools[t] += 1

print("Tool distribution in kept corpus:")
for t, n in sorted(tools.items(), key=lambda x: -x[1]):
    print(f"  {t:45s} {n}")

output = "Data/Training/exports/v6-toolselection-v3-consolidated.sft.jsonl"
with open(output, "w") as f:
    for r in kept:
        f.write(json.dumps(r, ensure_ascii=False) + "\n")
print(f"\nSaved: {output} ({len(kept)} records)")


# ── 2. Create fresh spotcheck eval ────────────────────────────────────
cases = [
    # memorysmith_search (exact terms) — 5 cases
    {"caseId": "search-exact-1", "expectedTool": "memorysmith_search",
     "query": "find memory with exact tag governance"},
    {"caseId": "search-exact-2", "expectedTool": "memorysmith_search",
     "query": "search for memory by id project-wiki-tool-catalog"},
    {"caseId": "search-exact-3", "expectedTool": "memorysmith_search",
     "query": "find records with status Core and tag project-wiki"},
    {"caseId": "search-exact-4", "expectedTool": "memorysmith_search",
     "query": "search for literal term MCP authorization matrix"},
    {"caseId": "search-exact-5", "expectedTool": "memorysmith_search",
     "query": "find memory matching exact phrase reciprocal rank fusion"},
    # memorysmith_hybrid_search (default) — 8 cases
    {"caseId": "hybrid-default-1", "expectedTool": "memorysmith_hybrid_search",
     "query": "what does the project wiki say about authentication"},
    {"caseId": "hybrid-default-2", "expectedTool": "memorysmith_hybrid_search",
     "query": "find information about tool calling in the wiki"},
    {"caseId": "hybrid-default-3", "expectedTool": "memorysmith_hybrid_search",
     "query": "search memories for configuration references"},
    {"caseId": "hybrid-default-4", "expectedTool": "memorysmith_hybrid_search",
     "query": "look up documentation about semantic search setup"},
    {"caseId": "hybrid-default-5", "expectedTool": "memorysmith_hybrid_search",
     "query": "find wiki records about the chat agent prompt"},
    {"caseId": "hybrid-default-6", "expectedTool": "memorysmith_hybrid_search",
     "query": "search the wiki for architecture decisions"},
    {"caseId": "hybrid-default-7", "expectedTool": "memorysmith_hybrid_search",
     "query": "find records about maintenance agent governance"},
    {"caseId": "hybrid-default-8", "expectedTool": "memorysmith_hybrid_search",
     "query": "look up information about proposal workflow state machine"},
    # memorysmith_page_search — 3 cases
    {"caseId": "page-search-1", "expectedTool": "memorysmith_page_search",
     "query": "find wiki pages about deployment"},
    {"caseId": "page-search-2", "expectedTool": "memorysmith_page_search",
     "query": "search pages for troubleshooting guides"},
    {"caseId": "page-search-3", "expectedTool": "memorysmith_page_search",
     "query": "find pages about training the model"},
    # memorysmith_task_list — 3 cases
    {"caseId": "task-list-1", "expectedTool": "memorysmith_task_list",
     "query": "what tasks are in progress"},
    {"caseId": "task-list-2", "expectedTool": "memorysmith_task_list",
     "query": "list all finished tasks assigned to me"},
    {"caseId": "task-list-3", "expectedTool": "memorysmith_task_list",
     "query": "find tasks about search tool consolidation"},
    # memorysmith_code_search — 3 cases
    {"caseId": "code-search-1", "expectedTool": "memorysmith_code_search",
     "query": "find the ChatToolCatalog class in the codebase"},
    {"caseId": "code-search-2", "expectedTool": "memorysmith_code_search",
     "query": "search for MCP controller implementation"},
    {"caseId": "code-search-3", "expectedTool": "memorysmith_code_search",
     "query": "find source for intent interceptor patterns"},
]

spot = {
    "generatedAtUtc": "2026-06-02T22:00:00Z",
    "modelId": "Qwen/Qwen3.5-4B",
    "adapterPath": None,
    "caseCount": len(cases),
    "perToolCases": {t: sum(1 for c in cases if c["expectedTool"] == t)
                     for t in sorted(set(c["expectedTool"] for c in cases))},
    "toolsCovered": sorted(set(c["expectedTool"] for c in cases)),
    "base": {
        "caseCount": len(cases),
        "rows": [{"caseId": c["caseId"], "expectedTool": c["expectedTool"],
                  "predictedTool": None, "toolMatch": False, "toolCall": None}
                 for c in cases]
    },
    "tuned": {
        "caseCount": len(cases),
        "rows": [{"caseId": c["caseId"], "expectedTool": c["expectedTool"],
                  "predictedTool": None, "toolMatch": False, "toolCall": None}
                 for c in cases]
    },
    "evalVersion": "consolidated-v1"
}

print(f"\nSpotcheck: {spot['caseCount']} cases covering {len(spot['toolsCovered'])} tools:")
for t, n in sorted(spot["perToolCases"].items(), key=lambda x: -x[1]):
    print(f"  {t:45s} {n}")

with open("Data/Pages/research/training/tool-ab-spotcheck-consolidated-20260602.data.json", "w") as f:
    json.dump(spot, f, indent=2, ensure_ascii=False)
print("Saved: Data/Pages/research/training/tool-ab-spotcheck-consolidated-20260602.data.json")
