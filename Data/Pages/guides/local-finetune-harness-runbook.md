# Local Fine-Tune Harness Runbook

## Purpose

This runbook captures the current in-repo training harness flow for local fine-tuning preparation. It is designed to be runnable in the current Windows workspace and to produce durable run artifacts (`status.json`, `events.jsonl`, benchmark output, and exported dataset lines).

## Command

```powershell
./Scripts/Run-FinetuneHarness.ps1 -RunId sprint3-ft-20260528
```

## Produced Artifacts

- Run request: `runs/sprint3-ft-20260528/request.json`
- Run status: `runs/sprint3-ft-20260528/status.json`
- Run event stream: `runs/sprint3-ft-20260528/events.jsonl`
- Benchmark summary: `runs/sprint3-ft-20260528/benchmark.json`
- Exported SFT dataset: `Data/Training/exports/sprint3-ft-20260528.sft.jsonl`

## Current Benchmark Snapshot

- Records exported: `2`
- Estimated tokens: `90`
- Train mode: `simulated`
- Final simulated loss: `1.6747`
- Total harness wall time: `0.012s`

## Validation Outcome

- Harness phase reached: `done`
- Eval gate status: `passed`
- Status evidence: `runs/sprint3-ft-20260528/status.json`

## Constraints and Next Step

The current `.venv` does not contain a full LoRA stack (`torch`, `transformers`, `datasets`, `trl`, `peft`, `unsloth` are missing), so this run executes export/eval/benchmark with simulated training steps.

Next action for real fine-tuning:

1. Provision a dedicated training environment (recommended: pinned Python 3.11/3.12 venv).
2. Install training dependencies in that environment.
3. Keep using the same bridge contract (`request.json`, `status.json`, JSON event envelopes) so the app-facing orchestration path does not change.
