---
name: training-sprint-loop
description: "Run a repeatable training sprint loop: implement, validate, commit, push, and CI check with clear evidence and scoped commits."
argument-hint: "Task slice, validation scope, and commit intent"
user-invocable: true
disable-model-invocation: false
---

# Training Sprint Loop

Inherits from `task-core-loop`.

Use this skill for short, repeatable training-focused delivery rounds where each round ends in a pushed checkpoint and CI status update.

## Use When
- You want commit/push/review/fix/repeat cadence.
- You are iterating on training harness behavior, telemetry contracts, or workbench integration.

## Inputs
- Change slice and intended outcome.
- Validation scope (focused tests, strict GPU run, or both).
- Commit scope (files that must be included/excluded).

## Procedure Additions
1. Apply all `task-core-loop` steps first.
2. Add strict or focused training validation for the changed slice.
3. Push and capture CI status with conservative checks.
4. Record run-artifact evidence (`status.json`, `benchmark.json`, event traces) when relevant.

## Conservative CI Mode
- Prefer one `gh run list` status refresh per round.
- Avoid frequent polling loops unless user explicitly requests live watch.
- If runs are queued/in-progress, continue next safe local slice instead of aggressive CI polling.

## Done Criteria
- Changes applied and validated for the slice.
- Commit pushed with clean scope.
- CI state captured (queued/in-progress/success/failure).
- Tracker/memory notes updated with evidence.
