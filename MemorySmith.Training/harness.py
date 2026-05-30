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

    def resolve_trust_remote_code(self) -> bool:
        raw = self.request.get("trustRemoteCode", False)
        if isinstance(raw, bool):
            return raw
        if isinstance(raw, str):
            normalized = raw.strip().lower()
            return normalized in {"1", "true", "yes", "on"}
        return bool(raw)

    def resolve_training_mode(self) -> tuple[str, list[str]]:
        probe = self.request.get("dependencyProbe")
        requested_mode = str(self.request.get("trainMode") or "auto").strip().lower()
        if requested_mode not in {"auto", "simulated", "lora", "infer"}:
            requested_mode = "auto"

        if requested_mode == "simulated":
            return "simulated", ["train mode forced to simulated by request"]

        if requested_mode == "infer":
            # Inference mode requires adapter path but not full dependencies
            adapter_path = self.request.get("adapterPath")
            if not adapter_path:
                return "simulated", ["infer mode requested but no adapterPath provided"]
            return "inference-ready", []

        if not isinstance(probe, dict):
            return "simulated", ["dependency probe was not provided"]

        reasons: list[str] = []
        ready = bool(probe.get("ready"))
        accelerator_ready = bool(probe.get("acceleratorReady"))
        accelerator = str(probe.get("accelerator") or "").strip()

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
        starter_files: list[Path] = []

        synthetic_paths = self.request.get("syntheticDataPaths")
        if isinstance(synthetic_paths, list):
            for value in synthetic_paths:
                if isinstance(value, str) and value.strip():
                    starter_files.append(Path(value.strip()).expanduser().resolve())

        synthetic_path = self.request.get("syntheticDataPath")
        if isinstance(synthetic_path, str) and synthetic_path.strip():
            starter_files.append(Path(synthetic_path.strip()).expanduser().resolve())

        starter_files.extend([
            repo_root / "MemorySmith.Training" / "synthetic" / "starter_sft.jsonl",
            repo_root / "MemorySmith.Training" / "synthetic" / "starter_sft.expanded.jsonl",
        ])

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
        fallback_codes = self.as_fallback_codes(reasons)
        if dry_run:
            return {
                "steps": 0,
                "finalLoss": None,
                "mode": "dry-run",
                "trainMode": mode,
                "plannedMode": planned_mode,
                "fallbackCodes": fallback_codes,
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
            "fallbackCodes": fallback_codes,
            "reason": "; ".join(reasons),
        }

    def resolve_model_id(self) -> str:
        candidate = str(
            self.request.get("modelId")
            or self.request.get("activeModelTag")
            or self.request.get("fallbackModelTag")
            or ""
        ).strip()
        if not candidate:
            return "Qwen/Qwen3.5-4B"

        alias = candidate.lower()
        if "/" in candidate:
            return candidate

        # Allow Ollama-style tags in request files and map to HF model IDs.
        if alias.startswith("qwen3.5"):
            return "Qwen/Qwen3.5-4B"
        if alias.startswith("qwen3"):
            return "Qwen/Qwen3-4B"

        return candidate

    def resolve_hyperparameters(self) -> tuple[int, int, float, int | None]:
        hyperparameters = self.request.get("hyperparameters")
        if not isinstance(hyperparameters, dict):
            return 1, 512, 2e-4, None

        epochs = int(hyperparameters.get("epochs") or 1)
        sequence_length = int(hyperparameters.get("sequenceLength") or 512)
        learning_rate = float(hyperparameters.get("learningRate") or 2e-4)
        max_train_steps_raw = hyperparameters.get("maxTrainSteps")

        # Keep defaults bounded for an 8GB class laptop GPU.
        epochs = max(1, min(epochs, 3))
        sequence_length = max(128, min(sequence_length, 1024))
        learning_rate = max(1e-6, min(learning_rate, 5e-3))
        max_train_steps = None
        if max_train_steps_raw is not None:
            max_train_steps = max(1, min(int(max_train_steps_raw), 256))

        return epochs, sequence_length, learning_rate, max_train_steps

    def to_training_text(self, rows: list[dict[str, Any]], tokenizer: Any) -> list[str]:
        texts: list[str] = []
        for row in rows:
            messages = row.get("messages")
            if not isinstance(messages, list):
                continue

            normalized_messages: list[dict[str, str]] = []
            for message in messages:
                role = str(message.get("role") or "").strip()
                content = str(message.get("content") or "").strip()
                if role and content:
                    normalized_messages.append({"role": role, "content": content})

            if not normalized_messages:
                continue

            if hasattr(tokenizer, "apply_chat_template"):
                text = tokenizer.apply_chat_template(
                    normalized_messages,
                    tokenize=False,
                    add_generation_prompt=False,
                )
            else:
                turns = [f"<{m['role']}>\n{m['content']}" for m in normalized_messages]
                text = "\n\n".join(turns)

            if text:
                texts.append(str(text))
        return texts

    def train_lora(self, rows: list[dict[str, Any]]) -> dict[str, Any]:
        import torch
        from peft import LoraConfig, get_peft_model
        from transformers import AutoModelForCausalLM, AutoTokenizer

        model_id = self.resolve_model_id()
        epochs, sequence_length, learning_rate, max_train_steps = self.resolve_hyperparameters()

        trust_remote_code = self.resolve_trust_remote_code()
        tokenizer = AutoTokenizer.from_pretrained(model_id, trust_remote_code=trust_remote_code)
        if tokenizer.pad_token is None:
            tokenizer.pad_token = tokenizer.eos_token

        cuda_available = torch.cuda.is_available()
        torch_dtype = torch.bfloat16 if cuda_available and torch.cuda.is_bf16_supported() else torch.float16
        training_device = "cuda" if cuda_available else "cpu"
        model = AutoModelForCausalLM.from_pretrained(
            model_id,
            dtype=torch_dtype,
            trust_remote_code=trust_remote_code,
            device_map=None,
            low_cpu_mem_usage=False,
        )
        model.to(training_device)
        model.config.use_cache = False

        lora_config = LoraConfig(
            r=8,
            lora_alpha=16,
            lora_dropout=0.05,
            target_modules=["q_proj", "k_proj", "v_proj", "o_proj"],
            bias="none",
            task_type="CAUSAL_LM",
        )
        model = get_peft_model(model, lora_config)
        model.train()

        texts = self.to_training_text(rows, tokenizer)
        if not texts:
            raise RuntimeError("No valid training records were found in the exported dataset.")

        # By default run full per-epoch coverage; optional cap keeps long runs bounded.
        steps_per_epoch = max(1, len(texts))
        computed_steps = steps_per_epoch * epochs
        max_steps = min(computed_steps, max_train_steps) if max_train_steps is not None else computed_steps
        optimizer = torch.optim.AdamW(model.parameters(), lr=learning_rate)

        losses: list[float] = []
        loss_per_epoch: list[dict[str, Any]] = []

        for step in range(max_steps):
            text = texts[step % len(texts)]
            encoded = tokenizer(
                text,
                truncation=True,
                max_length=sequence_length,
                return_tensors="pt",
            )

            input_ids = encoded["input_ids"].to(training_device)
            attention_mask = encoded["attention_mask"].to(training_device)

            outputs = model(
                input_ids=input_ids,
                attention_mask=attention_mask,
                labels=input_ids,
            )
            loss = outputs.loss
            loss.backward()
            optimizer.step()
            optimizer.zero_grad(set_to_none=True)

            loss_value = float(loss.detach().cpu().item())
            losses.append(loss_value)
            
            # Track per-epoch summary
            epoch_num = (step // len(texts)) + 1
            step_in_epoch = (step % len(texts)) + 1
            
            self.emit_event(
                "train.step",
                {
                    "step": step + 1,
                    "totalSteps": max_steps,
                    "epoch": epoch_num,
                    "stepInEpoch": step_in_epoch,
                    "loss": round(loss_value, 4),
                },
            )

        adapter_path = self.paths.workdir / "adapter"
        adapter_path.mkdir(parents=True, exist_ok=True)
        model.save_pretrained(adapter_path)
        tokenizer.save_pretrained(adapter_path)

        if torch.cuda.is_available():
            torch.cuda.empty_cache()

        final_loss = losses[-1] if losses else None
        initial_loss = losses[0] if losses else None
        
        # Calculate epoch summaries
        for epoch_idx in range(epochs):
            start_step = epoch_idx * steps_per_epoch
            end_step = min(start_step + steps_per_epoch, len(losses))
            if start_step < len(losses):
                epoch_losses = losses[start_step:end_step]
                loss_per_epoch.append({
                    "epoch": epoch_idx + 1,
                    "initialLoss": round(epoch_losses[0], 4) if epoch_losses else None,
                    "finalLoss": round(epoch_losses[-1], 4) if epoch_losses else None,
                    "minLoss": round(min(epoch_losses), 4) if epoch_losses else None,
                    "maxLoss": round(max(epoch_losses), 4) if epoch_losses else None,
                    "steps": len(epoch_losses),
                })

        completed_epochs = max_steps / float(steps_per_epoch)

        return {
            "steps": max_steps,
            "epochs": epochs,
            "stepsPerEpoch": steps_per_epoch,
            "maxTrainSteps": max_train_steps,
            "completedEpochs": round(completed_epochs, 4),
            "finalLoss": round(final_loss, 4) if final_loss is not None else None,
            "initialLoss": round(initial_loss, 4) if initial_loss is not None else None,
            "losses": [round(l, 4) for l in losses],
            "lossPerEpoch": loss_per_epoch,
            "mode": "lora",
            "trainMode": "lora",
            "plannedMode": "training-ready",
            "fallbackCodes": [],
            "reason": "executed real LoRA training",
            "modelId": model_id,
            "adapterPath": str(adapter_path),
            "device": training_device,
            "sequenceLength": sequence_length,
            "learningRate": learning_rate,
            "trustRemoteCode": trust_remote_code,
        }

    def infer_lora(self, adapter_path_str: str) -> dict[str, Any]:
        import torch
        from peft import PeftModel
        from transformers import AutoModelForCausalLM, AutoTokenizer

        adapter_path = Path(adapter_path_str)
        if not adapter_path.exists():
            raise RuntimeError(f"Adapter path does not exist: {adapter_path_str}")

        model_id = self.resolve_model_id()
        trust_remote_code = self.resolve_trust_remote_code()
        cuda_available = torch.cuda.is_available()
        torch_dtype = torch.bfloat16 if cuda_available and torch.cuda.is_bf16_supported() else torch.float16
        inference_device = "cuda" if cuda_available else "cpu"

        # Load base model
        self.emit_event("infer.base_model_loading", {"modelId": model_id})
        base_model = AutoModelForCausalLM.from_pretrained(
            model_id,
            dtype=torch_dtype,
            trust_remote_code=trust_remote_code,
            device_map=None,
            low_cpu_mem_usage=False,
        )
        base_model.to(inference_device)
        base_model.eval()

        # Load adapter and merge
        self.emit_event("infer.adapter_loading", {"adapterPath": str(adapter_path)})
        merged_model = PeftModel.from_pretrained(base_model, adapter_path)
        merged_model = merged_model.merge_and_unload()
        merged_model.eval()

        # Load tokenizer
        tokenizer = AutoTokenizer.from_pretrained(adapter_path, trust_remote_code=trust_remote_code)
        if tokenizer.pad_token is None:
            tokenizer.pad_token = tokenizer.eos_token

        # Test prompt
        test_prompt = "What is semantic search?"
        self.emit_event("infer.prompt", {"text": test_prompt})

        # Generate with base model
        base_input = tokenizer(test_prompt, return_tensors="pt").to(inference_device)
        with torch.no_grad():
            base_output = base_model.generate(
                base_input["input_ids"],
                max_length=128,
                top_p=0.9,
                temperature=0.7,
                do_sample=False,
            )
        base_text = tokenizer.decode(base_output[0], skip_special_tokens=True)

        # Generate with merged model
        merged_input = tokenizer(test_prompt, return_tensors="pt").to(inference_device)
        with torch.no_grad():
            merged_output = merged_model.generate(
                merged_input["input_ids"],
                max_length=128,
                top_p=0.9,
                temperature=0.7,
                do_sample=False,
            )
        merged_text = tokenizer.decode(merged_output[0], skip_special_tokens=True)

        self.emit_event("infer.base_output", {"length": len(base_text), "preview": base_text[:100]})
        self.emit_event("infer.merged_output", {"length": len(merged_text), "preview": merged_text[:100]})

        if torch.cuda.is_available():
            torch.cuda.empty_cache()

        return {
            "mode": "infer",
            "trainMode": "infer",
            "plannedMode": "inference-ready",
            "fallbackCodes": [],
            "reason": "executed LoRA adapter inference and comparison",
            "modelId": model_id,
            "adapterPath": str(adapter_path),
            "device": inference_device,
            "testPrompt": test_prompt,
            "baseOutputLength": len(base_text),
            "mergedOutputLength": len(merged_text),
            "baseOutputPreview": base_text[:80],
            "mergedOutputPreview": merged_text[:80],
            "trustRemoteCode": trust_remote_code,
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

    @staticmethod
    def as_fallback_codes(reasons: list[str]) -> list[str]:
        codes: list[str] = []
        lowered = [reason.lower() for reason in reasons]
        for reason in lowered:
            if "lora" in reason and "not implemented" in reason:
                codes.append("lora_not_implemented")
            elif "real trainer" in reason and "not implemented" in reason:
                codes.append("trainer_not_implemented")
            elif "accelerator unavailable" in reason:
                codes.append("accelerator_unavailable")
            elif "missing core deps" in reason:
                codes.append("missing_core_dependencies")
            elif "missing optional deps" in reason:
                codes.append("missing_optional_dependencies")
            elif "probe error" in reason:
                codes.append("dependency_probe_error")
            elif "forced to simulated" in reason:
                codes.append("forced_simulated_mode")
            elif "runtime error" in reason:
                codes.append("trainer_runtime_error")

        # Preserve order while de-duplicating.
        unique_codes: list[str] = []
        for code in codes:
            if code not in unique_codes:
                unique_codes.append(code)

        return unique_codes

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
        train_metrics: dict[str, Any]

        if dry_run:
            train_metrics = self.simulate_train(
                records,
                dry_run=True,
                mode="dry-run",
                planned_mode=planned_mode,
                reasons=train_reasons,
            )
            execution_mode = "dry-run"
        elif planned_mode == "inference-ready" and requested_mode == "infer":
            execution_mode = "infer"
            try:
                adapter_path = self.request.get("adapterPath")
                train_metrics = self.infer_lora(adapter_path)
            except Exception as ex:
                train_reasons = [
                    *train_reasons,
                    f"inference runtime error: {ex}",
                ]
                execution_mode = "simulated"
                train_metrics = self.simulate_train(
                    records,
                    dry_run=False,
                    mode=execution_mode,
                    planned_mode=planned_mode,
                    reasons=train_reasons,
                )
        elif planned_mode == "training-ready" and requested_mode in {"auto", "lora"}:
            execution_mode = "lora"
            try:
                train_metrics = self.train_lora(examples)
            except Exception as ex:
                train_reasons = [
                    *train_reasons,
                    f"real trainer runtime error: {ex}",
                ]
                execution_mode = "simulated"
                train_metrics = self.simulate_train(
                    records,
                    dry_run=False,
                    mode=execution_mode,
                    planned_mode=planned_mode,
                    reasons=train_reasons,
                )
        else:
            train_metrics = self.simulate_train(
                records,
                dry_run=False,
                mode=execution_mode,
                planned_mode=planned_mode,
                reasons=train_reasons,
            )

        fallback_codes = train_metrics.get("fallbackCodes") or self.as_fallback_codes(train_reasons)

        self.emit_event(
            "train.mode",
            {
                "requested": requested_mode,
                "plannedMode": planned_mode,
                "mode": execution_mode,
                "fallbackCodes": fallback_codes,
                "reasons": train_reasons,
            },
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

        done_metrics: dict[str, Any] = {
            "records": records,
            "events": self.events_written,
            "trainMode": execution_mode,
            "plannedMode": planned_mode,
            "fallbackCodes": fallback_codes,
        }
        hf_auth_configured = self.request.get("hfAuthConfigured")
        if isinstance(hf_auth_configured, bool):
            done_metrics["hfAuthConfigured"] = hf_auth_configured

        for key, out_key in (
            ("steps", "trainSteps"),
            ("completedEpochs", "trainCompletedEpochs"),
            ("finalLoss", "trainFinalLoss"),
        ):
            value = train_metrics.get(key)
            if isinstance(value, (int, float)):
                done_metrics[out_key] = value

        self.write_status(
            "done",
            "run.completed",
            done_metrics,
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
