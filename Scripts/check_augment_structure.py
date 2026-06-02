#!/usr/bin/env python3
import json
for path in [
    "Data/Training/exports/distilled-all-cat-20260601-step2-routing20.sft.jsonl",
    "Data/Training/exports/distilled-all-cat-20260601-step2-unified20.sft.jsonl",
]:
    with open(path) as f:
        first = json.loads(f.readline())
    msgs = first.get("messages", [])
    print(f"{path}:")
    for i, m in enumerate(msgs):
        print(f"  [{i}] role={m['role']}, content={m.get('content','')[:60]}")
    print()
