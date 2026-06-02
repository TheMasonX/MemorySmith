#!/usr/bin/env python3
"""
Fix distilled corpus for Qwen chat template compatibility.

Two issues fixed:
1. Multiple system messages → merge into one (tool results prepended to main prompt).
2. After merging, ensure system remains at position 0.
"""
import json
from pathlib import Path

paths = [
    "Data/Training/distilled-all-categories-20260529/distilled-all-categories-20260529.sft.jsonl",
    "Data/Training/distilled-all-categories-20260529/distilled-tool-selection-augment-20260530.sft.jsonl",
]

for src_path in paths:
    src = Path(src_path)
    out = src.with_name(src.stem + ".qwenfix.jsonl")

    fixed = 0
    with open(src) as f_in, open(out, "w") as f_out:
        for line in f_in:
            row = json.loads(line)
            msgs = row.get("messages", [])

            # Separate system messages from others
            system_msgs = [m for m in msgs if m.get("role") == "system"]
            other_msgs = [m for m in msgs if m.get("role") != "system"]

            if len(system_msgs) > 1:
                # Merge all system messages into one, tool results first then main prompt
                tool_results = [m for m in system_msgs if "tool results" in m.get("content", "").lower() or "Local MemorySmith" in m.get("content", "")]
                main_prompts = [m for m in system_msgs if m not in tool_results]
                
                new_content_parts = []
                for tr in tool_results:
                    new_content_parts.append(tr.get("content", ""))
                for mp in main_prompts:
                    new_content_parts.append(mp.get("content", ""))
                
                merged_content = "\n\n".join(new_content_parts)
                system_msgs = [{"role": "system", "content": merged_content}]
                fixed += 1

            # Ensure system is at position 0
            row["messages"] = system_msgs + other_msgs
            f_out.write(json.dumps(row, ensure_ascii=True) + "\n")

    # Verify
    issues = 0
    with open(out) as f:
        for line in f:
            row = json.loads(line)
            msgs = row.get("messages", [])
            for i, m in enumerate(msgs):
                if m.get("role") == "system" and i != 0:
                    issues += 1
    print(f"{src.name}: {fixed} records fixed, {len(list(open(out)))} total, remaining issues: {issues}")
