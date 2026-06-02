#!/usr/bin/env python3
import json

# Check canonical corpus
with open("Data/Training/exports/canonical-only-20260601.sft.jsonl") as f:
    lines = f.readlines()

print("=== Canonical corpus message structures (first 5) ===")
for i in range(5):
    row = json.loads(lines[i])
    msgs = row.get("messages", [])
    print(f"Record {i}:")
    for j, m in enumerate(msgs):
        print(f"  [{j}] role={m['role']}, content={m.get('content','')[:60]}")
    print()

# Check the issue in the fixed file
print("=== Distilled fixed file multi-system records ===")
with open("Data/Training/distilled-all-categories-20260529/distilled-all-categories-20260529.sft.fixed.jsonl") as f:
    for idx, line in enumerate(f):
        row = json.loads(line)
        msgs = row.get("messages", [])
        sys_count = sum(1 for m in msgs if m.get("role") == "system")
        if sys_count > 1:
            print(f"Record {idx}: {sys_count} system messages")
            for j, m in enumerate(msgs):
                print(f"  [{j}] role={m['role']}, content={m.get('content','')[:60]}")
            print()
