---
name: task-core-loop
description: "Shared core workflow for MemorySmith task execution: scope, evidence, minimal-change implementation, validation, and tracking updates."
argument-hint: "Task goal, scope boundaries, and validation target"
user-invocable: false
disable-model-invocation: false
---

# Task Core Loop

Use this internal skill as the shared base for implementation-focused workflows.

## Core Steps
1. Confirm scope and non-goals.
2. Gather source-of-truth evidence from code, wiki memories, and active docs.
3. Implement the smallest reversible slice.
4. Run the narrowest validation that can fail.
5. Record evidence, assumptions, confidence, and open questions.
6. Update tracker and any durable memory notes affected by new facts.

## Core Guardrails
- Keep changes minimal and focused to the stated slice.
- Prefer `/tasks` traceability for implementation work.
- Avoid repetitive polling loops when script-driven waiting or snapshot checks are available.
- Default to in-process analysis; invoke subagents only with explicit user permission in the current request.

## Core Outputs
- Scope summary.
- Evidence list.
- Validation result.
- Residual risks and next step.
