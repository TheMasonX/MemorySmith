#!/usr/bin/env python3
import argparse
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def pct(n: int, d: int) -> str:
    if d <= 0:
        return "0.0%"
    return f"{(100.0 * n / d):.1f}%"


def tool_row(tool: str, base: dict[str, Any], tuned: dict[str, Any]) -> str:
    b_count = int(base.get("count", 0))
    t_count = int(tuned.get("count", 0))
    b_env = int(base.get("envelopeValid", 0))
    b_match = int(base.get("toolMatch", 0))
    t_env = int(tuned.get("envelopeValid", 0))
    t_match = int(tuned.get("toolMatch", 0))
    delta_match = t_match - b_match
    delta_env = t_env - b_env
    return (
        f"| `{tool}` | {b_count} | {b_env}/{b_count} ({pct(b_env, b_count)}) | "
        f"{b_match}/{b_count} ({pct(b_match, b_count)}) | "
        f"{t_env}/{t_count} ({pct(t_env, t_count)}) | {t_match}/{t_count} ({pct(t_match, t_count)}) | "
        f"{delta_env:+d} | {delta_match:+d} |"
    )


def brief(text: str, limit: int = 160) -> str:
    s = " ".join((text or "").replace("<think>", "[think]").replace("</think>", "[/think]").split())
    if len(s) <= limit:
        return s
    return s[: limit - 3] + "..."


def compact_table_row(cells: list[str]) -> str:
    return "| " + " | ".join(cells) + " |"


def main() -> int:
    parser = argparse.ArgumentParser(description="Render tool A/B spot-check markdown report")
    parser.add_argument("--input", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()

    input_path = Path(args.input)
    output_path = Path(args.output)
    output_path.parent.mkdir(parents=True, exist_ok=True)

    with input_path.open("r", encoding="utf-8") as handle:
        data = json.load(handle)

    run_label = input_path.stem
    if run_label.startswith("tool-ab-spotcheck-"):
        run_label = run_label[len("tool-ab-spotcheck-"):]
    if run_label.endswith(".data"):
        run_label = run_label[: -len(".data")]

    base = data["base"]
    tuned = data["tuned"]
    base_summary = base["summary"]
    tuned_summary = tuned["summary"]
    tools = sorted(data.get("toolsCovered", []))

    base_by_tool = base_summary.get("byTool", {})
    tuned_by_tool = tuned_summary.get("byTool", {})

    both_rows = []
    for b, t in zip(base.get("rows", []), tuned.get("rows", [])):
        merged = {
            "caseId": b.get("caseId"),
            "tool": b.get("expectedTool"),
            "prompt": b.get("userPrompt"),
            "baseEnvelope": b.get("envelopeValid"),
            "baseMatch": b.get("toolMatch"),
            "basePredicted": b.get("predictedTool"),
            "baseError": b.get("parseError"),
            "baseCompletion": b.get("completion"),
            "tunedEnvelope": t.get("envelopeValid"),
            "tunedMatch": t.get("toolMatch"),
            "tunedPredicted": t.get("predictedTool"),
            "tunedError": t.get("parseError"),
            "tunedCompletion": t.get("completion"),
        }
        both_rows.append(merged)

    improved = [r for r in both_rows if (not r["baseMatch"] and r["tunedMatch"]) or (not r["baseEnvelope"] and r["tunedEnvelope"])]
    regressed = [r for r in both_rows if (r["baseMatch"] and not r["tunedMatch"]) or (r["baseEnvelope"] and not r["tunedEnvelope"])]
    persistent_fail = [r for r in both_rows if not r["baseMatch"] and not r["tunedMatch"]]

    lines: list[str] = []
    lines.append(f"# Tool A/B Spot Check - {run_label} (Base vs Tuned)")
    lines.append("")
    lines.append(f"Generated: {datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M:%SZ')}")
    lines.append("")
    lines.append("## Scope")
    lines.append("")
    lines.append("- Manual spot-check battery: 5 prompts per tool across 12 Athena tools (60 total A/B pairs)")
    lines.append(f"- Base model: `{data.get('modelId')}`")
    lines.append(f"- Tuned adapter: `{data.get('adapterPath')}`")
    lines.append(f"- Raw results JSON: `{input_path.as_posix()}`")
    lines.append("")
    lines.append("## Headline Metrics")
    lines.append("")
    lines.append("| Metric | Base | Tuned | Delta |")
    lines.append("| --- | ---: | ---: | ---: |")
    b_total = int(base_summary.get("total", 0))
    t_total = int(tuned_summary.get("total", 0))
    b_env = int(base_summary.get("envelopeValid", 0))
    t_env = int(tuned_summary.get("envelopeValid", 0))
    b_match = int(base_summary.get("toolMatch", 0))
    t_match = int(tuned_summary.get("toolMatch", 0))
    lines.append(f"| Envelope valid | {b_env}/{b_total} ({pct(b_env, b_total)}) | {t_env}/{t_total} ({pct(t_env, t_total)}) | {t_env - b_env:+d} |")
    lines.append(f"| Expected tool match | {b_match}/{b_total} ({pct(b_match, b_total)}) | {t_match}/{t_total} ({pct(t_match, t_total)}) | {t_match - b_match:+d} |")
    lines.append("")
    lines.append("## Per-Tool Results")
    lines.append("")
    lines.append("| Tool | Cases | Base envelope | Base tool match | Tuned envelope | Tuned tool match | Delta envelope | Delta tool match |")
    lines.append("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |")
    for tool in tools:
        lines.append(tool_row(tool, base_by_tool.get(tool, {}), tuned_by_tool.get(tool, {})))

    lines.append("")
    lines.append("## Notable Improvements")
    lines.append("")
    if improved:
        for row in improved[:12]:
            lines.append(
                f"- `{row['caseId']}` ({row['tool']}): base(match={row['baseMatch']}, env={row['baseEnvelope']}, pred={row['basePredicted']}) -> "
                f"tuned(match={row['tunedMatch']}, env={row['tunedEnvelope']}, pred={row['tunedPredicted']}); prompt=\"{brief(row['prompt'], 120)}\""
            )
    else:
        lines.append("- None")

    lines.append("")
    lines.append("## Notable Regressions")
    lines.append("")
    if regressed:
        for row in regressed[:12]:
            lines.append(
                f"- `{row['caseId']}` ({row['tool']}): base(match={row['baseMatch']}, env={row['baseEnvelope']}, pred={row['basePredicted']}) -> "
                f"tuned(match={row['tunedMatch']}, env={row['tunedEnvelope']}, pred={row['tunedPredicted']}); prompt=\"{brief(row['prompt'], 120)}\""
            )
    else:
        lines.append("- None")

    lines.append("")
    lines.append("## Persistent Failures (Both Models)")
    lines.append("")
    if persistent_fail:
        for row in persistent_fail[:15]:
            lines.append(
                f"- `{row['caseId']}` ({row['tool']}): base pred={row['basePredicted']}, tuned pred={row['tunedPredicted']}, "
                f"baseErr={row['baseError']}, tunedErr={row['tunedError']}"
            )
    else:
        lines.append("- None")

    lines.append("")
    lines.append("## Representative Output Snippets")
    lines.append("")
    sample = both_rows[:8]
    for row in sample:
        lines.append(f"### {row['caseId']}")
        lines.append("")
        lines.append(f"- Prompt: {row['prompt']}")
        lines.append(f"- Base: {brief(row['baseCompletion'], 240)}")
        lines.append(f"- Tuned: {brief(row['tunedCompletion'], 240)}")
        lines.append("")

    with output_path.open("w", encoding="utf-8") as handle:
        handle.write("\n".join(lines).rstrip() + "\n")

    print(f"Wrote markdown report: {output_path.as_posix()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
