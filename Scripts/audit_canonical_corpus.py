#!/usr/bin/env python3
"""Audit canonical corpus for quality issues."""
import json
import sys
from collections import Counter

path = "Data/Training/exports/canonical-only-20260601.sft.jsonl"
issues = []
total = 0
role_counts = Counter()

with open(path) as f:
    for idx, line in enumerate(f):
        row = json.loads(line)
        total += 1
        msgs = row.get("messages", [])

        if not msgs or len(msgs) < 2:
            issues.append(f"Line {idx}: {len(msgs)} messages (need >=2)")
            continue

        for j, m in enumerate(msgs):
            role = m.get("role", "")
            role_counts[role] += 1
            if role not in ("system", "user", "assistant", "tool"):
                issues.append(f"Line {idx}, msg[{j}]: unknown role {role!r}")

        if msgs[0].get("role") != "system":
            issues.append(f"Line {idx}: system not at position 0")

        for j, m in enumerate(msgs):
            if m.get("role") == "user" and not m.get("content", "").strip():
                issues.append(f"Line {idx}, msg[{j}]: empty user content")
            elif m.get("role") == "assistant":
                content = m.get("content", "")
                if not content.strip():
                    issues.append(f"Line {idx}, msg[{j}]: empty assistant content")
                else:
                    try:
                        parsed = json.loads(content)
                        if not isinstance(parsed.get("toolCalls"), list):
                            issues.append(f"Line {idx}, msg[{j}]: no toolCalls array")
                    except json.JSONDecodeError:
                        pass  # non-JSON assistant is fine (final reply)

if issues:
    print(f"Found {len(issues)} issues:")
    for issue in issues[:30]:
        print(f"  {issue}")
    if len(issues) > 30:
        print(f"  ... and {len(issues)-30} more")
else:
    print("No issues found — corpus is clean.")

print(f"\nTotal records: {total}")
print(f"Role distribution: {dict(role_counts)}")

# Check for duplicates
seen = set()
dups = 0
with open(path) as f:
    for idx, line in enumerate(f):
        row = json.loads(line)
        key = ""
        for m in row.get("messages", []):
            if m.get("role") == "assistant":
                key += m.get("content", "")
        if key in seen:
            dups += 1
        seen.add(key)

if dups:
    print(f"Duplicate records (by assistant content): {dups}")
else:
    print("No duplicate records found.")
