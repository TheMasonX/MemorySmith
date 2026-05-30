#!/usr/bin/env python3
import argparse
import json
import re
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import torch
from peft import PeftModel
from transformers import AutoModelForCausalLM, AutoTokenizer


@dataclass
class EvalCase:
    case_id: str
    tool: str
    user_prompt: str


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def build_cases() -> list[EvalCase]:
    by_tool: dict[str, list[str]] = {
        "memorysmith_unified_search": [
            "search the wiki for kv cache options",
            "find docs for training harness",
            "lookup wiki notes about chat template",
            "search for model profile defaults",
            "find references about code search eta",
        ],
        "memorysmith_hybrid_search": [
            "run a hybrid search for request guard middleware",
            "hybrid search for source bundle auth behavior",
            "use hybrid search to find onnx semantic search notes",
            "hybrid search for maintenance proposal workflow",
            "run hybrid search for task assignee update invariant",
        ],
        "memorysmith_semantic_search": [
            "semantic search for retrieval warning propagation",
            "semantic search for pages lock navigation regression",
            "semantic search for sidebar collapse standard",
            "semantic search for source-link default app open",
            "semantic search for chat context planner",
        ],
        "memorysmith_search": [
            "search memories for exact term TRAIN-001",
            "search for mem id mem_project_001",
            "find literal text RequestGuard",
            "search by tag governance",
            "search for exact key tsk-0228",
        ],
        "memorysmith_context_pack": [
            "pack context for memory mem_ops_009 with backlinks",
            "build context pack for retrieval warning with backlinks",
            "context pack for task governance memories",
            "create context pack from ids mem_a,mem_b",
            "context pack for query chatml template",
        ],
        "memorysmith_get": [
            "show memory mem_project_001",
            "open memory mem_training_001",
            "get memory mem_ops_009",
            "fetch memory mem_onnx_001",
            "load memory mem_task_001",
        ],
        "memorysmith_page_search": [
            "find pages about markdown rendering",
            "search wiki pages for training harness",
            "find page notes about vector search",
            "search pages for request guard middleware",
            "find pages about maintenance revision cycle",
        ],
        "memorysmith_page_get": [
            "open page memory-taxonomy",
            "get page codebase-vector-search-whitepaper",
            "show page training-workbench",
            "fetch page semantic-search",
            "open page wiki-chat-agent",
        ],
        "memorysmith_task_list": [
            "list tasks tagged eval",
            "list all tasks in progress",
            "find tasks assigned to smith",
            "show done tasks tagged training",
            "list blocked tasks",
        ],
        "memorysmith_task_get": [
            "show task TSK-0209",
            "open task TSK-0228",
            "get task tsk-0218",
            "fetch task TSK-0224",
            "load task tsk-0221",
        ],
        "memorysmith_code_search": [
            "run code search for ToolCatalog",
            "code search for BuildIndexCoreAsync",
            "find where training options are defined",
            "locate nav menu model link",
            "find code that parses training events.jsonl",
        ],
        "memorysmith_code_search_status": [
            "status of code search indexing",
            "show code search index status",
            "is code search indexing complete",
            "code index health status please",
            "report current code search status",
        ],
    }

    cases: list[EvalCase] = []
    for tool, prompts in by_tool.items():
        for idx, prompt in enumerate(prompts, start=1):
            cases.append(EvalCase(case_id=f"{tool}-{idx}", tool=tool, user_prompt=prompt))
    return cases


def render_prompt(tokenizer: AutoTokenizer, user_prompt: str) -> str:
    messages = [
        {
            "role": "system",
            "content": (
                "You are Athena, MemorySmith's local wiki assistant. "
                "When a search/retrieval action is requested, respond with exactly one JSON object "
                "in the form {\"toolCalls\":[{\"name\":\"...\",\"arguments\":{...}}]} and no other text."
            ),
        },
        {"role": "user", "content": user_prompt},
    ]

    if hasattr(tokenizer, "apply_chat_template"):
        return tokenizer.apply_chat_template(messages, tokenize=False, add_generation_prompt=True)

    return (
        "System: You are Athena, MemorySmith's local wiki assistant.\n"
        f"User: {user_prompt}\n"
        "Assistant:"
    )


def extract_first_json_object(text: str) -> str | None:
    start = text.find("{")
    if start < 0:
        return None

    depth = 0
    in_string = False
    escape = False
    for i in range(start, len(text)):
        ch = text[i]
        if escape:
            escape = False
            continue
        if ch == "\\":
            escape = True
            continue
        if ch == '"':
            in_string = not in_string
            continue
        if in_string:
            continue
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                return text[start : i + 1]
    return None


def parse_tool_call(output: str) -> tuple[bool, str | None, str | None]:
    text = output.strip()
    # Remove markdown fences if present.
    text = re.sub(r"^```(?:json)?", "", text, flags=re.IGNORECASE).strip()
    text = re.sub(r"```$", "", text).strip()

    json_blob = extract_first_json_object(text)
    if not json_blob:
        return False, None, "No JSON object found"

    try:
        payload = json.loads(json_blob)
    except json.JSONDecodeError as ex:
        return False, None, f"JSON parse error: {ex}"

    if not isinstance(payload, dict):
        return False, None, "Top-level payload is not an object"

    tool_calls = payload.get("toolCalls")
    if not isinstance(tool_calls, list) or not tool_calls:
        return False, None, "toolCalls missing or empty"

    first = tool_calls[0]
    if not isinstance(first, dict):
        return False, None, "toolCalls[0] is not an object"

    name = first.get("name")
    if not isinstance(name, str) or not name.strip():
        return False, None, "toolCalls[0].name missing"

    return True, name.strip(), None


def generate_outputs(
    model: AutoModelForCausalLM,
    tokenizer: AutoTokenizer,
    cases: list[EvalCase],
    max_new_tokens: int,
    batch_size: int,
    label: str,
) -> list[dict[str, Any]]:
    device = next(model.parameters()).device
    rows: list[dict[str, Any]] = []

    prompts = [render_prompt(tokenizer, case.user_prompt) for case in cases]

    for start in range(0, len(cases), batch_size):
        end = min(start + batch_size, len(cases))
        batch_cases = cases[start:end]
        batch_prompts = prompts[start:end]
        print(f"[{utc_now_iso()}] {label}: cases {start + 1}-{end}/{len(cases)}")

        encoded = tokenizer(
            batch_prompts,
            return_tensors="pt",
            padding=True,
            truncation=True,
        ).to(device)
        attention_mask = encoded["attention_mask"]
        input_lengths = attention_mask.sum(dim=1)

        with torch.no_grad():
            output_ids = model.generate(
                **encoded,
                max_new_tokens=max_new_tokens,
                do_sample=False,
                top_p=1.0,
                pad_token_id=tokenizer.eos_token_id,
            )

        for idx, case in enumerate(batch_cases):
            prompt_tokens = int(input_lengths[idx].item())
            generated_ids = output_ids[idx][prompt_tokens:]
            completion = tokenizer.decode(generated_ids, skip_special_tokens=True).strip()

            envelope_ok, predicted_tool, parse_error = parse_tool_call(completion)
            tool_match = predicted_tool == case.tool
            rows.append(
                {
                    "caseId": case.case_id,
                    "expectedTool": case.tool,
                    "userPrompt": case.user_prompt,
                    "completion": completion,
                    "envelopeValid": envelope_ok,
                    "predictedTool": predicted_tool,
                    "toolMatch": tool_match,
                    "parseError": parse_error,
                }
            )

    return rows


def summarize(rows: list[dict[str, Any]]) -> dict[str, Any]:
    by_tool: dict[str, dict[str, Any]] = {}
    for row in rows:
        tool = row["expectedTool"]
        if tool not in by_tool:
            by_tool[tool] = {"count": 0, "envelopeValid": 0, "toolMatch": 0}

        slot = by_tool[tool]
        slot["count"] += 1
        if row["envelopeValid"]:
            slot["envelopeValid"] += 1
        if row["toolMatch"]:
            slot["toolMatch"] += 1

    total = len(rows)
    envelope_valid = sum(1 for row in rows if row["envelopeValid"])
    tool_match = sum(1 for row in rows if row["toolMatch"])
    return {
        "total": total,
        "envelopeValid": envelope_valid,
        "toolMatch": tool_match,
        "envelopeValidRate": round(envelope_valid / max(1, total), 4),
        "toolMatchRate": round(tool_match / max(1, total), 4),
        "byTool": by_tool,
    }


def load_existing_results(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8") as handle:
        data = json.load(handle)

    base = data.get("base")
    if not isinstance(base, dict) or "summary" not in base or "rows" not in base:
        raise SystemExit(f"Base results file is missing required data: {path}")

    return base


def load_base_model(model_id: str) -> tuple[AutoModelForCausalLM, AutoTokenizer]:
    trust_remote_code = False
    dtype = torch.bfloat16 if torch.cuda.is_available() and torch.cuda.is_bf16_supported() else torch.float16
    tokenizer = AutoTokenizer.from_pretrained(model_id, trust_remote_code=trust_remote_code)
    tokenizer.padding_side = "left"
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token

    model = AutoModelForCausalLM.from_pretrained(
        model_id,
        dtype=dtype,
        trust_remote_code=trust_remote_code,
        device_map=None,
        low_cpu_mem_usage=False,
    )
    model.to("cuda" if torch.cuda.is_available() else "cpu")
    model.eval()
    return model, tokenizer


def load_tuned_model(model_id: str, adapter_path: Path) -> tuple[AutoModelForCausalLM, AutoTokenizer]:
    base_model, _ = load_base_model(model_id)
    merged = PeftModel.from_pretrained(base_model, str(adapter_path))
    merged = merged.merge_and_unload()
    merged.eval()

    tokenizer = AutoTokenizer.from_pretrained(str(adapter_path), trust_remote_code=False)
    tokenizer.padding_side = "left"
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token
    return merged, tokenizer


def main() -> int:
    parser = argparse.ArgumentParser(description="Manual A/B tool-call spot check between base and tuned models")
    parser.add_argument("--model-id", default="Qwen/Qwen3.5-4B")
    parser.add_argument("--adapter-path", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--base-results", help="Reuse an existing benchmark JSON file for the base side instead of rerunning the base model.")
    parser.add_argument("--max-new-tokens", type=int, default=96)
    parser.add_argument("--batch-size", type=int, default=8)
    args = parser.parse_args()

    adapter_path = Path(args.adapter_path)
    if not adapter_path.exists():
        raise SystemExit(f"Adapter path does not exist: {adapter_path}")

    output_path = Path(args.output)
    output_path.parent.mkdir(parents=True, exist_ok=True)

    cases = build_cases()

    if args.base_results:
        base_results_path = Path(args.base_results)
        if not base_results_path.exists():
            raise SystemExit(f"Base results file does not exist: {base_results_path}")

        print(f"[{utc_now_iso()}] Reusing base results from {base_results_path}")
        base = load_existing_results(base_results_path)
        base_rows = base["rows"]
        base_summary = base["summary"]
    else:
        print(f"[{utc_now_iso()}] Running base model evaluation ({len(cases)} cases)")
        base_model, base_tokenizer = load_base_model(args.model_id)
        base_rows = generate_outputs(
            base_model,
            base_tokenizer,
            cases,
            args.max_new_tokens,
            args.batch_size,
            "base",
        )
        del base_model
        if torch.cuda.is_available():
            torch.cuda.empty_cache()
        base_summary = summarize(base_rows)

    print(f"[{utc_now_iso()}] Running tuned model evaluation ({len(cases)} cases)")
    tuned_model, tuned_tokenizer = load_tuned_model(args.model_id, adapter_path)
    tuned_rows = generate_outputs(
        tuned_model,
        tuned_tokenizer,
        cases,
        args.max_new_tokens,
        args.batch_size,
        "tuned",
    )
    del tuned_model
    if torch.cuda.is_available():
        torch.cuda.empty_cache()

    payload = {
        "generatedAtUtc": utc_now_iso(),
        "modelId": args.model_id,
        "adapterPath": str(adapter_path),
        "baseSource": str(Path(args.base_results).resolve()) if args.base_results else None,
        "caseCount": len(cases),
        "perToolCases": 5,
        "toolsCovered": sorted({c.tool for c in cases}),
        "base": {
            "summary": base_summary,
            "rows": base_rows,
        },
        "tuned": {
            "summary": summarize(tuned_rows),
            "rows": tuned_rows,
        },
    }

    with output_path.open("w", encoding="utf-8") as handle:
        json.dump(payload, handle, indent=2)

    print(f"[{utc_now_iso()}] Wrote results to {output_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
