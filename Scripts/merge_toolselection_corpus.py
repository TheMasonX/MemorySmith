#!/usr/bin/env python3
"""Merge v6-chatml corpus with tool-selection augment files to create ~100-example corpus."""
import json, re
from collections import Counter
from pathlib import Path

base = "Data/Training/exports/distilled-all-cat-20260530-v6-chatml.sft.jsonl"
augments = [
    "Data/Training/exports/distilled-all-cat-20260601-step2-routing20.sft.jsonl",
    "Data/Training/exports/distilled-all-cat-20260601-step2-unified20.sft.jsonl",
]
out = "Data/Training/exports/v6-toolselection-20260602.sft.jsonl"

# Load base
records = []
with open(base) as f:
    for line in f:
        records.append(json.loads(line))

# Load augments
for aug in augments:
    with open(aug) as f:
        for line in f:
            records.append(json.loads(line))

# Write merged
with open(out, "w") as f:
    for rec in records:
        f.write(json.dumps(rec, ensure_ascii=True) + "\n")

print(f"Merged {len(records)} records -> {out}")

# Verify quality
issues = 0
c = Counter()
for rec in records:
    msgs = rec.get("messages", [])
    if not msgs or len(msgs) < 2:
        issues += 1
    if msgs[0].get("role") != "system":
        issues += 1
    for m in msgs:
        if m.get("role") == "assistant":
            for match in re.finditer(r'"name"\s*:\s*"([^"]+)"', m.get("content", "")):
                c[match.group(1)] += 1

print(f"Issues: {issues}")
print(f"\nTool distribution ({sum(c.values())} total refs):")
for tool, count in c.most_common():
    print(f"  {tool}: {count}")
