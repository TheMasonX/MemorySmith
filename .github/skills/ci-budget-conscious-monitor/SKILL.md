---
name: ci-budget-conscious-monitor
description: "Monitor CI conservatively to control token/tool usage while preserving delivery confidence."
argument-hint: "Branch/commit and monitoring strictness"
user-invocable: true
disable-model-invocation: false
---

# CI Budget-Conscious Monitor

Use this skill to monitor CI in a cost-aware way.

## Policy
- Default to conservative polling.
- Prefer snapshot checks over continuous watch.
- Escalate polling frequency only for active failures or user-requested live monitoring.

## Procedure
1. Capture latest CI snapshot by branch and/or head SHA.
2. Map push and PR runs to current commit.
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
