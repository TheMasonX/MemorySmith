#!/usr/bin/env python3
"""Fix system message ordering: ensure system role is always at position 0."""
import json
from pathlib import Path

src = Path("Data/Training/distilled-all-categories-20260529/distilled-all-categories-20260529.sft.jsonl")
fixed = src.with_suffix(".fixed.jsonl")

fixed_records = []
with open(src) as f:
    for line in f:
        row = json.loads(line)
        msgs = row.get("messages", [])
        # Extract all system messages and non-system messages
        system_msgs = [m for m in msgs if m.get("role") == "system"]
        other_msgs = [m for m in msgs if m.get("role") != "system"]
        # Put system messages first, then the rest in order
        row["messages"] = system_msgs + other_msgs
        fixed_records.append(row)

with open(fixed, "w") as f:
    for rec in fixed_records:
        f.write(json.dumps(rec, ensure_ascii=True) + "\n")

print(f"Fixed {len(fixed_records)} records. Wrote {fixed}")

# Verify: check system-not-first count
issues = 0
with open(fixed) as f:
    for line in f:
        row = json.loads(line)
        msgs = row.get("messages", [])
        for i, msg in enumerate(msgs):
            if msg.get("role") == "system" and i != 0:
                issues += 1
print(f"Remaining system-not-first issues: {issues}")
