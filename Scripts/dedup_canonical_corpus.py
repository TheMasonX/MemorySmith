#!/usr/bin/env python3
"""Deduplicate canonical corpus while keeping full diversity."""
import json
from pathlib import Path

src = Path("Data/Training/exports/canonical-only-20260601.sft.jsonl")
out = Path("Data/Training/exports/canonical-dedup-20260602.sft.jsonl")

seen = set()
kept = 0
skipped = 0
with open(src) as f_in, open(out, "w") as f_out:
    for line in f_in:
        row = json.loads(line)
        # Build dedup key from assistant content only
        key = ""
        for m in row.get("messages", []):
            if m.get("role") == "assistant":
                key += m.get("content", "")
        if key not in seen:
            seen.add(key)
            f_out.write(line)
            kept += 1
        else:
            skipped += 1

print(f"Kept: {kept} unique records")
print(f"Skipped: {skipped} duplicates")
print(f"Output: {out}")
