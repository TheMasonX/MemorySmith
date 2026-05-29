---
name: gpu-reality-validation
description: "Prove that training runs are truly GPU-backed and not silently simulated, with artifact-level evidence."
argument-hint: "Run ID, train mode, and expected GPU proof points"
user-invocable: true
disable-model-invocation: false
---

# GPU Reality Validation

Use this skill to validate that a run executed real GPU training and produced expected artifacts.

## Validation Gates
- `trainMode` is `lora` (or intended real mode), not `simulated`.
- `plannedMode` aligns with readiness.
- `fallbackCodes` is empty when real path is expected.
- Device resolves to CUDA in train metrics.
- Adapter artifacts are present.
- Key telemetry fields are present and coherent.

## Procedure
1. Run strict mode with required dependencies enabled.
2. Read `status.json`, `benchmark.json`, and relevant `events.jsonl` lines.
3. Confirm gate results and summarize any drift.
4. If drift exists, classify as runtime, contract, or environment issue and propose next fix slice.

## Output
- Pass/fail by gate.
- Evidence references.
- Confidence percentage and residual risks.
