#!/usr/bin/env python3
import json, re
from collections import Counter
from pathlib import Path

def get_tool_dist(path, max_lines=None):
    c = Counter()
    with open(path) as f:
        for i, line in enumerate(f):
            if max_lines and i >= max_lines:
                break
            line = line.strip()
            if not line:
                continue
            try:
                row = json.loads(line)
            except json.JSONDecodeError:
                continue
            for msg in row.get("messages", []):
                if msg.get("role") == "assistant":
                    for m in re.finditer(r'"name"\s*:\s*"([^"]+)"', msg.get("content", "")):
                        c[m.group(1)] += 1
    return c

corpora = [
    ("v7-clean", "Data/Training/exports/distilled-all-cat-20260530-v7-clean.sft.jsonl", None),
    ("v6-batched-step1", "Data/Training/exports/v6-chatml-batched-20260601-step1.sft.jsonl", None),
    ("canonical (first 500)", "Data/Training/exports/canonical-only-20260601.sft.jsonl", 500),
    ("canonical (full)", "Data/Training/exports/canonical-only-20260601.sft.jsonl", None),
]

for label, path, limit in corpora:
    dist = get_tool_dist(path, max_lines=limit)
    total = sum(dist.values())
    print(f"\n=== {label} ===")
    print(f"  Total tool refs: {total}")
    for tool, count in dist.most_common():
        print(f"  {tool}: {count} ({count/total*100:.1f}%)")
