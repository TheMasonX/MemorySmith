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
2. Gather source-of-truth evidence from code, wiki memories, and active docs (MCP tools first).
3. Implement the smallest reversible slice.
4. Run the narrowest validation that can fail.
5. Record evidence, assumptions, confidence, and open questions.
6. Update tracker and any durable memory notes affected by new facts.

## Core Guardrails
- Keep changes minimal and focused to the stated slice.
- Prefer `/tasks` traceability for implementation work.
- Prefer MCP tools over manual file rewrites when tool coverage exists.
- Avoid repetitive polling loops when script-driven waiting or snapshot checks are available.
- Default to in-process analysis; invoke subagents only with explicit user permission in the current request.

## Modular Hooks
- CI snapshot: `pwsh ./Scripts/SkillHooks/Get-CiSnapshot.ps1 -Branch <branch> -Commit <sha>`
- PR bounded wait: `pwsh ./Scripts/SkillHooks/Wait-PrReviewState.ps1 -PullNumber <number> -PollSeconds 60 -TimeoutMinutes 20`
- Task evidence comment payload: `pwsh ./Scripts/SkillHooks/New-TaskEvidenceComment.ps1 ...`
- Council evidence bundle payload: `pwsh ./Scripts/SkillHooks/New-CouncilEvidenceBundle.ps1 ...`

## Core Outputs
- Scope summary.
- Evidence list.
- Validation result.
- Residual risks and next step.
