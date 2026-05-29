---
name: training-sprint-loop
description: "Run a repeatable training sprint loop: implement, validate, commit, push, and CI check with clear evidence and scoped commits."
argument-hint: "Task slice, validation scope, and commit intent"
user-invocable: true
disable-model-invocation: false
---

# Training Sprint Loop

Use this skill for short, repeatable delivery rounds where each round ends in a pushed checkpoint and CI status update.

## Use When
- You want commit/push/review/fix/repeat cadence.
- You are iterating on training harness behavior, telemetry contracts, or workbench integration.

## Inputs
- Change slice and intended outcome.
- Validation scope (focused tests, strict GPU run, or both).
- Commit scope (files that must be included/excluded).

## Procedure
1. Audit current branch and CI status.
2. Implement the smallest viable slice.
3. Run narrow validations first; expand only on failure/uncertainty.
4. Verify working tree and stage only scoped files.
5. Commit with a single-purpose message.
6. Push and capture CI run IDs/status.
7. Record evidence, assumptions, risks, and confidence.

## Conservative CI Mode
- Prefer one `gh run list` status refresh per round.
- Avoid frequent polling loops unless user explicitly requests live watch.
- If runs are queued/in-progress, continue next safe local slice instead of aggressive CI polling.

## Done Criteria
- Changes applied and validated for the slice.
- Commit pushed with clean scope.
- CI state captured (queued/in-progress/success/failure).
- Tracker/memory notes updated with evidence.
