#!/usr/bin/env python3
"""
Build a canonical-only SFT corpus by scanning existing exports and
keeping only examples where the assistant response is a single JSON
toolCalls object. Remaps common alias tool names to canonical
`memorysmith_*` names.

Usage:
    python Scripts/build_canonical_corpus.py

Writes: Data/Training/exports/canonical-only-20260601.sft.jsonl
"""
from pathlib import Path
import json
import re

ROOT = Path(__file__).resolve().parents[1]
EXPORT_DIR = ROOT / "Data" / "Training" / "exports"
OUTPUT = EXPORT_DIR / "canonical-only-20260601.sft.jsonl"

# Canonical tool names we expect
CANONICAL = {
    "memorysmith_code_search",
    "memorysmith_code_search_status",
    "memorysmith_context_pack",
    "memorysmith_get",
    "memorysmith_hybrid_search",
    "memorysmith_page_get",
    "memorysmith_page_search",
    "memorysmith_search",
    "memorysmith_semantic_search",
    "memorysmith_task_get",
    "memorysmith_task_list",
    "memorysmith_unified_search",
}

# Common alias -> canonical mapping (non-exhaustive)
ALIAS_MAP = {
    "search": "memorysmith_search",
    "get": "memorysmith_get",
    "open_page": "memorysmith_page_get",
    "page_get": "memorysmith_page_get",
    "page_search": "memorysmith_page_search",
    "fetch_task": "memorysmith_task_get",
    "task_get": "memorysmith_task_get",
    "task_list": "memorysmith_task_list",
    "context_pack": "memorysmith_context_pack",
    "hybrid_search": "memorysmith_hybrid_search",
    "semantic_search": "memorysmith_semantic_search",
    "unified_search": "memorysmith_unified_search",
    "code_search": "memorysmith_code_search",
    "code_search_status": "memorysmith_code_search_status",
}

JSON_OBJ_RE = re.compile(r"(\{.*\})", re.DOTALL)


def extract_assistant_json(messages):
    # messages is a list of dicts with 'role' and 'content'
    for m in reversed(messages):
        if m.get("role") == "assistant":
            content = m.get("content", "")
            # find first {...} block
            mobj = JSON_OBJ_RE.search(content)
            if not mobj:
                return None
            jtxt = mobj.group(1)
            try:
                parsed = json.loads(jtxt)
                return parsed
            except Exception:
                # maybe it's double-encoded (string containing escaped JSON)
                try:
                    inner = json.loads(content)
                    if isinstance(inner, str):
                        inner_obj = json.loads(inner)
                        return inner_obj
                except Exception:
                    return None
    return None


def remap_toolcalls(parsed):
    # parsed expected to have key 'toolCalls' -> list
    if not isinstance(parsed, dict):
        return None
    tcalls = parsed.get("toolCalls") or parsed.get("tool_calls")
    if not isinstance(tcalls, list):
        return None
    changed = False
    for call in tcalls:
        name = call.get("name")
        if not name:
            continue
        if name in ALIAS_MAP:
            call["name"] = ALIAS_MAP[name]
            changed = True
    # verify all names are canonical or mapped
    for call in tcalls:
        if call.get("name") not in CANONICAL:
            return None
    return {"toolCalls": tcalls}


def build():
    written = 0
    processed = 0
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    with OUTPUT.open("w", encoding="utf-8") as out:
        for f in sorted(EXPORT_DIR.glob("*.sft.jsonl")):
            if f.name == OUTPUT.name:
                continue
            with f.open("r", encoding="utf-8") as fh:
                for line in fh:
                    line = line.strip()
                    if not line:
                        continue
                    processed += 1
                    try:
                        obj = json.loads(line)
                    except Exception:
                        continue
                    messages = obj.get("messages") or obj.get("dialog") or None
                    if not messages or not isinstance(messages, list):
                        continue
                    parsed = extract_assistant_json(messages)
                    if not parsed:
                        continue
                    remapped = remap_toolcalls(parsed)
                    if not remapped:
                        continue
                    # replace assistant content with compact JSON
                    # find last assistant message index
                    for m in reversed(messages):
                        if m.get("role") == "assistant":
                            m["content"] = json.dumps(remapped, separators=(",",":"))
                            break
                    out.write(json.dumps(obj, ensure_ascii=False) + "\n")
                    written += 1
    print(f"Processed {processed} lines, wrote {written} canonical examples to {OUTPUT}")


if __name__ == "__main__":
    build()
