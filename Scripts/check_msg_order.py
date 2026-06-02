#!/usr/bin/env python3
import json, sys

paths = [
    "Data/Training/distilled-all-categories-20260529/distilled-all-categories-20260529.sft.jsonl",
    "Data/Training/distilled-all-categories-20260529/distilled-tool-selection-augment-20260530.sft.jsonl",
]

for path in paths:
    with open(path) as f:
        first = json.loads(f.readline())
    print(f"\n=== {path} ===")
    for msg in first.get("messages", []):
        content_preview = msg.get("content", "")[:80]
        print(f"  role={msg.get('role')}, content={content_preview!r}")
    
    # Check all records for system message position
    with open(path) as f:
        issues = 0
        total = 0
        for line in f:
            total += 1
            row = json.loads(line)
            msgs = row.get("messages", [])
            # Find system messages
            for i, msg in enumerate(msgs):
                if msg.get("role") == "system" and i != 0:
                    issues += 1
        print(f"  Records: {total + 1}, system-not-first issues: {issues}")
