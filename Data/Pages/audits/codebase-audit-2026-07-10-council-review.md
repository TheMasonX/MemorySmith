---
title: Codebase Audit & Council Review (2026-07-10)
tags: [audit, council, codebase]
---

**Executive summary**

- Ten parallel subagents audited the repository for safety, correctness, observability, and policy compliance.
- A 6-seat council (Archivist, Data Model, Retrieval, Human Learning, Skeptical, Synthesizer) synthesized findings and produced prioritized actions.
- Immediate priorities: parser/planner boundary, eliminate silent catches, add ActionOutcome plumbing, harden DB migrations, improve adapter error handling, and add CI policy checks.

**Top actions (short)**

1. Harden parser → planner boundary: enforce "parsers never create goals" and add negative tests.
2. Replace silent `catch { }` and `default` drops with structured logging and telemetry.
3. Wire `ActionOutcome` through ToolDispatcher and Agent journal paths.
4. Wrap SQLite migrations in transactions and add retry/backoff for transient errors.
5. Improve adapter-layer error handling (Mineflayer, WebSocket bridge) and ensure correlationId propagation.
6. Add CI stages: dependency vulnerability scan, CodeQL, and task/page validation.

**Recommended next steps**

- Small/urgent patches: logging fixes, parser tests, and CI SCA job.
- Medium: ActionOutcome plumbing, adapter error-handling refactors, and policy harmonization.
- Large: Planner pipeline refactor, retrieval test harness, and full migration framework.

**Evidence & links**

- See council notes and synthesized evidence in the tracking tasks created alongside this report (Tasks created: see task tracker entries created on 2026-07-10).
- Representative files referenced during audit: Agent.Construction/BlueprintParser.cs, Agent.Core/ReplanGovernor.cs, MineflayerAdapter/index.js, MemorySmith.Storage/SqliteMemorySmithDatabase.cs, WebUI.Blazor/Logging/FileChatLogger.cs.

**Notes**

- Retrieval Specialist seat requested a targeted retrieval audit — scheduled as a follow-up task.
- Confidence: 78% (high confidence on logging and parser fixes; moderate on large refactor estimates pending retrieval-audit results).

---

If you want me to open PRs for the small fixes now, or create the task records (done automatically), tell me which to prioritize next.
