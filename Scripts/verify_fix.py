#!/usr/bin/env python3
import json

with open("Data/Training/distilled-all-categories-20260529/distilled-all-categories-20260529.sft.fixed.jsonl") as f:
    lines = f.readlines()

issues = 0
for idx, line in enumerate(lines):
    row = json.loads(line)
    msgs = row.get("messages", [])
    for j, m in enumerate(msgs):
        if m.get("role") == "system" and j != 0:
            issues += 1
            if issues <= 3:
                print(f"Record {idx}: system at position {j}")
                for k, m2 in enumerate(msgs):
                    print(f"  [{k}] role={m2['role']}, content={m2.get('content','')[:60]!r}")
                print()

print(f"\nTotal issues: {issues}")
