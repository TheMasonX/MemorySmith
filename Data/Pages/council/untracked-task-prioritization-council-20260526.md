# Council Review: Untracked Architecture Gaps Versus Active MCP Follow-up

## Decision

Prioritize `ChatToolCatalog` modularization as the enabling architecture task for the active MCP backlog, then run a dedicated route-and-core simplification sequence for `Chat.razor`, `Admin.razor`, and `MemoryApplicationService` rather than continuing to add work to those hotspots opportunistically.

## Evidence Reviewed

- `research/codebase-untracked-task-audit-20260526`
- `research/ultra-codebase-audit-20260524`
- `audits/claude-mcp-benchmark-followup-20260526`
- `TSK-0183`, `TSK-0185`, `TSK-0186`, `TSK-0053`
- Hotspot metrics from 2026-05-26: `Chat.razor` 2511, `Admin.razor` 1663, `MemoryApplicationService.cs` 1168, `ChatToolCatalog.cs` 1161

## Findings

| Seat | Recommendation | Confidence | Blocking concern |
| --- | --- | ---: | --- |
| Source-Grounded Archivist | Add new tasks only where the backlog truly has no owner: route decomposition, memory-core decomposition, and tool-catalog modularization. Update `TSK-0053` instead of cloning a duplicate hygiene task. | 92% | Avoid re-auditing already-tracked 2026-05-26 MCP follow-up work. |
| Data Model Architect | Treat `MemoryApplicationService` as a first-class architecture hotspot because it now owns read, write, diagnostic, and telemetry flows. Split by stable domain boundary, not by method count alone. | 87% | Do not start extraction without characterization tests around context-pack, search envelopes, CRUD, and stats. |
| Retrieval Specialist | Make `TSK-0192` an enabler for `TSK-0185` and `TSK-0186`; search-contract expansion will otherwise keep hard-coding more MCP/search behavior into one already oversized registry file. | 90% | Modularization must preserve tool names, risk tiers, and current output formats exactly. |
| Human Learning Advocate | Decompose `Chat.razor` and `Admin.razor`, but do it after the MCP follow-up sprint so users see stable behavior while the architecture work is prepared and explained. | 79% | If decomposition starts without stable route smoke coverage, the user-facing risk is higher than the maintainability gain. |
| Skeptical Reviewer | Resist adding `Tasks.razor` and `Pages.razor` decomposition tasks today; evidence is strongest for Chat/Admin only. Also keep `TSK-0053` medium priority unless tooling or task imports actively break. | 84% | Do not let architecture cleanup sprawl into a many-surface refactor program with no clear demo. |
| Synthesizer | Sequence work into three sprints: MCP contract/tooling, then route decomposition, then memory-core plus task-contract cleanup. Keep each sprint small enough that validation remains focused and reversible. | 88% | Each sprint needs explicit exit gates so the cleanup does not become open-ended. |

## Synthesis

### Change Now

- Create `TSK-0192` and sequence it with `TSK-0183`, `TSK-0185`, and `TSK-0186`.
- Create `TSK-0189`, `TSK-0190`, and `TSK-0191` so the current backlog finally owns the largest still-untracked architecture hotspots.
- Update `TSK-0053` with today's evidence instead of creating a new duplicate hygiene task.

### Defer

- Do not create `Tasks.razor` or `Pages.razor` decomposition tasks in this pass.
- Do not broaden `TSK-0053` into a full task-id migration program until canonicalization policy and rollback are explicit.

## Dissent

The Human Learning Advocate would prefer deferring all route decomposition until additional route-smoke or browser characterization exists for Chat/Admin, while the Retrieval Specialist argues `TSK-0192` should happen immediately because more MCP feature work is already queued. The council resolves this by sequencing `TSK-0192` first and placing route decomposition in the following sprint rather than the same sprint.

## Acceptance Criteria

- The new untracked-gap tasks exist as first-class `/tasks` records with implementation-ready descriptions.
- The sprint page sequences `TSK-0192` before the pending MCP feature-expansion items.
- `TSK-0053` includes current evidence about mixed JSON shapes and imported legacy ids.
- No new route-decomposition task is created for `Tasks.razor` or `Pages.razor` without fresh evidence.

## Open Questions

- Should `TSK-0192` aim for domain registries only, or also introduce a shared schema/format helper layer in the same slice?
- Does `MemoryApplicationService` want one orchestration facade or multiple independently injected services after extraction?
