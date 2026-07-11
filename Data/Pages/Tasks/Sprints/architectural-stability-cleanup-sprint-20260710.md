# Sprint Plan — Architectural Stability & Legacy Cleanup (2026-07-10)

## Sprint Objective

Sweep accumulated architectural debt, deprecated fallbacks, and dead code paths while closing the highest-confidence audit-synthesis findings. Re-establish a clean foundation for the next feature cycle.

## Capacity Assumptions

- Team size: 1 maintainer with agent-assisted delivery.
- Effective days per sprint: 4.
- Risk buffer: 30% for validation and task/tracker updates.

## Theme: Architectural Stability + Cleanup

The backlog has accumulated 115+ stale items since late May. This sprint focuses on:
- **Implementing all 9 Ready audit-synthesis tasks** (TSK-0293–TSK-0301) — bug fixes, dead-code deletion, security guardrails, doc cleanup
- **Closing the 4 dormant InProgress tasks** — either deliver or return to Backlog with rationale
- **Archiving superseded/deferred Backlog items** — mark rendering expansion, chat response polish, screenshot capture, and other stalled feature families
- **Rolling a new consolidated roadmap** that reflects architectural stability priorities

## Committed Items

### Audit-Synthesis Fixes (9 Ready tasks)

| Task | Priority | Description |
|------|----------|-------------|
| TSK-0293 | High | Fix TreeSitter C# chunking key mismatch (silent fallback to generic chunking) |
| TSK-0294 | High | Scrub dead search tool references from README and wiki guides |
| TSK-0295 | High | Add `TaskStatuses.All` / `TaskPriorities.All` validation sets |
| TSK-0296 | Medium | Consolidate `FixedTimeEquals` into shared helper (3 copies → 1) |
| TSK-0297 | Medium | Delete 10 dead private methods from `ChatServices.cs` |
| TSK-0298 | Medium | Fix training harness `warmupSteps` default and stale docstring |
| TSK-0299 | Medium | Fix SplitThinking, silent exception catch, and validation error clobbering |
| TSK-0300 | High | Add total auth self-lockout guardrail |
| TSK-0301 | Medium | Delete `MemoryIndex` dead code carrying live race risk |

### InProgress Resolution

| Task | Priority | Action |
|------|----------|--------|
| TSK-0042 | High | Continue decompose ChatServices into bounded modules |
| TSK-0201 | High | Assess: deliver or move to Backlog with scope note |
| TSK-0202 | High | Assess: deliver or move to Backlog with scope note |
| TSK-0203 | Medium | Assess: deliver or move to Backlog with scope note |
| TSK-0271 | Medium | Complete removal of `memorysmith_semantic_search` and `memorysmith_unified_search` |

## Stretch Items

### Security & Reliability (3000-series critical/high)

| Task | Priority | Description |
|------|----------|-------------|
| TSK-3001 | Critical | Narrow antiforgery exemptions + add regression tests |
| TSK-3007 | Critical | Enforce OAuth callback state lifecycle and replay protection |
| TSK-3012 | Critical | Sandbox and allowlist content endpoints to prevent traversal |
| TSK-3009 | High | Enforce task field allowlists and completion semantics |
| TSK-3014 | High | Implement proper backpressure for GitHub Copilot streaming |

### Backlog Pruning (archive stale families)

Archive the following stalled feature families to `Archived` with explanatory comments:

- **Screenshot capture** (TSK-0057–0063, TSK-0117) — superseded by CI browser smoke coverage
- **Markdown rendering expansion** (TSK-0075–0090) — deferred since May 24, no movement
- **Chat response quality polish** (TSK-0091–0100) — deferred since May 24, no movement
- **Stale audit trail** — any Backlog item untouched since before 2026-06-10 that is superseded by a Done task

## Exit Criteria

- All 9 Ready tasks are either Done or advanced with evidence.
- Dormant InProgress tasks have a clear disposition (Done, Backlog, or Archived).
- Old sprint plans are archived; new consolidated sprint plan and roadmap are published.
- At least the superseded/deferred task families are archived with rationale.

## Task Links

- Audit synthesis council: `council/audit-synthesis-council-20260705.md`
- Infrastructure audit: `logs/infrastructure-audit-20260710.md`
- New consolidated roadmap: `plans/architectural-stability-roadmap-20260710.md`

## Assumptions

- Behavior preservation takes precedence over maximal cleanup speed.
- Archiving is a status change with a rationale comment, not a deletion.
- The 3000-series tasks were created 2026-07-10 and represent the next wave after the audit-synthesis Ready items.

## Confidence

- Sprint delivery confidence: 82%
