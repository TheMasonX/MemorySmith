---
name: codebase-audit-sprint-planner
description: 'Run a full deep-dive, evidence-first codebase audit and convert findings into prioritized sprint plans with implementation-ready task documents. Use when you need comprehensive architecture, quality, risk, and delivery planning from current code and docs.'
argument-hint: 'Repository scope, depth, sprint horizon, and target outcomes'
user-invocable: true
disable-model-invocation: false
---

# Codebase Audit to Sprint Plan

Inherits from `task-core-loop`.

Use this skill to perform a comprehensive codebase audit, then distill evidence into an actionable sprint backlog and task documents.

## Outcome

Produce a delivery package with:
- Evidence-backed audit report
- Prioritized issue and opportunity backlog
- Sprint plan with goals, capacity assumptions, and sequencing
- Task documents ready for implementation handoff
- Explicit assumptions, open questions, risks, and confidence percentages
- Escalated critical decisions through /council (or self-simulated council analysis)

## Use When

Use this workflow when you need:
- Full-repository deep dives before major feature work or refactors
- Recovery plans for architecture drift, quality regressions, or unclear priorities
- A roadmap that translates findings into sprint-ready tasks
- Traceable planning grounded in code, tests, configs, and docs

Do not use this workflow for one-file fixes, superficial style reviews, or ad hoc brainstorming without evidence collection.

For high-impact architecture, governance, schema, retrieval/search, or agent-write decisions, invoke /council.

## Inputs

Collect or confirm before running:
- Audit scope: full repo, subdomains, or critical paths
- Timebox and depth: quick, medium, or full deep dive
- Sprint horizon: number of sprints and sprint duration
- Primary planning surface: `/tasks` backed by `Data/Tasks`
- Secondary documentation surface for narrative audit/sprint summaries: `Data/Pages/Tasks/Sprints`
- Team assumptions: capacity, skills, constraints
- Quality gates: tests, benchmarks, security, docs, release checks
- Required output location and naming for audit and planning documents

## Procedure

1. Define scope and acceptance criteria.
Lock what is in and out of scope.
Define mandatory outputs and quality bars before analysis.

2. Build an evidence map.
Gather evidence from:
- Core product docs and plans
- Source code and call chains
- Tests, benchmark artifacts, and validation scripts
- Configuration and deployment paths
- Known incidents, bug patterns, or debt areas

3. Audit by domain.
Run structured analysis across domains:
- Architecture and boundaries
- Data contracts and schema consistency
- Retrieval/search/chat pathways where applicable
- Security, auth, and governance controls
- Test coverage, reliability, and observability
- Build/release/deployment resilience

4. Record findings with severity.
For each finding, capture:
- Evidence and source reference
- Impact and risk surface
- Probable root cause
- Severity: critical, high, medium, low
- Confidence percentage

5. Branch on uncertainty.
If evidence is weak, contradictory, or stale:
- Mark as unresolved
- Define validation tasks to close uncertainty
- Do not promote to high-priority implementation without validation

6. Escalate critical decisions to council review.
For high-impact decisions, run /council before final sprint commitment.
Default to self-simulated council analysis. Use subagents only when the user has given explicit permission in the current request.
If direct council invocation is not feasible, run a self-simulated critical analysis with explicit seats:
- Source-Grounded Archivist
- Data Model Architect
- Retrieval Specialist
- Skeptical Reviewer
- Synthesizer
Each seat must provide recommendation, key risks, assumptions, open questions, and confidence percentage.

7. Convert findings into backlog items.
Translate each accepted finding into a normalized task candidate:
- Prefer creating or updating first-class task records in the `/tasks` system (`Data/Tasks`, `/api/tasks`) over standalone markdown task docs.
- Preserve traceability in each task by linking the relevant page slug, evidence note, or audit finding identifier.
- Problem statement
- Desired outcome
- Scope and non-goals
- Dependencies and blockers
- Estimated size and risk
- Validation plan and definition of done

8. Prioritize and sequence.
Apply prioritization using impact, urgency, dependency depth, and risk reduction.
Sequence tasks into sprint slices that minimize integration risk.

9. Build sprint plans.
For each sprint, define:
- Sprint objective
- Committed items
- Stretch items
- Capacity assumptions
- Exit criteria and demo targets
- Save the sprint narrative under `Data/Pages/Tasks/Sprints` by default unless the caller overrides the location.
- Represent committed implementation work primarily as `/tasks` records and keep the sprint page as the summary/index into those task records.

10. Generate task documents.
Create or update one `/tasks` record per committed item, and add markdown task pages only when longer-form design detail is needed, with:
- Context and rationale
- Implementation approach
- Acceptance criteria
- Test and validation commands
- Rollback and monitoring notes

11. Final consistency and readiness check.
Verify:
- Every sprint item maps to an audit finding or required enabler
- Dependencies are represented
- Risks and open questions are explicit
- Confidence values are realistic
- If in execution mode, planned validation is executable
- If in planning-only discovery mode, deferred validation gates are explicit
- High-impact decision items include council output (invoked or self-simulated) and dissent notes

## Decision Branches

- Branch A: Stability-first vs feature-first planning
If critical reliability or security risk exists, prioritize stabilization work before net-new features.

- Branch B: Refactor now vs defer
Refactor now only when current architecture blocks delivery, quality, or safety. Otherwise prefer narrow, reversible changes.

- Branch C: Monolithic task vs sliced delivery
If uncertainty is high or integration risk is broad, slice into smaller vertical tasks with checkpoints.

- Branch D: Missing measurements
If no objective baseline exists, schedule instrumentation or benchmark tasks before major optimization work.

- Branch E: Planning-only discovery mode
If the request is discovery/planning only, do not require immediate execution of tests or benchmarks. Instead, include explicit deferred validation tasks and gates in sprint scope.

- Branch F: Council-triggered governance review
If a finding affects schema, retrieval behavior, trust/safety boundaries, or long-lived architecture, require /council before final sprint commitment. If unavailable, require self-simulated council output with explicit dissent.

## Completion Checks

The workflow is complete only when all are true:
- Audit findings include source-grounded evidence
- Findings include severity and confidence percentages
- Backlog items are traceable to findings
- Sprint plans include explicit capacity assumptions and exit criteria
- Each committed item has a task record in the `/tasks` system with acceptance criteria and validation; longer markdown task docs are optional supplements
- Open questions and risks are listed with proposed owners or decision gates
- For planning-only mode, each item includes deferred validation gates and execution prerequisites
- High-impact decisions include council analysis output with confidence values and visible dissent

## Output Templates

Use this structure for the final package.

```markdown
# Comprehensive Codebase Audit Report

## Scope
- Included:
- Excluded:
- Timebox:

## Evidence Reviewed
- <doc/code/test references>

## Findings
| ID | Domain | Severity | Confidence | Summary | Evidence |
|---|---|---|---:|---|---|
| F-001 | Architecture | High | 85% | ... | ... |

## Risk Register
- R-001: <risk>, impact, likelihood, mitigation

## Open Questions
- Q-001: <question>, decision owner, due gate
```

```markdown
# Sprint Plan - <Sprint Name>

## Sprint Objective
<one sentence objective>

## Capacity Assumptions
- Team size:
- Effective days:
- Risk buffer:

## Committed Items
- T-001 <title> (estimate, dependency)

## Stretch Items
- T-00X <title>

## Exit Criteria
- <measurable criteria>

## Demo Targets
- <what is demonstrable>

## Task Links
- <task id / key and why it is in the sprint>
```

```markdown
# Task Document - <Task ID> <Task Title>

## Problem Statement

## Desired Outcome

## Scope
- In scope:
- Out of scope:

## Dependencies

## Implementation Plan
1. 
2. 
3. 

## Acceptance Criteria
- 

## Validation
- Commands:
- Tests:
- Observability checks:

## Rollback Plan

## Risks and Unknowns

## Confidence
- Delivery confidence: <percent>
```

```json
{
	"id": "tsk-xxxx-example",
	"title": "Example task title",
	"description": "Problem statement, desired outcome, scope, dependencies, implementation plan, acceptance criteria, validation, rollback, and risks in one durable task record.",
	"status": "Backlog",
	"linkedPages": ["tasks/sprints/example-sprint"],
	"labels": ["audit", "sprint-1"],
	"attachments": [],
	"comments": []
}
```

## Prompt Pattern

Use prompts in this form:

```text
Run a full deep-dive codebase audit for <scope>.
Then convert findings into <N> sprint plans and implementation-ready `/tasks` records, using markdown sprint pages only for narrative summaries or longer design detail.
Use severity, confidence percentages, explicit assumptions, and open questions.
Require evidence links for all high-impact findings.
Invoke /council for high-impact decisions; if unavailable, run a self-simulated critical council analysis.
```

## References

- README.md
- Data/Memories/Core
- MemorySmith.Core/Docs/Plans
- MemorySmith.Core/Docs/Reviews
- MemorySmith.Core/Docs/ProgressReports
- .github/skills/council/SKILL.md (command name: `council`)
