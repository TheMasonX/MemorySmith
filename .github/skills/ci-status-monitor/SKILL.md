---
name: ci-status-monitor
description: "Monitor CI conservatively with snapshot-first status checks and actionable failure triage."
argument-hint: "Branch/commit and monitoring strictness"
user-invocable: true
disable-model-invocation: false
---

# CI Status Monitor

Inherits from `task-core-loop`.

Use this skill to monitor CI with clear, conservative status handling.

## Policy
- Default to conservative polling.
- Prefer snapshot checks over continuous watch.
- Escalate polling frequency only for active failures or user-requested live monitoring.

## Procedure Additions
1. Apply `task-core-loop` evidence and output discipline.
2. Capture latest CI snapshot via script hook first:
  - `pwsh ./Scripts/SkillHooks/Get-CiSnapshot.ps1 -Branch <branch> -Commit <sha>`
3. Classify state: queued, in_progress, success, failure, stale.
4. For queued/in_progress: report once and continue local work.
5. For failure: pull failing job details and isolate first actionable failure.

## Staleness Heuristic
- Treat long-running in_progress runs as stale only after a conservative threshold.
- Do not spam repeated status calls unless there is a decision to make.

## Output
- Current CI state table.
- Recommended next action.
- Confidence and unresolved risks.