# Local Fine-Tune Harness Runbook

## Purpose

This runbook captures the current in-repo training harness flow for local fine-tuning preparation. It is designed to be runnable in the current Windows workspace and to produce durable run artifacts (`status.json`, `events.jsonl`, benchmark output, and exported dataset lines).

## Command

```powershell
./Scripts/Setup-FinetuneTrainingEnv.ps1 -InstallPythonIfMissing -PersistUserEnvironment
./Scripts/Run-FinetuneHarness.ps1 -RunId sprint3-ft-20260528 -RequireTrainingDependencies
```

Preflight only:

```powershell
./Scripts/Test-FinetuneHarnessPrereqs.ps1
```

Bash bootstrap (future-compatible path for Linux/WSL operators):

```bash
./Scripts/setup-finetune-training-env.sh
```

## Environment Bootstrap

The recommended local training scratch root is `D:\temp\memorysmith-training` on Windows. This keeps heavyweight Hugging Face caches, torch wheels, temporary files, and run artifacts off the repo drive while leaving exported datasets and the final tuned model location under explicit operator control.

`Setup-FinetuneTrainingEnv.ps1` now provisions a dedicated Python 3.12/3.11 venv, installs the core GPU-capable LoRA stack, writes a local override file at `artifacts/MemorySmith.App/appsettings.LocalOverrides.json`, and can persist the scratch/cache environment variables plus `MemorySmith__SettingsOverridePath` for future app launches. Optional `Unsloth` installation is opt-in for Windows workflows.

Key defaults:

- Scratch root: `D:\temp\memorysmith-training` when `D:\temp` exists, otherwise `artifacts/training-scratch`
- Training venv: `<scratch-root>/.venv`
- Runs directory: `<scratch-root>/runs`
- Hugging Face and torch caches: under `<scratch-root>/hf-home` and `<scratch-root>/torch-home`
- Override file: `artifacts/MemorySmith.App/appsettings.LocalOverrides.json`

If you want a dry bootstrap without package installs, run:

```powershell
./Scripts/Setup-FinetuneTrainingEnv.ps1 -SkipDependencyInstall
```

If you want to include optional Unsloth packages during bootstrap, run:

```powershell
./Scripts/Setup-FinetuneTrainingEnv.ps1 -IncludeUnsloth -AllowCpuFallback
```

Operator note: preflight now requires both core dependencies and a visible accelerator before reporting ready.

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

Second run (`sprint8-ft-20260528`) with dependency-preflight wiring produced the same pass gate with explicit simulated-mode warning and artifacts under `runs/sprint8-ft-20260528/`.

## Validation Outcome

- Harness phase reached: `done`
- Eval gate status: `passed`
- Status evidence: `runs/sprint3-ft-20260528/status.json`

## Constraints and Next Step

If the configured training venv is missing the core LoRA stack (`torch`, `transformers`, `datasets`, `trl`, `peft`) or no accelerator is available, the harness executes export/eval/benchmark with simulated training steps. Optional `unsloth` is surfaced separately and does not block base readiness by default. The runner prints this state up-front via preflight and can be configured to fail fast with `-RequireTrainingDependencies`.

Next action for real fine-tuning:

1. Run `./Scripts/Setup-FinetuneTrainingEnv.ps1 -InstallPythonIfMissing -PersistUserEnvironment` on Windows, or `./Scripts/setup-finetune-training-env.sh` on bash-capable hosts.
2. Verify `./Scripts/Test-FinetuneHarnessPrereqs.ps1` reports `Training dependencies: ready` against the configured training venv.
3. Keep using the same bridge contract (`request.json`, `status.json`, JSON event envelopes) so the app-facing orchestration path does not change.
