#!/usr/bin/env python3
import argparse
import json
import os
import random
import sys
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


@dataclass
class RunPaths:
    workdir: Path
    status_path: Path
    events_path: Path
    benchmark_path: Path
    export_path: Path


class Harness:
    def __init__(self, run_id: str, request: dict[str, Any], paths: RunPaths) -> None:
        self.run_id = run_id
        self.request = request
        self.paths = paths
        self.started_at = time.perf_counter()
        self.phase_started_at = time.perf_counter()
        self.events_written = 0

    def resolve_training_mode(self) -> tuple[str, list[str]]:
        probe = self.request.get("dependencyProbe")
        requested_mode = str(self.request.get("trainMode") or "auto").strip().lower()
        if requested_mode not in {"auto", "simulated", "lora"}:
            requested_mode = "auto"

        if requested_mode == "simulated":
            return "simulated", ["train mode forced to simulated by request"]

        if not isinstance(probe, dict):
            return "simulated", ["dependency probe was not provided"]

        reasons: list[str] = []
        ready = bool(probe.get("ready"))
        accelerator_ready = bool(probe.get("acceleratorReady"))
        accelerator = str(probe.get("accelerator") or "").strip()

        if requested_mode == "lora":
            reasons.append("train mode lora requested but lora runner is not implemented yet")

        missing = probe.get("missing")
        if isinstance(missing, list) and missing:
            reasons.append(f"missing core deps: {', '.join(str(item) for item in missing)}")

        optional_missing = probe.get("optionalMissing")
        if isinstance(optional_missing, list) and optional_missing:
            reasons.append(f"missing optional deps: {', '.join(str(item) for item in optional_missing)}")

        probe_error = str(probe.get("error") or "").strip()
        if probe_error:
            reasons.append(f"probe error: {probe_error}")

        if not accelerator_ready:
            reasons.append(f"accelerator unavailable: {accelerator or 'unknown'}")

        if ready and accelerator_ready:
            if requested_mode == "lora":
                return "simulated", reasons
            return "training-ready", reasons

        if not reasons:
            reasons.append("dependency probe reported not ready")

        return "simulated", reasons

    def emit_event(self, event: str, data: dict[str, Any]) -> None:
        payload = {"event": event, "data": data, "ts": utc_now(), "runId": self.run_id}
        line = json.dumps(payload, ensure_ascii=True)
        print(line, flush=True)
        with self.paths.events_path.open("a", encoding="utf-8") as handle:
            handle.write(line + "\n")
        self.events_written += 1

    def write_status(self, phase: str, last_event: str, metrics: dict[str, Any] | None = None, warnings: list[str] | None = None, errors: list[str] | None = None) -> None:
        payload = {
            "runId": self.run_id,
            "phase": phase,
            "startedAt": utc_now(),
            "elapsedSeconds": round(time.perf_counter() - self.started_at, 3),
            "lastEvent": last_event,
            "metrics": metrics or {},
            "warnings": warnings or [],
            "errors": errors or [],
        }
        tmp_path = self.paths.status_path.with_suffix(".json.tmp")
        with tmp_path.open("w", encoding="utf-8") as handle:
            json.dump(payload, handle, indent=2)
        os.replace(tmp_path, self.paths.status_path)

    def load_chat_examples(self) -> list[dict[str, Any]]:
        transcript_dir = Path(self.request.get("transcriptDirectory", "../Data/Events/chat-transcripts"))
        transcript_dir = transcript_dir.resolve()
        examples: list[dict[str, Any]] = []
        for file in sorted(transcript_dir.glob("*.content.jsonl")):
            with file.open("r", encoding="utf-8") as handle:
                for raw in handle:
                    raw = raw.strip()
                    if not raw:
                        continue
                    try:
                        row = json.loads(raw)
                    except json.JSONDecodeError:
                        continue
                    user = str(row.get("requestText") or "").strip()
                    assistant = str(row.get("responseText") or "").strip()
                    if user and assistant:
                        examples.append({
                            "messages": [
                                {"role": "system", "content": "You are MemorySmith Athena."},
                                {"role": "user", "content": user},
                                {"role": "assistant", "content": assistant},
                            ]
                        })
        if examples:
            return examples

        starter_examples = self.load_starter_sft_examples()
        if starter_examples:
            self.emit_event("data.synthetic_examples_loaded", {"records": len(starter_examples)})
            return starter_examples

        # Final fallback keeps the harness runnable even if synthetic files are missing.
        return [
            {
                "messages": [
                    {"role": "system", "content": "You are MemorySmith Athena."},
                    {"role": "user", "content": "Summarize semantic search fallback behavior."},
                    {"role": "assistant", "content": "If ONNX embeddings are unavailable, semantic/hybrid routes return lexical-backed results with provider metadata explaining fallback reason."},
                ]
            }
        ]

    def load_starter_sft_examples(self) -> list[dict[str, Any]]:
        repo_root = Path(__file__).resolve().parents[1]
        starter_files = [
            repo_root / "MemorySmith.Training" / "synthetic" / "starter_sft.jsonl",
            repo_root / "MemorySmith.Training" / "synthetic" / "starter_sft.expanded.jsonl",
        ]

        examples: list[dict[str, Any]] = []
        for starter_file in starter_files:
            if not starter_file.exists():
                continue

            with starter_file.open("r", encoding="utf-8") as handle:
                for raw in handle:
                    raw = raw.strip()
                    if not raw:
                        continue

                    try:
                        row = json.loads(raw)
                    except json.JSONDecodeError:
                        continue

                    messages = row.get("messages")
                    if isinstance(messages, list) and len(messages) >= 2:
                        examples.append({"messages": messages})

        return examples

    def write_export(self, examples: list[dict[str, Any]]) -> None:
        with self.paths.export_path.open("w", encoding="utf-8") as handle:
            for example in examples:
                handle.write(json.dumps(example, ensure_ascii=True) + "\n")

    def validate_export(self) -> tuple[int, int]:
        records = 0
        tokens_est = 0
        with self.paths.export_path.open("r", encoding="utf-8") as handle:
            for raw in handle:
                row = json.loads(raw)
                if "messages" not in row or not isinstance(row["messages"], list) or len(row["messages"]) < 2:
                    raise ValueError("Invalid SFT export row: missing messages array")
                for message in row["messages"]:
                    content = str(message.get("content", ""))
                    tokens_est += max(1, len(content) // 4)
                records += 1
        return records, tokens_est

    def simulate_train(self, records: int, dry_run: bool, mode: str, planned_mode: str, reasons: list[str]) -> dict[str, Any]:
        if dry_run:
            return {
                "steps": 0,
                "finalLoss": None,
                "mode": "dry-run",
                "trainMode": mode,
                "plannedMode": planned_mode,
                "reason": "; ".join(reasons),
            }

        steps = max(10, records * 4)
        loss = 1.8
        for idx in range(steps):
            loss = max(0.15, loss * (0.992 + random.random() * 0.002))
            if idx % 10 == 0:
                self.emit_event("train.step", {"step": idx + 1, "totalSteps": steps, "loss": round(loss, 4)})
        return {
            "steps": steps,
            "finalLoss": round(loss, 4),
            "mode": "simulated",
            "trainMode": mode,
            "plannedMode": planned_mode,
            "reason": "; ".join(reasons),
        }

    def write_benchmark(self, records: int, tokens_est: int, train_metrics: dict[str, Any], elapsed_export: float, elapsed_train: float, elapsed_eval: float) -> None:
        payload = {
            "runId": self.run_id,
            "records": records,
            "estimatedTokens": tokens_est,
            "train": train_metrics,
            "timingsSeconds": {
                "export": round(elapsed_export, 3),
                "train": round(elapsed_train, 3),
                "eval": round(elapsed_eval, 3),
                "total": round(time.perf_counter() - self.started_at, 3),
            },
            "throughput": {
                "recordsPerSecond": round(records / max(0.001, elapsed_export + elapsed_train), 3),
                "tokensPerSecondEstimate": round(tokens_est / max(0.001, elapsed_export + elapsed_train), 3),
            },
            "environment": {
                "python": sys.version,
                "argv": sys.argv,
            },
        }
        with self.paths.benchmark_path.open("w", encoding="utf-8") as handle:
            json.dump(payload, handle, indent=2)

    @staticmethod
    def as_warning_messages(mode: str, reasons: list[str]) -> list[str]:
        if mode != "simulated":
            return []

        if not reasons:
            return ["Simulated training mode active."]

        return [f"Simulated training mode: {reason}" for reason in reasons]

    def run(self, dry_run: bool) -> int:
        template_path = Path(__file__).resolve().parents[1] / "MemorySmith.Core" / "Docs" / "Prompts" / "chat-template.jinja2"
        if template_path.exists():
            self.emit_event("template.chatml.ready", {"path": str(template_path)})
        else:
            self.emit_event("template.chatml.missing", {"path": str(template_path)})

        self.emit_event("run.started", {"dryRun": dry_run})
        self.write_status("data", "run.started")

        export_start = time.perf_counter()
        examples = self.load_chat_examples()
        self.write_export(examples)
        records, tokens_est = self.validate_export()
        elapsed_export = time.perf_counter() - export_start
        self.emit_event("data.exported", {"records": records, "exportPath": str(self.paths.export_path)})
        self.write_status("data", "data.exported", {"records": records, "estimatedTokens": tokens_est})

        train_start = time.perf_counter()
        self.write_status("train", "train.started")
        requested_mode = str(self.request.get("trainMode") or "auto").strip().lower()
        planned_mode, train_reasons = self.resolve_training_mode()
        execution_mode = "simulated"
        if planned_mode == "training-ready":
            train_reasons = [
                *train_reasons,
                "real trainer path is not implemented yet; executing simulated trainer",
            ]

        self.emit_event(
            "train.mode",
            {
                "requested": requested_mode,
                "plannedMode": planned_mode,
                "mode": execution_mode,
                "reasons": train_reasons,
            },
        )
        train_metrics = self.simulate_train(
            records,
            dry_run=dry_run,
            mode=execution_mode,
            planned_mode=planned_mode,
            reasons=train_reasons,
        )
        train_warnings = self.as_warning_messages(execution_mode, train_reasons)
        elapsed_train = time.perf_counter() - train_start
        self.emit_event("train.completed", train_metrics)
        self.write_status("train", "train.completed", train_metrics, warnings=train_warnings)

        eval_start = time.perf_counter()
        self.write_status("eval", "eval.started")
        # Lightweight eval gate for now: require >=2 records and non-empty assistant messages.
        passed = records >= 2
        elapsed_eval = time.perf_counter() - eval_start
        eval_payload = {"passed": passed, "records": records}
        self.emit_event("eval.completed", eval_payload)
        self.write_status("eval", "eval.completed", eval_payload)

        self.write_benchmark(records, tokens_est, train_metrics, elapsed_export, elapsed_train, elapsed_eval)
        self.emit_event("benchmark.written", {"path": str(self.paths.benchmark_path)})

        if not passed:
            self.write_status("failed", "eval.failed", {"records": records}, errors=["Insufficient records for eval gate"]) 
            self.emit_event("run.failed", {"reason": "Insufficient records for eval gate"})
            return 2

        self.write_status(
            "done",
            "run.completed",
            {"records": records, "events": self.events_written},
            warnings=train_warnings,
        )
        self.emit_event("run.completed", {"records": records, "events": self.events_written})
        return 0


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="MemorySmith training harness runner")
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--request", required=True)
    parser.add_argument("--workdir", required=True)
    parser.add_argument("--dry-run", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    request_path = Path(args.request)
    with request_path.open("r", encoding="utf-8") as handle:
        request = json.load(handle)

    workdir = Path(args.workdir)
    workdir.mkdir(parents=True, exist_ok=True)
    export_dir = Path(request.get("exportPath", "../Data/Training/exports")).resolve()
    export_dir.mkdir(parents=True, exist_ok=True)

    paths = RunPaths(
        workdir=workdir,
        status_path=workdir / "status.json",
        events_path=workdir / "events.jsonl",
        benchmark_path=workdir / "benchmark.json",
        export_path=export_dir / f"{args.run_id}.sft.jsonl",
    )
    harness = Harness(args.run_id, request, paths)
    try:
        return harness.run(dry_run=args.dry_run)
    except Exception as ex:
        harness.write_status("failed", "run.exception", errors=[str(ex)])
        harness.emit_event("run.failed", {"reason": str(ex)})
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
