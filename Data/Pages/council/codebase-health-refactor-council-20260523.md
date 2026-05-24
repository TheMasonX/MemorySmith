# Council Review: Refactor-Now Strategy for Codebase Health

## Decision

Proceed with stability-first refactoring now for the largest complexity hotspots, using small behavior-preserving slices and explicit validation gates before dependency pruning.

## Evidence Reviewed

- Data/Pages/research/codebase-health-audit-20260523.md
- MemorySmith.App/Services/ChatServices.cs
- MemorySmith.App/Services/MaintenanceAgentServices.cs
- MemorySmith.App/Services/TaskDomainService.cs
- MemorySmith.Tests/PagesAndChatTests.cs
- MemorySmith.Core/Docs/Plans/MemorySmith_FinalRefactorDesign_20260507.md
- MemorySmith.App/MemorySmith.App.csproj

## Findings

| Seat | Recommendation | Confidence | Blocking concern |
|---|---|---:|---|
| Source-Grounded Archivist | Refactor now on top two hotspot files; current file size and role concentration are outliers versus project simplification goals. | 93% | Deferral will compound coupling and increase future migration cost. |
| Data Model Architect | Extract explicit contracts and orchestration layers first to improve boundaries before moving implementation details. | 84% | Boundary extraction without contract stabilization can create churn. |
| Retrieval Specialist | Preserve retrieval/search behavior during refactor; add characterization tests around chat and memory-search pathways before moving internals. | 88% | Search regressions may be subtle and not obvious in basic route checks. |
| Skeptical Reviewer | Reject big-bang rewrites; require vertical slices with green tests and diff-limited PRs. | 90% | Large PRs will hide regressions and slow review throughput. |
| Synthesizer | Two-sprint sequence: hotspot decomposition first, then dependency and guardrail cleanup after behavior is stable. | 91% | Must keep momentum while avoiding uncontrolled scope growth. |

## Synthesis

- Change now:
  - Decompose ChatServices and MaintenanceAgentServices by responsibilities.
  - Split monolithic PagesAndChatTests fixture into focused suites.
- Defer with gate:
  - Dependency pruning and architectural guardrail automation after initial decompositions are green.

## Dissent

- Data Model Architect prefers contract-first extraction, while Skeptical Reviewer prefers behavior-slice-first extraction. Resolution: begin with behavior slices that include minimal contract extraction at each seam.

## Acceptance Criteria

- Largest hotspot files are reduced substantially with no behavioral regressions.
- Refactor PRs remain small enough for high-signal review.
- New/refined tests protect search/chat/task critical paths during decomposition.
- Dependency-pruning actions are gated by explicit build/test validation.

## Open Questions

- Which extraction order for ChatServices yields the lowest regression risk?
- How strict should initial file-size guardrails be to avoid false positives?
- Should dependency pruning run as warning-only in Sprint 2 before hard fail?
